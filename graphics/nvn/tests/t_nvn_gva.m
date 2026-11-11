// 池的 GPU 地址（hle_nvn.c「GPU 地址空间」）：公开 NVN 接口 → 真实 Metal 提交 → 读回 CPU 存储。期望值按 Switch 语义手写，不由被测实现推出：
//   真机驱动按 64 KB 大页把池映射进 GPU 地址空间，缓冲区地址 64 KB 对齐，与存储的 CPU 地址无关（通用模式真驱动给十三机兵的池 0x2ba8b0000，存储 CPU 0x71175bb000）；
//   GPU 命令按缓冲区地址 + 偏移读写，落在存储同一偏移；nvnBufferGetAddress = 池缓冲区地址 + 缓冲偏移；同时活着的两个池地址区间不相交。
// 存储故意放在 4 KB 对齐、非 64 KB 对齐处（旧实现直接把 CPU 指针当缓冲区地址，对齐那条红）。
// Switch 2（NVN2，平台位开）：池建在 gsv GPU 可访问内存上，GPU 虚拟地址 = CPU 虚拟地址，缓冲区地址就是存储的 CPU 地址（游戏拿它加偏移直接 CPU 读，#351）；
//   GPU 拷贝按它读写同样落在存储同一偏移，nvnBufferGetAddress = 存储地址 + 缓冲偏移。
#include "nvn_protos.h"
#import <Foundation/Foundation.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern void *h_nvnBootstrapLoader(const char *name);
#include <unistd.h>
uint64_t s2a_npdm_window_need(const char *npdm);   // runtime/src/svc_mem.c：读 NPDM，Switch 2 的 ACID 布局置平台位（nvn2_on）
extern int nvn2_on(void);
static int g_ounce;   // 平台位已开（之后建的池是 Switch 2 池）
static void ounce_on(void) {   // 写一份 Switch 2 布局的 NPDM（ACID 魔数在 ACID +0x300），经原生层读一遍置平台位（同 t_nvn2_procs）
    char path[1024]; const char *td = getenv("TMPDIR"); snprintf(path, sizeof path, "%s/t_nvn_gva_npdm_XXXXXX", td && *td ? td : "/tmp");
    int fd = mkstemp(path); if (fd < 0) { printf("✗ t_nvn_gva：建临时 NPDM 失败\n"); exit(1); }
    static uint8_t npdm[0x80 + 0x400]; memcpy(npdm, "META", 4); uint32_t acid = 0x80; memcpy(npdm + 0x78, &acid, 4); memcpy(npdm + 0x80 + 0x300, "ACID", 4);
    if (write(fd, npdm, sizeof npdm) != (ssize_t)sizeof npdm) { printf("✗ t_nvn_gva：写临时 NPDM 失败\n"); exit(1); }
    close(fd); s2a_npdm_window_need(path); unlink(path); g_ounce = 1;
}
extern int mtl_last_commit_error(void);
static NVNdevice device;
static NVNqueue queue;
static NVNcommandBuffer commands;
static unsigned checks;
static const char *phase = "初始化";
enum { POOL_SIZE = 4 << 20, COMMAND_SIZE = 1 << 20, SRC = 2 << 20, DST = 3 << 20, N = 4096, SKEW = 0x5000 };
#define REQUIRE(x) do { checks++; if (!(x)) { printf("✗ t_nvn_gva：[%s] %s:%d: %s\n", phase, __FILE__, __LINE__, #x); exit(1); } } while (0)
static void *load(const char *name) { void *entry = h_nvnBootstrapLoader(name); REQUIRE(entry != NULL); return entry; }
#define API(name) ((NVNT_##name)load(#name))
typedef struct { NVNmemoryPool pool; uint8_t *mem; int ounce; } pool_box;   // ounce：建池时平台位开着（Switch 2 池）
static void make_pool(pool_box *b) {
    void *raw; REQUIRE(posix_memalign(&raw, 65536, POOL_SIZE + 65536) == 0);
    b->ounce = g_ounce; b->mem = (uint8_t *)raw + SKEW; memset(b->mem, 0, POOL_SIZE);   // 4 KB 对齐、非 64 KB 对齐
    NVNmemoryPoolBuilder pb; API(nvnMemoryPoolBuilderSetDefaults)(&pb); API(nvnMemoryPoolBuilderSetDevice)(&pb, &device);
    API(nvnMemoryPoolBuilderSetStorage)(&pb, b->mem, POOL_SIZE);
    API(nvnMemoryPoolBuilderSetFlags)(&pb, NVN_MEMORY_POOL_FLAGS_CPU_UNCACHED | NVN_MEMORY_POOL_FLAGS_GPU_CACHED);
    REQUIRE(API(nvnMemoryPoolInitialize)(&b->pool, &pb));
}
static void copy_check(pool_box *cmd, pool_box *b, const char *what) {
    phase = what;
    for (int i = 0; i < N; i++) b->mem[SRC + i] = (uint8_t)(i * 7 + 3);
    memset(b->mem + DST, 0xA5, N);
    NVNbufferAddress ba = API(nvnMemoryPoolGetBufferAddress)(&b->pool);
    if (b->ounce) REQUIRE(ba == (uint64_t)(uintptr_t)b->mem); else REQUIRE(ba % 65536 == 0);
    API(nvnCommandBufferAddCommandMemory)(&commands, &cmd->pool, 0, COMMAND_SIZE); API(nvnCommandBufferBeginRecording)(&commands);
    API(nvnCommandBufferCopyBufferToBuffer)(&commands, ba + SRC, ba + DST, N, 0);
    NVNcommandHandle h = API(nvnCommandBufferEndRecording)(&commands); REQUIRE(h != 0);
    API(nvnQueueSubmitCommands)(&queue, 1, &h); API(nvnQueueFinish)(&queue); REQUIRE(mtl_last_commit_error() == 0);
    REQUIRE(memcmp(b->mem + DST, b->mem + SRC, N) == 0);
}
int main(void) {
    @autoreleasepool {
        NVNdeviceBuilder db; API(nvnDeviceBuilderSetDefaults)(&db); REQUIRE(API(nvnDeviceInitialize)(&device, &db));
        NVNqueueBuilder qb; API(nvnQueueBuilderSetDefaults)(&qb); API(nvnQueueBuilderSetDevice)(&qb, &device);
        REQUIRE(API(nvnQueueInitialize)(&queue, &qb)); REQUIRE(API(nvnCommandBufferInitialize)(&commands, &device));
        static uint8_t control[16384]; API(nvnCommandBufferAddControlMemory)(&commands, control, sizeof control);
        static pool_box a, b, c;
        make_pool(&a); make_pool(&b);
        copy_check(&a, &a, "池 A：缓冲区地址 64 KB 对齐，GPU 拷贝落在存储同一偏移");
        copy_check(&a, &b, "池 B：同上（命令内存在池 A）");
        phase = "两池地址区间不相交";
        NVNbufferAddress pa = API(nvnMemoryPoolGetBufferAddress)(&a.pool), pb = API(nvnMemoryPoolGetBufferAddress)(&b.pool);
        REQUIRE(pa + POOL_SIZE <= pb || pb + POOL_SIZE <= pa);
        phase = "nvnBufferGetAddress = 池缓冲区地址 + 偏移";
        NVNbufferBuilder bb; API(nvnBufferBuilderSetDefaults)(&bb); API(nvnBufferBuilderSetDevice)(&bb, &device);
        API(nvnBufferBuilderSetStorage)(&bb, &b.pool, SRC, N); NVNbuffer buf; REQUIRE(API(nvnBufferInitialize)(&buf, &bb));
        REQUIRE(API(nvnBufferGetAddress)(&buf) == pb + SRC);
        API(nvnBufferFinalize)(&buf);
        phase = "Finalize 没建过的缓冲对象：不读对象里的垃圾池指针（密特罗德 生存恐惧 启动崩溃）";
        NVNbuffer junk; memset(&junk, 0x62, sizeof junk); API(nvnBufferFinalize)(&junk); checks++;
        API(nvnMemoryPoolFinalize)(&b.pool); make_pool(&c);
        copy_check(&a, &c, "池 B 释放后新建池 C：照常");
        ounce_on(); phase = "平台位"; REQUIRE(nvn2_on()); static pool_box d;
        make_pool(&d);
        copy_check(&a, &d, "Switch 2 池 D：缓冲区地址 = 存储 CPU 地址，GPU 拷贝落在存储同一偏移");
        phase = "Switch 2：nvnBufferGetAddress = 存储地址 + 偏移";
        API(nvnBufferBuilderSetStorage)(&bb, &d.pool, SRC, N); NVNbuffer buf2; REQUIRE(API(nvnBufferInitialize)(&buf2, &bb));
        REQUIRE(API(nvnBufferGetAddress)(&buf2) == (uint64_t)(uintptr_t)d.mem + SRC);
        API(nvnBufferFinalize)(&buf2);
        copy_check(&d, &a, "Switch 2 池 D 当命令内存、拷 Switch 1 池 A：照常");
        printf("✓ t_nvn_gva：全部通过（%u 项检查）\n", checks);
    }
    return 0;
}

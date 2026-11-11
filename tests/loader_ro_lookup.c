// nn::ro::LookupSymbol 直连跳板（runtime/src/loader.c s2a_ro_lookup_tramp）：合成模块走装载器 relocate，再经游戏导入槽调。
// 期望按直连约定与 Horizon 语义手写（不由实现推出）：
//   ① 直连：游戏导入的 LookupSymbol 不直接绑 sdk 的定义（绑宿主跳板）；
//   ② 直连命中：按名取 vkGetInstanceProcAddr → 回 0、*out = 宿主 Vulkan 层地址，与同名导入槽一致，sdk 原定义没被调；
//   ③ 直连不命中：进 sdk 原定义一次，x0 / x1 原样交给它（它按 name 写 *out），它的结果码原样回来；
//   ④ 通用：导入槽就是 sdk 原定义，跳板不出现；
//   ⑤ sdk 没定义 LookupSymbol：直连不命中回 0xA16、不写 *out。
// 假 sdk 的 LookupSymbol 是本文件的真函数（符号值 = 函数地址 − 模块基址），记调用次数：nnFoo → 写 0x5555、回 0；其余 → 回 0x2A2（与 0xA16 区分，证明结果码原样传回）。
#include "../runtime/src/native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void s2a_loader_test_relocate(module_t *m);

enum { JUMP_SLOT = 1026, STB_GLOBAL = 1, STT_FUNC = 2 };
#define HOST_VK 0x7000ull   // 假的宿主 Vulkan 层里 vkGetInstanceProcAddr 的地址（graphics/nvn/host_adapt.m s2a_vk_entry 的替身）
#define LOOKUP "_ZN2nn2ro12LookupSymbolEPmPKc"

static int fails, g_direct, g_sdk_calls;
static void check(const char *what, int ok) { printf("%s %s\n", ok ? "通" : "不通", what); fails += !ok; }

uint64_t s2a_direct_lookup(const char *name) { return g_direct && !strcmp(name, "vkGetInstanceProcAddr") ? HOST_VK : 0; }   // gfxmode.c 的替身：通用模式恒 0
uint64_t s2a_far_bind(const char *name, uint64_t v) { (void)name; return v; }
void *s2a_libc_lookup(const char *name) { (void)name; return NULL; }
uint64_t s2a_direct_stub(const char *name) { (void)name; return 0; }   // gfxmode.c 的替身：找遍之后的 GL 空实现，本测不涉及
void *sw_xstack_wrap(const char *name, void *fn) { (void)name; return fn; }
int s2a_loader_store64(uint64_t *where, uint64_t v) { *where = v; return 0; }   // svc_mem.c 的装载器写入口（本测不链 svc_mem.c；页可写时生产实现也是直接写）
int s2a_gfx_mode(void) { return g_direct; }
int plat_tsd_init(void) { return 0; }
uint64_t plat_tsd_base(void) { return 0; }
int plat_tsd_offset(uint32_t key, uint64_t *out) { (void)key; if (out) *out = 0; return 0; }
int s2a_window_ok(void) { return 0; }
uint64_t s2a_mem_mod_base(const char *npdm) { (void)npdm; return 0; }
void s2a_window_get(uint64_t *b, uint64_t *s) { if (b) *b = 0; if (s) *s = 0; }
void s2a_widen_load(module_t *m, const char *dir) { (void)m; (void)dir; }
void s2a_cpatch_load(module_t *m, const char *dir) { (void)m; (void)dir; }
void s2a_patch_data_apply(module_t *m) { (void)m; }
void gate_register(uint64_t ret_abs, const char *kind, int arg, uint64_t orig) { (void)ret_abs; (void)kind; (void)arg; (void)orig; }
char _mh_execute_header;

static uint32_t fake_sdk_lookup(uint64_t *out, const char *name) {   // 假 sdk 的 nn::ro::LookupSymbol
    g_sdk_calls++;
    if (!strcmp(name, "nnFoo")) { *out = 0x5555; return 0; }
    return 0x2A2;
}

// 合成模块：names[i] 有定义（defined[i] = 1，值 0x100 + i×0x10）或是导入（0，每个导入一条 JUMP_SLOT，槽在 8×i）
static void make_module(module_t *m, const char *name, const char *const *names, const int *defined, unsigned n) {
    uint8_t *img = aligned_alloc(16, 0x2000); memset(img, 0, 0x2000);
    char *str = (char *)img + 0x1000; unsigned so = 1;
    Elf64_Sym *sym = (Elf64_Sym *)(img + 0x800);
    Elf64_Rela *jmp = (Elf64_Rela *)(img + 0x600); unsigned nj = 0;
    for (unsigned i = 0; i < n; i++) {
        Elf64_Sym *s = &sym[i + 1];
        s->st_name = so; strcpy(str + so, names[i]); so += (unsigned)strlen(names[i]) + 1;
        s->st_info = (STB_GLOBAL << 4) | STT_FUNC;
        if (defined[i]) { s->st_shndx = 1; s->st_value = 0x100 + i * 0x10; }
        else { jmp[nj].r_offset = 8 * i; jmp[nj].r_info = ((uint64_t)(i + 1) << 32) | JUMP_SLOT; jmp[nj].r_addend = 0; nj++; }
    }
    Elf64_Dyn *dyn = (Elf64_Dyn *)(img + 0x200);
    dyn[0].d_tag = 23; dyn[0].d_un.d_ptr = 0x600;
    dyn[1].d_tag = 2; dyn[1].d_un.d_val = nj * sizeof(Elf64_Rela);
    dyn[2].d_tag = 0;
    memset(m, 0, sizeof *m); snprintf(m->name, sizeof m->name, "%s", name);
    m->base = img; m->size = 0x2000; m->dyn = dyn; m->symtab = sym; m->strtab = str; m->nsym = n + 1;
}

typedef uint32_t (*lookup_fn)(uint64_t *out, const char *name);
static uint32_t call(module_t *main_m, const char *name, uint64_t *out) { *out = 0xDEAD; return ((lookup_fn)((uint64_t *)main_m->base)[0])(out, name); }

int main(void) {
    static module_t mods[4]; g_mod = mods; g_mod_cap = 4;   // 模块表按需分配（loader.c mod_table_alloc 只在起局路径调）：本测自备一张小表
    static const char *const sdk_n[] = {LOOKUP, "nnFoo"};                           static const int sdk_d[] = {1, 1};
    static const char *const bare_n[] = {"nnFoo"};                                  static const int bare_d[] = {1};
    static const char *const drv_n[] = {"vkGetInstanceProcAddr", "vkCreateInstance"}; static const int drv_d[] = {1, 1};
    static const char *const main_n[] = {LOOKUP, "vkGetInstanceProcAddr"};         static const int main_d[] = {0, 0};
    module_t main_m, sdk, bare;
    make_module(&sdk, "sdk", sdk_n, sdk_d, 2);
    ((Elf64_Sym *)sdk.symtab)[1].st_value = (uint64_t)fake_sdk_lookup - (uint64_t)sdk.base;   // sdk 的 LookupSymbol = 假 sdk 函数
    make_module(&bare, "sdk", bare_n, bare_d, 1);                                             // 不定义 LookupSymbol 的 sdk
    make_module(&g_mod[1], "subsdk0", drv_n, drv_d, 2);
    make_module(&main_m, "main", main_n, main_d, 2);
    s2a_loader_test_relocate(&sdk); s2a_loader_test_relocate(&bare); s2a_loader_test_relocate(&g_mod[1]);   // 只为建名字哈希（没有重定位）
    uint64_t sdk_fn = (uint64_t)fake_sdk_lookup, drv_vk = (uint64_t)g_mod[1].base + 0x100;
    uint64_t *slot = (uint64_t *)main_m.base, out; uint32_t rc;

    // 直连模式：GPU 驱动模块 subsdk0 不装（只有 sdk）
    g_mod[0] = sdk; g_direct = 1; g_nmod = 1; s2a_loader_test_relocate(&main_m);
    check("① 直连：导入的 LookupSymbol 不直接绑 sdk 原定义", slot[0] && slot[0] != sdk_fn);
    check("直连：同名导入 vkGetInstanceProcAddr 重定位到宿主 Vulkan 层", slot[1] == HOST_VK);
    g_sdk_calls = 0; rc = call(&main_m, "vkGetInstanceProcAddr", &out);
    check("② 直连命中：vkGetInstanceProcAddr → 回 0、宿主 Vulkan 层地址（= 导入槽），sdk 原定义没被调", rc == 0 && out == HOST_VK && out == slot[1] && g_sdk_calls == 0);
    g_sdk_calls = 0; rc = call(&main_m, "nnFoo", &out);
    check("③ 直连不命中：进 sdk 原定义一次，它按 name 写的 *out 与回 0 原样回来", g_sdk_calls == 1 && rc == 0 && out == 0x5555);
    g_sdk_calls = 0; rc = call(&main_m, "vkCreateInstance", &out);
    check("③ 直连不命中：sdk 原定义的结果码 0x2A2 原样回来、不写 *out", g_sdk_calls == 1 && rc == 0x2A2 && out == 0xDEAD);

    // 通用模式：subsdk0 照装，不挂跳板
    g_direct = 0; g_nmod = 2; s2a_loader_test_relocate(&main_m);
    check("④ 通用：导入槽就是 sdk 原定义（跳板不出现）", slot[0] == sdk_fn);
    check("通用：同名导入 vkGetInstanceProcAddr 重定位到 subsdk0 的定义", slot[1] == drv_vk);

    // sdk 没定义 LookupSymbol：直连不命中回 0xA16
    g_mod[0] = bare; g_direct = 1; g_nmod = 1; s2a_loader_test_relocate(&main_m);
    g_sdk_calls = 0; rc = call(&main_m, "nnFoo", &out);
    check("⑤ sdk 无定义：直连不命中 → 0xA16、不写 *out", rc == 0xA16 && out == 0xDEAD && g_sdk_calls == 0);
    rc = call(&main_m, "vkGetInstanceProcAddr", &out);
    check("⑤ sdk 无定义：直连命中照样 → 宿主 Vulkan 层", rc == 0 && out == HOST_VK);

    printf("%s loader_ro_lookup：%d 处不符\n", fails ? "不通" : "通", fails);
    return fails ? 1 : 0;
}

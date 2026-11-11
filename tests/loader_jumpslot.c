// 合成一块 AArch64 ELF64 RELA，走装载器 relocate。
// 期望按 AAELF64：ABS64 / GLOB_DAT = S + A，RELATIVE = base + A，JUMP_SLOT 缺省只存 far_bind(S)，
// S2A_JUMP_SLOT_ADDEND=1 时为 S + A；far_bind 换了桩时加数为 0 存桩、加数非 0 存 S + A（桩没有 +A 这个入口，#743 F-jumpslot-addend-after-far）。弱未定义 JUMP_SLOT 仍是 0。
#include "../runtime/src/native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void s2a_loader_test_relocate(module_t *m);

enum { ABS64 = 257, GLOB_DAT = 1025, JUMP_SLOT = 1026, RELATIVE = 1027 };
enum { STB_LOCAL = 0, STB_GLOBAL = 1, STB_WEAK = 2, STT_FUNC = 2 };

static int fails;
static void check(const char *what, int ok) {
    printf("%s %s\n", ok ? "通" : "不通", what);
    fails += !ok;
}

uint64_t s2a_direct_lookup(const char *name) {
    if (name && !strcmp(name, "imp")) return 0x2000;
    return 0;
}
uint64_t s2a_far_bind(const char *name, uint64_t v) {
    (void)v;
    if (name && !strcmp(name, "fn")) return 0x9000;   // 不看 S：先加再绑会把加数吃掉
    return v;
}
void *s2a_libc_lookup(const char *name) { (void)name; return NULL; }
uint64_t s2a_direct_stub(const char *name) { (void)name; return 0; }   // gfxmode.c 的替身：找遍之后的 GL 空实现，本测不涉及
void *sw_xstack_wrap(const char *name, void *fn) { (void)name; return fn; }
int s2a_loader_store64(uint64_t *where, uint64_t v) { *where = v; return 0; }   // svc_mem.c 的装载器写入口（本测不链 svc_mem.c；页可写时生产实现也是直接写）
int s2a_gfx_mode(void) { return 0; }
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

static Elf64_Rela rela_of(uint64_t off, uint32_t sym, uint32_t type, int64_t addend) {
    Elf64_Rela r;
    r.r_offset = off;
    r.r_info = ((uint64_t)sym << 32) | type;
    r.r_addend = addend;
    return r;
}

int main(void) {
    const char *env = getenv("S2A_JUMP_SLOT_ADDEND");
    int on = env && !strcmp(env, "1");
    uint8_t *img = aligned_alloc(16, 0x1000);
    if (!img) return 2;
    memset(img, 0, 0x1000);
    img[0x100] = 0xC0;

    const char *str = "\0fn\0imp\0weakfn";
    memcpy(img + 0x480, str, 16);

    Elf64_Sym *sym = (Elf64_Sym *)(img + 0x300);
    sym[1].st_name = 1; sym[1].st_info = (STB_LOCAL << 4) | STT_FUNC; sym[1].st_shndx = 1; sym[1].st_value = 0x100;
    sym[2].st_name = 4; sym[2].st_info = (STB_GLOBAL << 4) | STT_FUNC; sym[2].st_shndx = 0;
    sym[3].st_name = 8; sym[3].st_info = (STB_WEAK << 4) | STT_FUNC; sym[3].st_shndx = 0;

    Elf64_Rela *rela = (Elf64_Rela *)(img + 0x500);
    rela[0] = rela_of(5 * 8, 1, GLOB_DAT, 0x30);
    rela[1] = rela_of(6 * 8, 1, ABS64, 4);
    rela[2] = rela_of(7 * 8, 0, RELATIVE, 0x40);
    Elf64_Rela *jmp = (Elf64_Rela *)(img + 0x600);
    jmp[0] = rela_of(0 * 8, 1, JUMP_SLOT, 0);
    jmp[1] = rela_of(1 * 8, 1, JUMP_SLOT, 0x20);
    jmp[2] = rela_of(2 * 8, 1, JUMP_SLOT, -8);
    jmp[3] = rela_of(3 * 8, 2, JUMP_SLOT, 0x11);
    jmp[4] = rela_of(4 * 8, 3, JUMP_SLOT, 0x50);

    Elf64_Dyn *dyn = (Elf64_Dyn *)(img + 0x200);
    dyn[0].d_tag = 7; dyn[0].d_un.d_ptr = 0x500;
    dyn[1].d_tag = 8; dyn[1].d_un.d_val = 3 * sizeof(Elf64_Rela);
    dyn[2].d_tag = 23; dyn[2].d_un.d_ptr = 0x600;
    dyn[3].d_tag = 2; dyn[3].d_un.d_val = 5 * sizeof(Elf64_Rela);
    dyn[4].d_tag = 0;

    module_t m; memset(&m, 0, sizeof m);
    snprintf(m.name, sizeof m.name, "t");
    m.base = img; m.size = 0x1000;
    m.dyn = dyn; m.symtab = sym; m.strtab = (const char *)(img + 0x480); m.nsym = 4;
    s2a_loader_test_relocate(&m);

    uint64_t base = (uint64_t)img;
    uint64_t S = base + 0x100;
    uint64_t bound = 0x9000;   // far_bind("fn") 的结果，与 S 无关
    uint64_t *slot = (uint64_t *)img;
    uint64_t imp_add = on ? 0x11 : 0;

    check(on ? "开关开：加数 0 的 JUMP_SLOT = far_bind(S)（桩）" : "缺省：加数 0 的 JUMP_SLOT = far_bind(S)", slot[0] == bound);
    check(on ? "开关开：换了桩且加数 0x20 → S + 0x20（不跳进桩中间）" : "缺省：非零加数的 JUMP_SLOT 仍只存 far_bind(S)", slot[1] == (on ? S + 0x20 : bound));
    check(on ? "开关开：换了桩且负加数 → S - 8" : "缺省：负加数 JUMP_SLOT 仍只存 far_bind(S)", slot[2] == (on ? S - 8 : bound));
    check(on ? "开关开：没换桩的导入 JUMP_SLOT = 0x2000 + 0x11" : "缺省：导入 JUMP_SLOT 仍是 0x2000", slot[3] == 0x2000 + imp_add);
    check("无定义弱符号 JUMP_SLOT 置 0（加数不参与）", slot[4] == 0);
    check("GLOB_DAT 仍是 S + A，不经 far_bind", slot[5] == S + 0x30);
    check("ABS64 仍是 S + A", slot[6] == S + 4);
    check("RELATIVE 仍是 base + A", slot[7] == base + 0x40);
    if (on) check("非零加数不存「桩 + 加数」", slot[1] != bound + 0x20 && slot[2] != bound - 8);

    free(img);
    printf("%s loader_jumpslot（%s）：%d 处不符\n", fails ? "不通" : "通", on ? "S2A_JUMP_SLOT_ADDEND=1" : "缺省", fails);
    return fails ? 1 : 0;
}

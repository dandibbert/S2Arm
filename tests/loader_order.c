// 装载顺序 = 全局符号绑定优先级（runtime/src/loader.c resolve_global 按 g_mod 序、先装模块的定义胜出）。
//   ① 合成 main / subsdk0 / sdk：subsdk0 与 sdk 都定义默认可见性的 dup，subsdk0 有一条指到自己定义的 JUMP_SLOT。
//      Horizon 序 [main, subsdk0, sdk] → subsdk0 的槽绑 subsdk0 自己（resolve_global 先命中自己）；
//      字母序 [main, sdk, subsdk0] → 同一槽绑 sdk 的副本（11-06 安卓旧包的事故语义：本模块有定义却绑到先装的 sdk）。
//   ② s2a_module_order_ok：main → subsdk0..N（数字序）→ sdk 收；sdk 排到 subsdk 前、main 不在首位、subsdk 非数字序都拒。
//   ③ 找遍之后才给 GL 空实现（#601）：main 导入 glslcCompile（subsdk0 定义）与 glNoOne（谁都不定义）；s2a_direct_stub 替身对 gl* 一律回 STUB。
//      期望 glslcCompile 绑 subsdk0 的定义（游戏模块优先于空实现），glNoOne 绑 STUB；改前空实现在 s2a_direct_lookup 里先给，glslcCompile 会绑 STUB。
#include "../runtime/src/native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void s2a_loader_test_relocate(module_t *m);
int s2a_module_order_ok(const char *const *names, int n);

enum { JUMP_SLOT = 1026, STB_GLOBAL = 1, STT_FUNC = 2 };

static int fails;
static void check(const char *what, int ok) { printf("%s %s\n", ok ? "通" : "不通", what); fails += !ok; }

uint64_t s2a_direct_lookup(const char *name) { (void)name; return 0; }
uint64_t s2a_far_bind(const char *name, uint64_t v) { (void)name; return v; }   // 不换桩：槽里就是解析到的地址
void *s2a_libc_lookup(const char *name) { (void)name; return NULL; }
#define STUB 0x5100u
uint64_t s2a_direct_stub(const char *name) { return !strncmp(name, "gl", 2) ? STUB : 0; }   // gfxmode.c 的替身：gl* 一律给「空实现」
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

// 合成模块：defs[i] 是默认可见性定义（值 0x100 + i×0x10），每个定义再配一条指向自己的 JUMP_SLOT，槽在 8×i
// imps[j]：未定义导入（st_shndx = 0），各配一条 JUMP_SLOT，槽在 8×(ndef + j)
static void make_module_imp(module_t *m, const char *name, const char *const *defs, unsigned ndef, const char *const *imps, unsigned nimp);
static void make_module(module_t *m, const char *name, const char *const *defs, unsigned ndef) { make_module_imp(m, name, defs, ndef, NULL, 0); }
static void make_module_imp(module_t *m, const char *name, const char *const *defs, unsigned ndef, const char *const *imps, unsigned nimp) {
    uint8_t *img = aligned_alloc(16, 0x2000); memset(img, 0, 0x2000);
    char *str = (char *)img + 0x1000; unsigned so = 1;
    Elf64_Sym *sym = (Elf64_Sym *)(img + 0x800);
    Elf64_Rela *jmp = (Elf64_Rela *)(img + 0x600); unsigned nj = 0;
    for (unsigned i = 0; i < ndef; i++) {
        Elf64_Sym *s = &sym[i + 1];
        s->st_name = so; strcpy(str + so, defs[i]); so += (unsigned)strlen(defs[i]) + 1;
        s->st_info = (STB_GLOBAL << 4) | STT_FUNC; s->st_shndx = 1; s->st_value = 0x100 + i * 0x10;
        jmp[nj].r_offset = 8 * i; jmp[nj].r_info = ((uint64_t)(i + 1) << 32) | JUMP_SLOT; jmp[nj].r_addend = 0; nj++;
    }
    for (unsigned j = 0; j < nimp; j++) {
        unsigned k = ndef + j; Elf64_Sym *s = &sym[k + 1];
        s->st_name = so; strcpy(str + so, imps[j]); so += (unsigned)strlen(imps[j]) + 1;
        s->st_info = (STB_GLOBAL << 4) | STT_FUNC; s->st_shndx = 0;
        jmp[nj].r_offset = 8 * k; jmp[nj].r_info = ((uint64_t)(k + 1) << 32) | JUMP_SLOT; jmp[nj].r_addend = 0; nj++;
    }
    Elf64_Dyn *dyn = (Elf64_Dyn *)(img + 0x200);
    dyn[0].d_tag = 23; dyn[0].d_un.d_ptr = 0x600;
    dyn[1].d_tag = 2; dyn[1].d_un.d_val = nj * sizeof(Elf64_Rela);
    dyn[2].d_tag = 0;
    memset(m, 0, sizeof *m); snprintf(m->name, sizeof m->name, "%s", name);
    m->base = img; m->size = 0x2000; m->dyn = dyn; m->symtab = sym; m->strtab = str; m->nsym = ndef + nimp + 1;
}

int main(void) {
    static module_t mods[3]; g_mod = mods; g_mod_cap = 3;   // 模块表按需分配（loader.c mod_table_alloc 只在起局路径调）：本测自备
    static const char *const dup[] = {"dup"};
    module_t main_m, sub, sdk;
    make_module(&main_m, "main", dup, 0);
    make_module(&sub, "subsdk0", dup, 1);   // subsdk0：定义 dup（0x100）+ 对 dup 的 JUMP_SLOT（槽 0）
    make_module(&sdk, "sdk", dup, 1);       // sdk：同名 dup（0x100），地址与 subsdk0 的不同（基址不同）
    s2a_loader_test_relocate(&main_m);      // g_nmod 还是 0：只为建名字哈希（resolve_global 用 module_sym）
    s2a_loader_test_relocate(&sdk);
    s2a_loader_test_relocate(&sub);

    uint64_t sub_dup = (uint64_t)sub.base + 0x100, sdk_dup = (uint64_t)sdk.base + 0x100;
    uint64_t *slot = (uint64_t *)sub.base;

    g_mod[0] = main_m; g_mod[1] = sub; g_mod[2] = sdk; g_nmod = 3;   // Horizon 序
    s2a_loader_test_relocate(&sub);
    check("① Horizon 序 [main, subsdk0, sdk]：subsdk0 的 dup 槽绑 subsdk0 自己", slot[0] == sub_dup);

    g_mod[0] = main_m; g_mod[1] = sdk; g_mod[2] = sub; g_nmod = 3;   // 旧字母序（11-06 事故语义）
    slot[0] = 0;
    s2a_loader_test_relocate(&sub);
    check("① 字母序 [main, sdk, subsdk0]：同一槽绑 sdk 的 dup（先装的赢）", slot[0] == sdk_dup);

    {   // ③ 找遍之后才给 GL 空实现
        static const char *const gdef[] = {"glslcCompile"}, *const gimp[] = {"glslcCompile", "glNoOne"};
        module_t gmain, gsub;
        make_module_imp(&gmain, "main", NULL, 0, gimp, 2);
        make_module(&gsub, "subsdk0", gdef, 1);
        g_nmod = 0;   // 先各建名字哈希（同①）
        s2a_loader_test_relocate(&gmain);
        s2a_loader_test_relocate(&gsub);
        uint64_t *gs = (uint64_t *)gmain.base; gs[0] = gs[1] = 0;
        g_mod[0] = gmain; g_mod[1] = gsub; g_mod[2] = sdk; g_nmod = 3;
        s2a_loader_test_relocate(&gmain);
        check("③ glslcCompile 绑游戏模块 subsdk0 的定义（不被空实现顶掉）", gs[0] == (uint64_t)gsub.base + 0x100);
        check("③ glNoOne 谁都不定义 → 绑空实现", gs[1] == STUB);
    }

    static const char *const ok1[] = {"main", "subsdk0", "subsdk2", "subsdk10", "sdk"};
    static const char *const ok2[] = {"main", "sdk"};
    static const char *const ok3[] = {"main", "subsdk0", "rtld", "sdk"};
    static const char *const bad1[] = {"main", "sdk", "subsdk0"};                 // 旧字母序包
    static const char *const bad2[] = {"sdk", "main", "subsdk0"};                 // main 不在首位
    static const char *const bad3[] = {"main", "subsdk2", "subsdk0", "sdk"};      // subsdk 非数字序
    static const char *const bad4[] = {"main", "subsdk0", "sdk", "rtld"};         // 名单外的名不得排到 sdk 后
    check("② 收 main → subsdk0,2,10 → sdk", s2a_module_order_ok(ok1, 5) == 1);
    check("② 收 main → sdk", s2a_module_order_ok(ok2, 2) == 1);
    check("② 收名单外的名排中间", s2a_module_order_ok(ok3, 4) == 1);
    check("② 拒 sdk 排 subsdk 前", s2a_module_order_ok(bad1, 3) == 0);
    check("② 拒 main 不在首位", s2a_module_order_ok(bad2, 3) == 0);
    check("② 拒 subsdk 非数字序", s2a_module_order_ok(bad3, 4) == 0);
    check("② 拒名单外的名排 sdk 后", s2a_module_order_ok(bad4, 4) == 0);

    printf("%s loader_order：%d 处不符\n", fails ? "不通" : "通", fails);
    return fails ? 1 : 0;
}

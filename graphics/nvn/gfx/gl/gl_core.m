// OpenGL 前端公共部分：对象表、错误码、计数、函数表查找（eglGetProcAddress 与装载器共用）。接口见 docs/研究/GL前端接口.md
#include "gl_int.h"
#include <mach-o/getsect.h>
#include <mach-o/ldsyms.h>

__thread gl_state *gl_cur;   // 按线程（egl.m 文件头）
gl_names gl_buffers, gl_vaos, gl_shaders, gl_programs;
_Atomic uint64_t gl_n_frames, gl_n_draws, gl_n_passes, gl_bad_feat, gl_bad_err;

// ---------- 对象表 ----------
void *gl_name_get(gl_names *t, GLuint name) { return name && name < t->n ? t->v[name] : NULL; }
static void grow(gl_names *t, uint32_t need) {
    if (need <= t->cap) return;
    uint64_t c = t->cap ? t->cap : 64; while (c < need) c *= 2;
    void **v = realloc(t->v, c * sizeof *v); if (!v) die("[gl] 对象表扩容失败（%llu）", (unsigned long long)c);
    memset(v + t->cap, 0, (c - t->cap) * sizeof *v); t->v = v; t->cap = (uint32_t)c;
}
GLuint gl_name_new(gl_names *t, void *obj) {
    if (!obj) { gl_set_error(0x0505); return 0; }   // GL_OUT_OF_MEMORY：不发布空槽，也不扩表
    if (!t->n) t->n = 1;   // 0 号不用（GL 的 0 = 没有对象）
    for (uint32_t i = 1; i < t->n; i++) if (!t->v[i]) { t->v[i] = obj; return i; }
    grow(t, t->n + 1); t->v[t->n] = obj; return t->n++;
}
// 表是按名字下标的稠密数组：Gen 出的名字从 1 连号，绑定自选名字（兼容配置允许）超过 GL_NAME_DENSE_MAX 就拒绝，不为一个大名字开整张表，
//   也不让 name + 1（UINT32_MAX 回绕成 0）和扩容倍增回绕（wwmogu #360 复审指出）
int gl_name_put(gl_names *t, GLuint name, void *obj) {
    if (!name) return 1;
    if (name >= GL_NAME_DENSE_MAX) { GLRED1("对象名字 %u 超过稠密表上限 %u：不建（报 GL_OUT_OF_MEMORY）", name, GL_NAME_DENSE_MAX); return 0; }
    grow(t, name + 1); if (t->n <= name) t->n = name + 1; t->v[name] = obj; return 1;
}
void *gl_name_del(gl_names *t, GLuint name) { void *o = gl_name_get(t, name); if (o) t->v[name] = NULL; return o; }
void gl_names_clear(gl_names *t, void (*destroy)(void *)) {
    for (uint32_t i = 1; i < t->n; i++) if (t->v[i]) destroy(t->v[i]);
    free(t->v); *t = (gl_names){0};
}

// ---------- 错误 ----------
#undef gl_set_error
void gl_set_error(GLenum e) { if (gl_cur && !gl_cur->error) gl_cur->error = e; }
// S2A_GL_ERR_OFF=1（只给诊断局用）：错误照常打日志，但不交给游戏。游戏（如 LÖVE）读到第一个 GL 错误就抛异常退出，后面的缺口永远看不到；
//   关掉后一局就能把所有没实现的入口 / 参数一次列全（11-11 小丑牌：栈参数、纹理换位一个修完才露下一个）。缺省关，关路径只读一次缓存的开关
void gl_set_error_at(GLenum e, const char *fn) {
    static int off = -1; if (off < 0) { const char *v = getenv("S2A_GL_ERR_OFF"); off = v && *v == '1'; if (off) GLOG("【黄】 诊断：S2A_GL_ERR_OFF=1，GL 错误只记日志、不交给游戏"); }
    if (!gl_cur || (gl_cur->error && !off)) return;
    if (!off) gl_cur->error = e;
    static _Atomic int n; if (atomic_fetch_add(&n, 1) < (off ? 4096 : 32)) GLOG("GL 错误 %#x 由 %s 置（%s）", e, fn ? fn : "?", off ? "诊断：没交给游戏" : "游戏 glGetError 会读到");
}
GL(glGetError, GLenum, void) { if (!gl_cur) return 0; GLenum e = gl_cur->error; gl_cur->error = 0; return e; }

// ---------- 对公共部分的导出 ----------
void dm_gl_stats(uint64_t *frames, uint64_t *draws, uint64_t *passes) { *frames = gl_n_frames; *draws = gl_n_draws; *passes = gl_n_passes; }
void dm_gl_bad_draws(uint64_t *feat, uint64_t *err) { *feat = gl_bad_feat; *err = gl_bad_err; }

// ---------- 函数表查找 ----------
// 引用各片的锚（gl_int.h「分片锚」）：静态链接时每片的登记段都进来（used：没人读也留着这些引用）
__attribute__((used)) static const char *const gl_slices[] = {&gl_slice_egl, &gl_slice_gl_buf, &gl_slice_gl_draw, &gl_slice_gl_fb, &gl_slice_gl_prog, &gl_slice_gl_tex};
// __DATA,swgl：GL / GLB 宏登记的（名字 → 实现）。没有的名字回 NULL（GL 规范允许取不到扩展函数），每个名字记一次日志，供普查差集
// 核心函数（名字结尾不是两个以上大写字母的厂商后缀 ARB / EXT / NV …）没实现时不回空：游戏按报的 GL 版本直接调，拿到空指针就跳到 0、
//   iOS 当场按代码签名杀掉（小丑牌 v0.2.6 玩家 iPhone 5 次起局都这样）。交一个预编好的空实现：第一次调用打红点名、回 0，
//   日志直接看出游戏用了哪个，再给真实现。扩展函数照旧回空（GL 允许取不到，程序常按指针判支持）
// 核心名 = 「gl」紧跟大写字母（glBindTexture …）且结尾不是两个以上大写的厂商后缀。「gl」后跟小写的不是 GL 函数：NVN 的 GLSL 编译库
//   glslcInitialize / glslcSetAllocator / glslcCompile … 由游戏自己的模块导出（#601：0100C87006310000，v0.2.8 回归，978191cd 只看「gl」前缀）。
//   空实现按调用来源给（gl_stub）：查表 gl_lookup 只回真实现，装载器在游戏模块 / libc / NRO 都没定义时才要空实现，eglGetProcAddress 查表没有时才要
static int gl_core_name(const char *n) {
    size_t l = strlen(n), u = 0; while (u < l && n[l - 1 - u] >= 'A' && n[l - 1 - u] <= 'Z') u++;
    return u < 2 && !strncmp(n, "gl", 2) && n[2] >= 'A' && n[2] <= 'Z';
}
// 空实现个数 = GL_MISS_N（4096）：GL 4.6 兼容档的全部核心名约 2000+，LÖVE / glad 起局一次查一千多个（小丑牌 v0.2.7：没实现的核心名 854 个），
//   256 个时用完就退回空、照样跳 0。每个空实现是一条带编号的小函数（第一次调用按编号点名），编号用十六进制三位拼出来：gl_miss_abc ↔ 0xabc
enum { GL_MISS_N = 0x1000 };
static const char *g_gl_miss_name[GL_MISS_N];
static __attribute__((noinline)) uint64_t gl_miss_call(int i) {
    static _Atomic uint8_t said[GL_MISS_N]; if (!atomic_exchange(&said[i], 1)) GLOG("【红】 游戏调了没实现的 GL 核心函数 %s（空实现，什么都不做、回 0）", g_gl_miss_name[i]);
    return 0;
}
#define GLM_F(X, Y, Z) static uint64_t gl_miss_##X##Y##Z(void) { return gl_miss_call(0x##X##Y##Z); }
#define GLM_P(X, Y, Z) (void *)gl_miss_##X##Y##Z,
#define GLM_16(M, X, Y) M(X, Y, 0) M(X, Y, 1) M(X, Y, 2) M(X, Y, 3) M(X, Y, 4) M(X, Y, 5) M(X, Y, 6) M(X, Y, 7) M(X, Y, 8) M(X, Y, 9) M(X, Y, a) M(X, Y, b) M(X, Y, c) M(X, Y, d) M(X, Y, e) M(X, Y, f)
#define GLM_256(M, X) GLM_16(M, X, 0) GLM_16(M, X, 1) GLM_16(M, X, 2) GLM_16(M, X, 3) GLM_16(M, X, 4) GLM_16(M, X, 5) GLM_16(M, X, 6) GLM_16(M, X, 7) \
                      GLM_16(M, X, 8) GLM_16(M, X, 9) GLM_16(M, X, a) GLM_16(M, X, b) GLM_16(M, X, c) GLM_16(M, X, d) GLM_16(M, X, e) GLM_16(M, X, f)
#define GLM_4096(M) GLM_256(M, 0) GLM_256(M, 1) GLM_256(M, 2) GLM_256(M, 3) GLM_256(M, 4) GLM_256(M, 5) GLM_256(M, 6) GLM_256(M, 7) \
                    GLM_256(M, 8) GLM_256(M, 9) GLM_256(M, a) GLM_256(M, b) GLM_256(M, c) GLM_256(M, d) GLM_256(M, e) GLM_256(M, f)
GLM_4096(GLM_F)
static void *const g_gl_miss_fn[GL_MISS_N] = { GLM_4096(GLM_P) };
_Static_assert(sizeof g_gl_miss_fn / sizeof *g_gl_miss_fn == GL_MISS_N, "空实现表长");
// 编号空实现用完（GL 全部核心名也到不了 4096，留作兜底）：共用这一个——不读参数、x0 回 0，arm64 下对任意签名的调用都安全；
//   只是调用时点不出名字（lookup 那行已经记了名字），所以没有上限、永不回空
static __attribute__((noinline)) uint64_t gl_miss_shared(void) {
    static _Atomic uint8_t said; if (!atomic_exchange(&said, 1)) GLOG("【红】 游戏调了没实现的 GL 核心函数（编号空实现已用完、走共用空实现，名字见 lookup 行）");
    return 0;
}
void *gl_lookup(const char *name) {
    static const hle_ent *sec; static unsigned long sz;
    if (!name) return NULL;
    if (!sec) sec = (const hle_ent *)getsectiondata(&_mh_execute_header, "__DATA", "swgl", &sz);
    void *fn = NULL;
    for (unsigned long i = 0; i < sz / sizeof *sec && !fn; i++) if (!strcmp(sec[i].name, name)) fn = sec[i].fn;
    static const char *seen[4096]; static int nseen; static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
    pthread_mutex_lock(&mu);
    int known = 0; for (int i = 0; i < nseen; i++) if (!strcmp(seen[i], name)) { known = 1; break; }
    if (!known && nseen < 4096) { seen[nseen++] = strdup(name); GLOG("lookup %s → %s", name, fn ? "宿主实现" : "没有（回空）"); }
    pthread_mutex_unlock(&mu);
    return fn;
}
// 没实现的核心名的安全空实现（同一个名字再要给同一个）；不是核心名回空。只在调用方已经找遍真实现之后才要：
//   eglGetProcAddress（游戏运行时按名要 GL 函数）查表没有时；装载器解析导入时直连层 → 游戏模块 → libc → romfs NRO 都没有定义时（loader.c relocate）。
//   查表（gl_lookup）本身不交空实现：装载器先问直连层，层里交了空实现，游戏模块自己导出的同名函数就永远轮不到（#601 glslc* 的病根）
void *gl_stub(const char *name) {
    if (!name || !gl_core_name(name)) return NULL;
    static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER; static int nmiss;
    pthread_mutex_lock(&mu);
    int i = 0; while (i < nmiss && strcmp(g_gl_miss_name[i], name)) i++;
    const int fresh = i == nmiss;
    if (fresh && nmiss < GL_MISS_N) { g_gl_miss_name[nmiss] = strdup(name); nmiss++; }
    void *fn = i < nmiss ? g_gl_miss_fn[i] : (void *)gl_miss_shared;
    pthread_mutex_unlock(&mu);
    if (fresh) GLOG("stub %s → 没有（核心函数，交%s空实现）", name, i < GL_MISS_N ? "" : "共用");
    return fn;
}
void *sw_xstack_wrap(const char *name, void *fn);   // S2Arm 宿主：在宿主栈上跑的切栈桩（单测给替身）
int gl_is_big(const char *name);
// 交给游戏的入口（装载器解析的导入、eglGetProcAddress 交出的指针同一条路）：查表，重活（swglb 登记的）套切栈桩
void *gl_entry(const char *name) { void *fn = gl_lookup(name); return fn && gl_is_big(name) ? sw_xstack_wrap(name, fn) : fn; }
// eglGetProcAddress 交出的：真实现（同 gl_entry），没有时核心名给安全空实现（游戏按报的 GL 版本直接调，回空就跳到地址 0）
void *gl_entry_or_stub(const char *name) { void *fn = gl_entry(name); return fn ? fn : gl_stub(name); }
// __DATA,swglb：要跑在宿主栈上的名字（适配层交给游戏前套切栈桩）
int gl_is_big(const char *name) {
    static const char *const *sec; static unsigned long sz;
    if (!sec) sec = (const char *const *)getsectiondata(&_mh_execute_header, "__DATA", "swglb", &sz);
    for (unsigned long i = 0; sec && i < sz / sizeof *sec; i++) if (!strcmp(sec[i], name)) return 1;
    return 0;
}

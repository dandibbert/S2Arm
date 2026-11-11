// EGL 与上屏（分片「EGL 与上屏」，#272）：一个显示、一个配置、窗口表面、上下文；交换时提交本帧并交给直连公共后端上屏（mtl_present_ext）。
// 窗口表面 = 3 张颜色贴图轮换 + 1 张深度模板，颜色贴图登记进上屏路径（mtl_ext_tex），存储按 GL 行序（第 0 行在底），上屏时翻转（nvn_be_set_origin_lower_left）
// 表面尺寸：游戏按自己 vi::CreateLayer 的尺寸画、不查询表面（数码宝贝不导入 eglQuerySurface），S2Arm 的 vi 在服务层拿不到该尺寸，
//   表面按 vi 层缺省最大 1920×1080 建；本帧 0 号帧缓冲画到的范围（视口右上角）小于表面时上屏只取这块（同 Switch 合成器只显示层内容）
// 当前上下文 / 表面按线程（EGL 1.5 §3.7.3）：eglMakeCurrent 只绑调用线程，别的线程各绑各的；共享组的对象（缓冲 / 纹理 / 程序等）本层名字表本来就全进程一张，
//   共享上下文因此天然共享（不共享的上下文也落在同一张表里：名字不撞、游戏不靠撞名，只是比规范宽）。删除仍被某线程绑着的上下文 / 表面按规范推迟到最后一个线程松开才真放。
//   11-02 火影忍者 终极风暴羁绊：另一线程 eglCreateContext(share) + 无表面 eglMakeCurrent，原先全进程一个当前上下文，把渲染线程的上下文换成新上下文
//   （帧缓冲绑定 0、无表面），渲染线程下一笔清除落到 0 号帧缓冲报「没有绘制表面」+ 0x502，随后游戏客体代码读空指针崩
#include "gl_int.h"
#include "../nvn/nvn_fps.h"
#include <pthread.h>
#include <time.h>
const char gl_slice_egl = 1;   // 分片锚（gl_core.m 引用：静态链接时整片进来，见 gl_int.h「分片锚」）

enum { EGL_SUCCESS = 0x3000, EGL_NOT_INITIALIZED = 0x3001, EGL_BAD_ACCESS = 0x3002,
    EGL_BAD_ALLOC = 0x3003, EGL_BAD_ATTRIBUTE = 0x3004, EGL_BAD_CONFIG = 0x3005,
    EGL_BAD_CONTEXT = 0x3006, EGL_BAD_DISPLAY = 0x3008, EGL_BAD_MATCH = 0x3009,
    EGL_BAD_PARAMETER = 0x300C, EGL_BAD_SURFACE = 0x300D, EGL_NONE = 0x3038,
    EGL_HEIGHT = 0x3056, EGL_WIDTH = 0x3057, EGL_OPENGL_ES_API = 0x30A0, EGL_OPENGL_API = 0x30A2,
    EGL_PBUFFER_LIMIT = 16384 };
int nvn_be_fps_target(void); int nvn_be_link_active(void); int nvn_be_link_wait_tick(int vsyncs, uint64_t timeout_ns);   // nvn_mtl.m
void s2a_sleep_ns(int64_t ns);   // runtime/src/thread.c：按时睡
void dm_guest_frame_tick(void); double nvn_be_presented_time(uint64_t n); void dm_perf_present(double iv_ms);

#define NSURF 3
typedef struct surface_t { uint32_t w, h, cur, interval, ext_w, ext_h, wid[NSURF], did; __strong id<MTLTexture> col[NSURF], depth; uint64_t serial[NSURF], ticket[NSURF]; double next_swap; int bound, dead, pbuffer, largest; struct surface_t *next; } surface_t;   // next_swap：下一拍（单调钟秒）；wid / did：宽屏核心贴图号（宽屏关 = 0）；w / h 是游戏看到的尺寸，贴图可能被放宽
typedef struct context_t { gl_state st; int bound, dead; struct context_t *next; } context_t;   // bound：绑着它的线程数；dead：已 eglDestroy*、等最后一个线程松开再放
static __thread surface_t *g_draw;     // 本线程的当前绘制表面（MakeCurrent；无表面上下文 = NULL）
static surface_t *g_surfaces;
static context_t *g_contexts;
static pthread_mutex_t g_egl_mu = PTHREAD_MUTEX_INITIALIZER;   // 护表面 / 上下文链表与 bound / dead（多线程各自建上下文、MakeCurrent）
// 游戏自己要的帧率（开发浮层取，runtime/src/hud.c hud_target）：每次交换按交换间隔折算，0 = 交换间隔 0（游戏不锁垂直同步，只受 S2A_FPS 上限）；-1 = 还没交换过（本局不是 GL 款）
static int g_gl_game_fps = -1;
int nvn_gl_game_fps(void) { return __atomic_load_n(&g_gl_game_fps, __ATOMIC_RELAXED); }
static __thread EGLenum g_api = EGL_OPENGL_ES_API;   // 当前 API 与错误码都按线程（EGL 1.5 §3.1 / §3.7）
static __thread EGLint g_err = EGL_SUCCESS;
static _Atomic int g_initialized;
static int display_ready(EGLDisplay d) {
    if (d != (EGLDisplay)1) { g_err = EGL_BAD_DISPLAY; return 0; }
    if (!atomic_load(&g_initialized)) { g_err = EGL_NOT_INITIALIZED; return 0; }
    return 1;
}
// Single actual configuration: RGBA8 + Metal Depth32Float_Stencil8, no MSAA,
// native drawing, pixmaps or EGL texture binding. No conformance claim.
static int config_value(EGLint attr, EGLint *v) {
    switch (attr) {
    case 0x3020: *v = 32; break; // EGL_BUFFER_SIZE
    case 0x3021: case 0x3022: case 0x3023: case 0x3024: *v = 8; break;
    case 0x3025: *v = 32; break; // EGL_DEPTH_SIZE matches actual storage.
    case 0x3026: *v = 8; break;
    case 0x3027: *v = 0x3051; break; // EGL_NON_CONFORMANT_CONFIG
    case 0x3028: *v = 1; break;
    case 0x302A: case 0x302C: *v = EGL_PBUFFER_LIMIT; break;
    case 0x302B: *v = EGL_PBUFFER_LIMIT * EGL_PBUFFER_LIMIT; break;
    case 0x3033: *v = 0x0001 | 0x0004; break; // EGL_PBUFFER_BIT | EGL_WINDOW_BIT
    case 0x3034: *v = EGL_NONE; break; // No transparent pixel matching.
    case 0x303C: *v = INT32_MAX; break; // Existing window interval accepts every nonnegative EGLint.
    case 0x303F: *v = 0x308E; break; // EGL_RGB_BUFFER
    case 0x3040: *v = 0x0004 | 0x0008 | 0x0040; break; // ES2 / OpenGL / ES3; no ES1 fixed pipeline.
    case 0x3029: case 0x302D: case 0x302E: case 0x302F: case 0x3031: case 0x3032:
    case 0x3035: case 0x3036: case 0x3037: case 0x3039: case 0x303A: case 0x303B:
    case 0x303D: case 0x303E: case 0x3042: *v = 0; break;
    default: return 0;
    }
    return 1;
}

id<MTLTexture> gl_surface_color(void) { return g_draw ? g_draw->col[g_draw->cur] : nil; }
void gl_surface_game(uint32_t *w, uint32_t *h) { *w = g_draw ? g_draw->w : 0; *h = g_draw ? g_draw->h : 0; }   // 游戏看到的表面尺寸（eglQuerySurface）；贴图可能按宽屏放宽
id<MTLTexture> gl_surface_depth(void) { return g_draw ? g_draw->depth : nil; }
// 前缓冲 = 上一次交换交给上屏的那张（3 张轮换，交换后 cur 前移一张）；还没交换过没有前缓冲内容
id<MTLTexture> gl_surface_front(void) { return g_draw && g_draw->ticket[(g_draw->cur + NSURF - 1) % NSURF] ? g_draw->col[(g_draw->cur + NSURF - 1) % NSURF] : nil; }
void gl_surface_wid(uint32_t *color, uint32_t *depth) { *color = g_draw ? g_draw->wid[g_draw->cur] : 0; *depth = g_draw ? g_draw->did : 0; }
void gl_surface_extent(GLint x1, GLint y1) {
    if (!g_draw) return;
    uint32_t x = x1 < 0 ? 0 : (uint32_t)x1 > g_draw->w ? g_draw->w : (uint32_t)x1, y = y1 < 0 ? 0 : (uint32_t)y1 > g_draw->h ? g_draw->h : (uint32_t)y1;
    if (x > g_draw->ext_w) g_draw->ext_w = x; if (y > g_draw->ext_h) g_draw->ext_h = y;
}

__attribute__((weak)) void s2a_gl_first_use(void) {}   // graphics/nvn/host_adapt.m：定着色器库路径、起直连异常看门（单测不链宿主，用这个空的）
GLB(eglGetDisplay, EGLDisplay, void *native) { @autoreleasepool { (void)native; s2a_gl_first_use(); return (EGLDisplay)1; }}   // 游戏进 GL 的第一个调用；写日志 / 建目录，走宿主栈
GL(eglInitialize, EGLBoolean, EGLDisplay d, EGLint *major, EGLint *minor) { if (d != (EGLDisplay)1) { g_err = EGL_BAD_DISPLAY; return 0; } atomic_store(&g_initialized, 1); if (major) *major = 1; if (minor) *minor = 5; return 1; }
GL(eglTerminate, EGLBoolean, EGLDisplay d) { if (d != (EGLDisplay)1) { g_err = EGL_BAD_DISPLAY; return 0; } atomic_store(&g_initialized, 0); return 1; }
GL(eglGetError, EGLint, void) { EGLint e = g_err; g_err = EGL_SUCCESS; return e; }
GL(eglBindAPI, EGLBoolean, EGLenum api) {
    if (api != EGL_OPENGL_ES_API && api != EGL_OPENGL_API) { g_err = EGL_BAD_PARAMETER; return 0; }
    g_api = api; return 1;
}
GL(eglQueryAPI, EGLenum, void) { return g_api; }
GL(eglQueryString, const char *, EGLDisplay d, EGLint name) {
    // EGL 1.5 §3.3 permits client version/extensions without a display.
    if (!d && (name == 0x3054 || name == 0x3055)) return name == 0x3054 ? "1.5 S2Arm" : "";
    if (!display_ready(d)) return NULL;
    switch (name) {
    case 0x3053: return "S2Arm";
    case 0x3054: return "1.5 S2Arm";
    case 0x3055: return ""; // Do not advertise unimplemented KHR context attributes / sync extensions.
    case 0x308D: return "OpenGL OpenGL_ES";
    default: g_err = EGL_BAD_PARAMETER; return NULL;
    }
}
GL(eglGetConfigAttrib, EGLBoolean, EGLDisplay d, EGLConfig c, EGLint attr, EGLint *v) {
    if (!display_ready(d)) return 0;
    if (c != (EGLConfig)1) { g_err = EGL_BAD_CONFIG; return 0; }
    if (!v) { g_err = EGL_BAD_PARAMETER; return 0; }
    if (!config_value(attr, v)) { g_err = EGL_BAD_ATTRIBUTE; return 0; }
    return 1;
}
// 只有一个配置；配置的查询值以 config_value 为准。
GL(eglChooseConfig, EGLBoolean, EGLDisplay d, const EGLint *attrib, EGLConfig *cfg, EGLint size, EGLint *num) {
    (void)d; (void)attrib; if (num) *num = 1; if (cfg && size > 0) cfg[0] = (EGLConfig)1; return 1;
}
GL(eglGetConfigs, EGLBoolean, EGLDisplay d, EGLConfig *cfg, EGLint size, EGLint *num) { (void)d; if (num) *num = 1; if (cfg && size > 0) cfg[0] = (EGLConfig)1; return 1; }
GLB(eglCreateWindowSurface, EGLSurface, EGLDisplay d, EGLConfig c, EGLNativeWindowType win, const EGLint *attrib) { @autoreleasepool {
    (void)d; (void)c; (void)win; (void)attrib; gl_dev_init();
    surface_t *s = calloc(1, sizeof *s); if (!s) die("[gl] 表面分配失败");
    pthread_mutex_lock(&g_egl_mu); s->next = g_surfaces; g_surfaces = s; pthread_mutex_unlock(&g_egl_mu);
    s->w = 1920; s->h = 1080; s->interval = 1;
    // 宽屏（gl_widen.inc）：表面贴图建的时候就知道是渲染目标，按核心给的宽建；游戏看到的仍是 s->w × s->h（关时一样宽）
    int wd = gl_wd_on() > 0; uint32_t tw = s->w, th = s->h;   // 窄屏加高：表面高按核心给的高（gl_widen.inc gl_wd_rt）
    @autoreleasepool {
        for (int i = 0; i < NSURF; i++) {
            if (wd) { s->wid[i] = gl_wd_new_id(); tw = gl_wd_rt(s->wid[i], s->w, s->h, &th); }
            MTLTextureDescriptor *td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:tw height:th mipmapped:NO];
            td.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead; td.storageMode = MTLStorageModePrivate;
            s->col[i] = [gl_dev newTextureWithDescriptor:td]; s->serial[i] = mtl_ext_tex((__bridge void *)s->col[i]);
        }
        if (wd) { s->did = gl_wd_new_id(); tw = gl_wd_rt(s->did, s->w, s->h, &th); }
        MTLTextureDescriptor *dd = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatDepth32Float_Stencil8 width:tw height:th mipmapped:NO];
        dd.usage = MTLTextureUsageRenderTarget; dd.storageMode = MTLStorageModePrivate; s->depth = [gl_dev newTextureWithDescriptor:dd];
    }
    nvn_be_set_origin_lower_left(1);   // 存储按 GL 行序：上屏翻转
    GLOG("窗口表面 %ux%u，%d 张颜色贴图轮换%s", s->w, s->h, NSURF, tw != s->w || th != s->h ? [NSString stringWithFormat:@"（宽屏放宽到 %ux%u）", tw, th].UTF8String : "");
    return s;
}}
static void surface_unlink(surface_t *s) {   // 持 g_egl_mu
    surface_t **slot = &g_surfaces; while (*slot && *slot != s) slot = &(*slot)->next;
    if (*slot) *slot = s->next;
}
static void surface_free(surface_t *s, int wait) {   // 已摘链、没有线程绑着
    for (int i = 0; i < NSURF; i++) {
        if (wait && s->ticket[i]) nvn_be_present_wait(s->ticket[i]);
        s->col[i] = nil; gl_wd_release_id(s->wid[i]);
    }
    gl_wd_release_id(s->did); s->depth = nil;
    free(s);
}
GLB(eglCreatePbufferSurface, EGLSurface, EGLDisplay d, EGLConfig c, const EGLint *attrib) { @autoreleasepool {
    if (!display_ready(d)) return NULL;
    if (c != (EGLConfig)1) { g_err = EGL_BAD_CONFIG; return NULL; }
    EGLint w = 0, h = 0, largest = 0, format = 0x305C, target = 0x305C;
    if (attrib) for (const EGLint *a = attrib; a[0] != EGL_NONE; a += 2) {
        switch (a[0]) {
        case EGL_WIDTH: w = a[1]; break;
        case EGL_HEIGHT: h = a[1]; break;
        case 0x3058: if (a[1] != 0 && a[1] != 1) { g_err = EGL_BAD_ATTRIBUTE; return NULL; } largest = a[1]; break;
        case 0x3080: if (a[1] < 0x305C || a[1] > 0x305E) { g_err = EGL_BAD_ATTRIBUTE; return NULL; } format = a[1]; break;
        case 0x3081: if (a[1] != 0x305C && a[1] != 0x305F) { g_err = EGL_BAD_ATTRIBUTE; return NULL; } target = a[1]; break;
        case 0x3082: if (a[1] != 0 && a[1] != 1) { g_err = EGL_BAD_ATTRIBUTE; return NULL; } break; // No texture: mipmap attribute has no effect.
        case 0x309D: if (a[1] != 0x308A) { g_err = EGL_BAD_MATCH; return NULL; } break; // EGL_GL_COLORSPACE_LINEAR
        default: g_err = EGL_BAD_ATTRIBUTE; return NULL;
        }
    }
    if (w < 0 || h < 0) { g_err = EGL_BAD_PARAMETER; return NULL; }
    // EGL_BIND_TO_TEXTURE_RGB/RGBA are false: never return a fake texture-backed pbuffer.
    if (format != 0x305C || target != 0x305C) { g_err = EGL_BAD_MATCH; return NULL; }
    if ((w > EGL_PBUFFER_LIMIT || h > EGL_PBUFFER_LIMIT) && !largest) { g_err = EGL_BAD_ALLOC; return NULL; }
    if (w > EGL_PBUFFER_LIMIT) w = EGL_PBUFFER_LIMIT;
    if (h > EGL_PBUFFER_LIMIT) h = EGL_PBUFFER_LIMIT;
    surface_t *s = calloc(1, sizeof *s); if (!s) { g_err = EGL_BAD_ALLOC; return NULL; }
    s->pbuffer = 1; s->largest = largest; s->w = (uint32_t)w; s->h = (uint32_t)h;
    gl_dev_init();
    // Pbuffers have no present registration, widening IDs or swapchain. A legal
    // zero-sized surface keeps minimal Metal backing while exposing size zero.
    for (;;) {
        MTLTextureDescriptor *td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm width:MAX(s->w, 1u) height:MAX(s->h, 1u) mipmapped:NO];
        td.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead; td.storageMode = MTLStorageModePrivate;
        s->col[0] = [gl_dev newTextureWithDescriptor:td];
        td.pixelFormat = MTLPixelFormatDepth32Float_Stencil8; td.usage = MTLTextureUsageRenderTarget;
        s->depth = [gl_dev newTextureWithDescriptor:td];
        if (s->col[0] && s->depth) break;
        s->col[0] = nil; s->depth = nil;
        if (!largest || (s->w <= 1 && s->h <= 1)) { surface_free(s, 0); g_err = EGL_BAD_ALLOC; return NULL; }
        if (s->w >= s->h && s->w > 1) s->w /= 2; else s->h /= 2;
    }
    pthread_mutex_lock(&g_egl_mu); s->next = g_surfaces; g_surfaces = s; pthread_mutex_unlock(&g_egl_mu);
    return s;
}}
GLB(eglDestroySurface, EGLBoolean, EGLDisplay d, EGLSurface sf) { @autoreleasepool {
    (void)d; surface_t *s = sf; if (!s) { g_err = EGL_BAD_SURFACE; return 0; }
    pthread_mutex_lock(&g_egl_mu);
    if (s->dead) { pthread_mutex_unlock(&g_egl_mu); g_err = EGL_BAD_SURFACE; return 0; }
    s->dead = 1; int now = !s->bound; if (now) surface_unlink(s);   // 还被某线程绑着：规范是松开时才放（EGL 1.5 §3.5.6）
    pthread_mutex_unlock(&g_egl_mu);
    if (now) surface_free(s, 1);
    return 1;
}}
GL(eglQuerySurface, EGLBoolean, EGLDisplay d, EGLSurface sf, EGLint attr, EGLint *v) {
    (void)d; surface_t *s = sf; if (!s) { g_err = EGL_BAD_SURFACE; return 0; }
    if (!v) { g_err = EGL_BAD_PARAMETER; return 0; }
    if (attr == EGL_WIDTH) *v = (EGLint)s->w; else if (attr == EGL_HEIGHT) *v = (EGLint)s->h;
    else if (s->pbuffer && attr == 0x3058) *v = s->largest;
    else if (s->pbuffer && (attr == 0x3080 || attr == 0x3081)) *v = 0x305C; // EGL_NO_TEXTURE
    else if (s->pbuffer && (attr == 0x3082 || attr == 0x3083)) *v = 0;
    else { g_err = EGL_BAD_ATTRIBUTE; return 0; }
    return 1;
}
GL(eglCreateContext, EGLContext, EGLDisplay d, EGLConfig c, EGLContext share, const EGLint *attrib) {
    (void)d; (void)c; (void)attrib;
    if (share && ((context_t *)share)->dead) { g_err = EGL_BAD_CONTEXT; return NULL; }   // 共享组：对象名字表全进程一张（文件头），无须另建
    context_t *x = calloc(1, sizeof *x); if (!x) die("[gl] 上下文分配失败");
    x->st.api = g_api; x->st.depth_mask = 1; x->st.default_draw[0] = 0x0405; /* GL_BACK */
    for (int i = 0; i < GL_COLOR_TARGETS; i++) {
        gl_color_state *color = &x->st.color[i];
        for (int k = 0; k < 4; k++) color->mask[k] = 1;
        color->eq[0] = color->eq[1] = 0x8006; /* GL_FUNC_ADD */
        color->factor[0] = color->factor[2] = 1; /* GL_ONE；目标因子与常量色初值为 0 */
    }
    x->st.stencil_wmask[0] = x->st.stencil_wmask[1] = ~0u;
    for (int i = 0; i < 2; i++) { x->st.stencil_func[i] = 0x0207; /* GL_ALWAYS */ x->st.stencil_mask[i] = ~0u; x->st.stencil_op[i][0] = x->st.stencil_op[i][1] = x->st.stencil_op[i][2] = 0x1E00; /* GL_KEEP */ }
    x->st.depth_range[1] = 1; x->st.clear_depth = 1; x->st.depth_func = 0x0201; /* GL_LESS */ x->st.cull = 0x0405; /* GL_BACK */ x->st.front = 0x0901; /* GL_CCW */
    x->st.unpack_align = 4; x->st.unpack_row = 0; x->st.pack_align = 4; x->st.line_width = 1; x->st.point_size = 1; // #272：每个上下文独立的 GL 初始像素存储。
    x->st.default_read = 0x0405; /* GL_BACK */ x->st.poly_mode = 0x1B02; /* GL_FILL */
    for (int i = 0; i < 16; i++) x->st.attr_cur[i][3] = 1;   // 当前通用顶点属性初值 (0, 0, 0, 1)
    x->st.dirty = ~0u;
    pthread_mutex_lock(&g_egl_mu); x->next = g_contexts; g_contexts = x; pthread_mutex_unlock(&g_egl_mu);
    return x;
}
static void context_unlink(context_t *c) {   // 持 g_egl_mu
    context_t **slot = &g_contexts; while (*slot && *slot != c) slot = &(*slot)->next;
    if (*slot) *slot = c->next;
}
static void context_free(context_t *c) { gl_tex_context_drop(&c->st); free(c); }   // 已摘链、没有线程绑着；宿主栈
static context_t *cur_context(void) { return gl_cur ? (context_t *)((uint8_t *)gl_cur - offsetof(context_t, st)) : NULL; }
GLB(eglDestroyContext, EGLBoolean, EGLDisplay d, EGLContext c) { @autoreleasepool {
    (void)d; context_t *x = c; if (!x) { g_err = EGL_BAD_CONTEXT; return 0; }
    pthread_mutex_lock(&g_egl_mu);
    if (x->dead) { pthread_mutex_unlock(&g_egl_mu); g_err = EGL_BAD_CONTEXT; return 0; }
    x->dead = 1; int now = !x->bound; if (now) context_unlink(x);   // 还是某线程的当前上下文：照常可用，松开时才放（EGL 1.5 §3.7.2）
    pthread_mutex_unlock(&g_egl_mu);
    if (now) context_free(x);
    return 1;
}}
void gl_egl_session_reset(void) {
    // The shared present backend is already drained; its texture registry is
    // cleared by the common graphics shutdown after these references are dropped.
    // 客体线程都已退出：不管还绑着与否全部放掉（调用线程自己的绑定也清）
    while (g_surfaces) { surface_t *s = g_surfaces; g_surfaces = s->next; surface_free(s, 0); }
    while (g_contexts) { context_t *c = g_contexts; g_contexts = c->next; context_free(c); }
    g_draw = NULL; gl_cur = NULL; g_api = EGL_OPENGL_ES_API; g_err = EGL_SUCCESS;
    atomic_store(&g_initialized, 0);
    g_gl_game_fps = -1;
}
// 换绑本线程：新的先记绑定、旧的松开；松开后没人绑、又已删的当场放（宿主栈：放贴图 / 等上屏）
static void bind_current(surface_t *draw, context_t *c) {
    surface_t *os = g_draw; context_t *oc = cur_context(), *fc = NULL; surface_t *fs = NULL;
    pthread_mutex_lock(&g_egl_mu);
    if (c) c->bound++; if (draw) draw->bound++;
    if (oc && !--oc->bound && oc->dead) { context_unlink(oc); fc = oc; }
    if (os && !--os->bound && os->dead) { surface_unlink(os); fs = os; }
    pthread_mutex_unlock(&g_egl_mu);
    g_draw = draw; gl_cur = c ? &c->st : NULL;
    if (fc) context_free(fc);
    if (fs) surface_free(fs, 1);
}
// draw = NULL 且 c 非空：无表面上下文（EGL_KHR_surfaceless_context，游戏只画自己的帧缓冲对象；0 号帧缓冲不存在）
GLB(eglMakeCurrent, EGLBoolean, EGLDisplay d, EGLSurface draw, EGLSurface read, EGLContext c) { @autoreleasepool {
    (void)d; if (read != draw) { g_err = EGL_BAD_MATCH; return 0; }
    context_t *x = c; surface_t *s = draw;
    if (x && x != cur_context() && x->dead) { g_err = EGL_BAD_CONTEXT; return 0; }   // 已删的上下文不能新绑（还绑着的线程照用）
    if (s && s != g_draw && s->dead) { g_err = EGL_BAD_SURFACE; return 0; }
    bind_current(s, x);
    if (gl_cur && g_draw && !gl_cur->vp[2]) { gl_cur->vp[2] = gl_cur->sc[2] = (GLint)g_draw->w; gl_cur->vp[3] = gl_cur->sc[3] = (GLint)g_draw->h; }   // 第一次绑表面：视口 / 裁剪 = 整个表面（规范）
    return 1;
}}
GL(eglGetCurrentContext, EGLContext, void) { return cur_context(); }
GLB(eglWaitGL, EGLBoolean, void) { @autoreleasepool {
    // EGL 1.5 §3.8: waits the ES context, regardless of the currently bound API.
    // OpenGL and ES share the one current GL context slot in this frontend.
    if (!gl_cur || gl_cur->api != EGL_OPENGL_ES_API) return 1;
    if (gl_session_stopping()) { g_err = EGL_BAD_ACCESS; return 0; }
    uint64_t v = gl_commit(); gl_wait(v);
    if (gl_session_stopping() || gl_ev.signaledValue < v) { g_err = EGL_BAD_ACCESS; return 0; }
    return 1;
}}
GL(eglWaitNative, EGLBoolean, EGLint engine) {
    if (engine != 0x305B) { g_err = EGL_BAD_PARAMETER; return 0; } // EGL_CORE_NATIVE_ENGINE
    // No supported surface shares its color storage with native rendering.
    // The specified no-op (including without a current context), not a GPU wait.
    return 1;
}
GL(eglSwapInterval, EGLBoolean, EGLDisplay d, EGLint n) { (void)d; if (g_draw) g_draw->interval = n < 0 ? 0 : (uint32_t)n; return 1; }
// S2Arm 标称帧率（host_adapt.m 在 dm_guest_frame_tick 里取，即 eglSwapBuffers 的线程）：出图实际跟的节拍 = 目标帧率 / 交换间隔（间隔 0 = 不等垂直同步，上屏仍按目标帧率的最短显示时长）；
//   还没有表面交换过回 0（这局不是 GL 款）
int gl_present_fps(void) { return g_draw && atomic_load(&gl_n_frames) ? nvn_be_fps_target() / (int)(g_draw->interval ? g_draw->interval : 1) : 0; }
// 交换：提交本帧（本层共享事件到 v），交给公共后端在 v 之后上屏；轮到下一张颜色贴图，等它上一次上屏交出去后才往里画
GLB(eglSwapBuffers, EGLBoolean, EGLDisplay d, EGLSurface sf) { @autoreleasepool {
    if (gl_session_stopping()) return 0;
    (void)d; surface_t *s = sf; if (!s) { g_err = EGL_BAD_SURFACE; return 0; }
    if (s->pbuffer) {
        if (!gl_cur || g_draw != s) { g_err = EGL_BAD_SURFACE; return 0; }
        gl_commit(); // EGL §3.10.3 implicit flush; no presentation, rotation or pacing.
        return 1;
    }
    dm_gfx_enter(); uint64_t t_in = clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
    uint64_t v = gl_commit();
    // 存储按 GL 行序：GL 的左下 (0,0) 起 ext_w × ext_h 就是贴图第 0 行起的这块（上屏再翻转）
    int crop[4] = {0, 0, (int)s->ext_w, (int)s->ext_h}, part = s->ext_w && s->ext_h && (s->ext_w < s->w || s->ext_h < s->h);
    if (part) { static uint32_t lw, lh; if (lw != s->ext_w || lh != s->ext_h) { lw = s->ext_w; lh = s->ext_h; GLOG("上屏取窗口表面左下 %ux%u（游戏的 vi 层尺寸）", lw, lh); } }
    s->ext_w = s->ext_h = 0;
    if (s->wid[s->cur]) { if (!part) crop[2] = crop[3] = 0; gl_wd_present(s->wid[s->cur], s->w, s->h, crop); part = crop[2] > 0 && crop[3] > 0; }   // 宽屏：裁剪按放宽换算（核心定出图范围）
    s->ticket[s->cur] = mtl_present_ext(s->serial[s->cur], nvn_fps_period((s->interval ? s->interval : 1) / (double)nvn_be_fps_target()), (__bridge void *)gl_ev, v, part ? crop : NULL);
    s->cur = (s->cur + 1) % NSURF;
    uint64_t t_pw = clock_gettime_nsec_np(CLOCK_UPTIME_RAW); if (s->ticket[s->cur]) nvn_be_present_wait(s->ticket[s->cur]);
    uint64_t t_pace = clock_gettime_nsec_np(CLOCK_UPTIME_RAW); dm_gfx_leave();
    // 交换锁垂直同步（Switch 语义：NVIDIA 驱动的 eglSwapBuffers 等到第 interval 次垂直同步才返回，游戏主循环靠它锁帧）。只交给上屏路径不等的话，
    //   上屏路径没按显示刷新节拍卡住时（Mac 宿主离屏图层、Spare-host 无显示器）游戏主循环空转到每秒几百帧（10-25 数码宝贝 Mac 局 321 / Spare-host 716 帧/秒）。
    //   按 interval / 刷新率的节拍等到下一拍；落后超过一拍不补帧，从现在重新对齐。interval 0 = 不等
    //   有显示链接（iPhone、Mac 窗口）时跟链接的拍（同 Vulkan 层 vkAcquireNextImageKHR）：自己的时钟和链接相位对不上，两套节拍叠加每帧多错过 1–3 拍
    //   （10-25 测试设备2 数码宝贝标题页上屏间隔 50 / 67 / 83 ms、12–16 帧/秒）；链接已过拍（本帧已落后）就不等
    double per = nvn_fps_period(s->interval / (double)nvn_be_fps_target());
    __atomic_store_n(&g_gl_game_fps, s->interval ? nvn_be_fps_target() / (int)s->interval : 0, __ATOMIC_RELAXED);   // 节拍周期 interval / 目标帧率 的倒数（上限另算）
    // interval 0 原来不等；显式上限启用后也应限帧。上限 30 由链接自身降为 30 Hz，interval 2 只等同一拍。
    if (per > 0 && nvn_be_link_active()) nvn_be_link_wait_tick(s->interval ? (int)s->interval : nvn_fps_limit() > 60 ? 0 : 1, (uint64_t)(2e9 * per));
    else if (per > 0) {
        struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); double now = ts.tv_sec + ts.tv_nsec * 1e-9;
        if (s->next_swap <= 0 || now - s->next_swap > per) s->next_swap = now;
        s->next_swap += per;
        double wait = s->next_swap - now;
        if (wait > 0) s2a_sleep_ns((int64_t)(wait * 1e9));   // 不用 nanosleep：游戏线程 utility 下定时器余量放宽、多睡上百毫秒（同 Vulkan 层 vkAcquireNextImageKHR）
    }
    // 每 300 帧一行拆账（真机定位用，10-25 测试设备2 标题页 12–16 帧/秒）：两次交换之间的墙钟 = 游戏自己（含等 GPU）+ 交换里提交 + 等上屏 + 节拍等待
    {   extern _Atomic uint64_t gl_n_tex_alloc; static uint64_t t_prev, acc_frame, acc_pw, acc_pace, acc_sub, w_n0, w_ns0, a_n0; uint64_t t_out = clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
        if (t_prev) acc_frame += t_out - t_prev;
        t_prev = t_out; acc_sub += t_pw - t_in; acc_pw += t_pace - t_pw; acc_pace += t_out - t_pace;
        if ((gl_n_frames + 1) % 300 == 0) {
            uint64_t wn = gl_wait_n - w_n0, wns = gl_wait_ns - w_ns0;
            // 显存账（11-01 街霸 30 周年 Spare-host：每帧新建一张 384x224、Metal 内部内存一路涨到分配失败）：新建贴图存储次数 + 设备当前已分配字节，看新建了是否都放掉
            GLOG("近 300 帧拆账：每帧 %.1f ms = 游戏 %.1f（其中等 GPU %llu 次 共 %.1f）+ 交换提交 %.1f + 等上屏 %.1f + 节拍等 %.1f；交换间隔 %u、显示链接 %s、目标 %d 帧/秒；新建贴图 %llu 张、设备已分配 %.0f MB",
                 acc_frame / 300e6, (acc_frame - acc_sub - acc_pw - acc_pace) / 300e6, (unsigned long long)wn, wns / 300e6, acc_sub / 300e6, acc_pw / 300e6, acc_pace / 300e6,
                 s->interval, nvn_be_link_active() ? "开" : "关", nvn_be_fps_target(), (unsigned long long)(gl_n_tex_alloc - a_n0), gl_dev.currentAllocatedSize / 1048576.0);
            acc_frame = acc_pw = acc_pace = acc_sub = 0; w_n0 = gl_wait_n; w_ns0 = gl_wait_ns; a_n0 = gl_n_tex_alloc; }
    }
    uint64_t f = ++gl_n_frames; dm_guest_frame_tick();
    if (f > 9) { double a = nvn_be_presented_time(f - 8), b = nvn_be_presented_time(f - 9); dm_perf_present(a < 0 ? -1 : a > 0 && b > 0 ? (a - b) * 1e3 : 0); }   // 同 Vulkan 层：上屏序号即票号
    return 1;
}}
GL(eglGetProcAddress, void *, const char *name) { return gl_entry_or_stub(name); }   // 重活套切栈桩（gl_core.m）；没实现的核心名给安全空实现
// 松开本线程的当前上下文与表面（EGL 1.5 §3.11：同 eglMakeCurrent 全空），错误码 / API 回初值；已删的此时才放
GLB(eglReleaseThread, EGLBoolean, void) { @autoreleasepool { bind_current(NULL, NULL); g_api = EGL_OPENGL_ES_API; g_err = EGL_SUCCESS; return 1; }}

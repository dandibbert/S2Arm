// 排队写查找按段（11-02 怪猎：pend_last 走哈希链占提交线程 24%）：生产 nvn_mtl.m（包含）的 pend_add / pend_last，对朴素定义逐次比对。
// 期望（按定义手写，与被测实现无关）：pend_last(a, n) = 本次提交里与 [a, a+n) 重叠（a < hi && a + n > lo）的最新一条写的序号，没有 −1。
//   ① 随机：写集中在少数几页（同页反复改小段，怪猎的样子）、少量跨 ≥ 8 页的宽写、长度 0 的写与查询；每次写后随机查若干次
//   ② 段表记不下（g_pseg_over：页表探测满或区间池满）后本次提交退回走链：照样逐次相等。造法：每写落一个新页，页数超过页表 4096 格
//   长度 0 的写按定义也会命中严格包住其地址的查询（a < dst < a + n）——第一版段表把它当「什么都不重叠」漏掉，本测 ① 抓到
//   ③ 每次提交复位后旧写不再可见
#include "../gfx/nvn/nvn_mtl.m"

static unsigned checks, fails;
#define VERIFY(c, ...) do { checks++; if (!(c)) { fails++; if (fails <= 10) { printf("FAIL "); printf(__VA_ARGS__); putchar('\n'); } } } while (0)
static uint64_t rng = 0x9E3779B97F4A7C15ull;
static uint64_t rnd(void) { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return rng; }
static int naive(uint64_t a, uint64_t n) { for (int e = g_npend - 1; e >= 0; e--) if (a < g_pend_hi[e] && a + n > g_pend_lo[e]) return e; return -1; }
static const uint64_t BASE = 0x7000000000ull;
static uint64_t pick_addr(void) { uint64_t page = rnd() % 6; return BASE + page * 4096 + (rnd() % 4096 & ~3ull); }   // 6 页里打转
static void one_round(int nwrites, int queries_per_write, const char *tag) {
    pend_reset(); unsigned bad0 = fails;
    for (int w = 0; w < nwrites; w++) {
        uint64_t r = rnd() % 100, dst, n;
        if (r < 3) { dst = BASE + (rnd() % 4) * 4096 + (rnd() % 64) * 16; n = (9 + rnd() % 8) * 4096; }   // 跨 ≥ 8 页的宽写
        else if (r < 6) { dst = pick_addr(); n = 0; }                                                     // 长度 0
        else { dst = pick_addr(); n = 16 + (rnd() % 64) * 16; }                                           // 常量段小写（可跨页尾）
        if (pend_add(dst, n, 1) < 0) break;
        for (int q = 0; q < queries_per_write; q++) {
            uint64_t a = pick_addr(), qn = rnd() % 8 == 0 ? 0 : 4 + rnd() % 1024; int got = pend_last(a, qn), want = naive(a, qn);
            VERIFY(got == want, "%s：第 %d 写后查 [%#llx, +%llu)：pend_last %d，定义 %d（段表%s）", tag, w, (unsigned long long)(a - BASE), (unsigned long long)qn, got, want, g_pseg_over ? "已满走链" : "");
        }
    }
    printf("%s %s：%d 写、每写 %d 查，区间池 %s（用了 %d 段）\n", fails == bad0 ? "PASS" : "FAIL", tag, g_npend, queries_per_write, g_pseg_over ? "满了、退回走链" : "没满", g_npseg);
}
int main(void) { @autoreleasepool {
    setvbuf(stdout, NULL, _IOLBF, 0);
    for (int r = 0; r < 20; r++) one_round(3000, 4, "① 随机");
    // ② 段表记不下：每写一个新页，直到 g_pseg_over
    pend_reset(); int over_at = -1;
    for (int w = 0; w < 8191; w++) {
        uint64_t dst = BASE + (uint64_t)w * 4096 + (rnd() % 256) * 16; pend_add(dst, 8, 1);
        if (g_pseg_over && over_at < 0) over_at = w;
        if (w % 7 == 0) { uint64_t a = BASE + (rnd() % (w + 1)) * 4096 + (rnd() % 4096); VERIFY(pend_last(a, 32) == naive(a, 32), "② 第 %d 写后", w); }
    }
    VERIFY(over_at >= 0, "② 段表应记不下（实际没有，%d 段）", g_npseg);
    for (int q = 0; q < 2000; q++) { uint64_t a = BASE + (rnd() % g_npend) * 4096 + (rnd() % 4096), n = 1 + rnd() % 300; VERIFY(pend_last(a, n) == naive(a, n), "② 写满后查 %#llx", (unsigned long long)a); }
    printf("%s ② 段表第 %d 写记不下，之后走链\n", fails ? "FAIL" : "PASS", over_at);
    // ④ 写入表按需增长：一次提交 1.1 万条（某 Switch 2 款 boss 关每帧的量，超过旧上限 8192），不该退化成「哪里都写过」，查找照样等于定义；再来一轮 4.4 万条
    for (int big = 11000; big <= 44000; big *= 4) {
        pend_reset();
        for (int w = 0; w < big; w++) { uint64_t dst = BASE + (rnd() % 6) * 4096 + (rnd() % 256) * 16; VERIFY(pend_add(dst, 16 + (rnd() % 8) * 16, 1) == w, "④ %d 条里第 %d 条没记上", big, w); }
        VERIFY(!g_pend_over && g_npend == big, "④ %d 条：g_pend_over=%d、记了 %d 条", big, g_pend_over, g_npend);
        for (int q = 0; q < 2000; q++) { uint64_t a = pick_addr(), n = 1 + rnd() % 300; VERIFY(pend_last(a, n) == naive(a, n), "④ %d 条后查 %#llx", big, (unsigned long long)a); }
        printf("%s ④ 一次提交 %d 条写入：没溢出、查找等于定义\n", fails ? "FAIL" : "PASS", big);
    }
    // ③ 复位后旧写不可见
    pend_reset(); VERIFY(pend_last(BASE, 4096 * 6) == -1, "③ 复位后应查不到");
    printf("%s t_nvn_pendseg：%u 项，%u 项失败\n", fails ? "✗" : "✓", checks, fails);
    return fails != 0;
} }

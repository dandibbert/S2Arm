// present_interval 缺省推导单测（10-28 60 帧零配置）：引擎标记 + L3 判定 → 缺省 display / game；显式写了的优先
// Unity / Unreal 推导 display；L3 判定 need_l3 覆盖回 game；显式写了 present_interval 的不推导
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "../../graphics/nvn/host/game_profile.h"
// 每款参数替身（host_adapt.m 一进程只读一次）：决策 display，供 DM_GAME_PRESENT_DISPLAY 按局现读开关（17–19）
static s2a_nvn_profile g_stub_prof;
const s2a_nvn_profile *s2a_nvn_prof(void) { return &g_stub_prof; }
static int g_stub_src;   // 能力清单来源替身（runtime/src/caps.c s2a_fps60_display_source）
int s2a_fps60_display_source(void) { return g_stub_src; }

static int fails;
static void expect(const char *what, int want, int got) {
    printf("%s 期望 %d → 真结果 %d\n", what, want, got);
    if (want != got) fails++;
}

int main(void) {
    // 1. 无显式、无推导、无 L3 → game
    expect("1. 无显式 / 推导 / L3", 0, s2a_present_decide(0, 0, 0, NULL));

    // 2. 显式 display、无推导、无 L3 → display（显式优先）
    expect("2. 显式 display", 1, s2a_present_decide(1, 1, 0, NULL));

    // 3. 显式 game、推导 display、L3 ok → game（显式优先）
    expect("3. 显式 game 优先于推导", 0, s2a_present_decide(1, 0, 1, "ok"));

    // 4. 推导 display、L3 ok → display
    expect("4. 推导 display + L3 ok", 1, s2a_present_decide(0, 0, 1, "ok"));

    // 5. 推导 display、L3 need_l3 → game
    expect("5. 推导 display + L3 need_l3 → game", 0, s2a_present_decide(0, 0, 1, "need_l3"));

    // 6. 推导 display、L3 untested → game（未验证不放开）
    expect("6. 推导 display + L3 untested → display（默认开、强信号才关）", 1, s2a_present_decide(0, 0, 1, "untested"));

    // 7. 推导 display、无 L3 判定文件 → game
    expect("7. 推导 display + 无 L3 文件 → display", 1, s2a_present_decide(0, 0, 1, NULL));

    // 8. 推导 display、本款不在 L3 表 → game
    expect("8. 推导 display + 不在 L3 表 → display", 1, s2a_present_decide(0, 0, 1, NULL));

    // 9. 推导 game、L3 ok → game（推导本身就是 game）
    expect("9. 推导 game + L3 ok → game", 0, s2a_present_decide(0, 0, 0, "ok"));

    // 10. 显式 display、L3 need_l3 → display（显式不受 L3 覆盖）
    expect("10. 显式 display + L3 need_l3 → display", 1, s2a_present_decide(1, 1, 0, "need_l3"));

    // 60 帧开关闸（s2a_present_fps60_gate，host_adapt.m 在决策后套）：关 = 原帧率，档里的 display 不生效；只认整串 "0" 为关（同 runtime/src/fps.c）
    expect("11. 显式 display + S2A_FPS60=0 → game", 0, s2a_present_fps60_gate(s2a_present_decide(1, 1, 0, NULL), "0"));
    expect("12. 推导 display + L3 ok + S2A_FPS60=0 → game", 0, s2a_present_fps60_gate(s2a_present_decide(0, 0, 1, "ok"), "0"));
    expect("13. 显式 display + S2A_FPS60 未设 → game（11-11：默认原帧率，玩家开才放开）", 0, s2a_present_fps60_gate(s2a_present_decide(1, 1, 0, NULL), NULL));
    expect("14. 推导 display + L3 ok + S2A_FPS60=1 → display", 1, s2a_present_fps60_gate(s2a_present_decide(0, 0, 1, "ok"), "1"));
    expect("15. game + S2A_FPS60 未设 → game", 0, s2a_present_fps60_gate(s2a_present_decide(0, 0, 0, NULL), NULL));
    expect("16. display + S2A_FPS60=00（不是整串 1）→ game", 0, s2a_present_fps60_gate(1, "00"));
    // 进程内重启（设置页切 60 帧 →「立刻重启」，11-02 维护者「关了 60 帧游戏还是 60 帧」）：每款参数只读一次、缓存开关前的决策，
    //   出图模式（hle_nvn.c present_cfg 每局读 DM_GAME_PRESENT_DISPLAY）要按当局的 S2A_FPS60 走
    g_stub_prof.t.present_display = 1;
    unsetenv("S2A_FPS60"); expect("17. 第 1 局玩家没开（未设）→ game", 0, DM_GAME_PRESENT_DISPLAY);
    setenv("S2A_FPS60", "0", 1); expect("18. 切关后重启的第 2 局 → game", 0, DM_GAME_PRESENT_DISPLAY);
    setenv("S2A_FPS60", "1", 1); expect("19. 再切开后重启的第 3 局 → display", 1, DM_GAME_PRESENT_DISPLAY);
    // 审计 P1-80：来源只在能力清单（每款参数决策 game）时同样按当局开关——原先清单 setenv DM_PRESENT_INTERVAL 压过开关，关了照样 display
    g_stub_prof.t.present_display = 0; g_stub_src = 1;
    unsetenv("S2A_FPS60"); expect("20. 只有清单来源、玩家没开 → game（清单不替玩家开）", 0, DM_GAME_PRESENT_DISPLAY);
    setenv("S2A_FPS60", "1", 1); expect("20b. 只有清单来源、玩家开了 → display", 1, DM_GAME_PRESENT_DISPLAY);
    setenv("S2A_FPS60", "0", 1); expect("21. 只有清单来源、切关后重启 → game", 0, DM_GAME_PRESENT_DISPLAY);
    g_stub_src = 0; unsetenv("S2A_FPS60"); expect("22. 没有任何来源 → game", 0, DM_GAME_PRESENT_DISPLAY);

    if (fails) printf("✗ t_fps_default：%d 项不符\n", fails);
    else printf("✓ t_fps_default：present_interval 推导规则（引擎标记 + L3 判定）、显式优先、L3 只有 need_l3 才退回 game（默认开）全过\n");
    return fails != 0;
}

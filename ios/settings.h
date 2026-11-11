#pragma once

// 在模块装载和 C# 初始化前读取当前 App 的画面偏好。
void s2a_ios_load_graphics_settings(void);
// 诊断局（#438）：设置里 S2ADiagRunNext 开着 → 设逐通道读数的环境变量（读数写 <本款文档根>/diagrun/），并把开关关掉（只管这一局）
#ifdef __OBJC__   // 本头也被纯 C 的 runtime/src/host_main.c 引用；NSUserDefaults 只有 Objective-C 看得到
@class NSUserDefaults;
void s2a_ios_apply_diag_run(NSUserDefaults *defaults);
#endif
// 包内能力清单 s2a_caps.tsv 里某项能力（fps60 / widen）的值：1 / 0；没有清单或没有这行 = -1（设置页据此如实显示开关、起局据此认 60 帧手动开）
int s2a_ios_caps_value(const char *cap);
// 串行会话启动阶段使用：旧客体停止后，撤销本模块注入的值并读取新偏好。
// 启动时显式给出的环境值始终优先；后来已改为不同内容的值不撤销。
// Returns 0 on success, -1 on failure; the caller must leave the session stopped.
int s2a_ios_reload_graphics_settings(void);

#ifdef __OBJC__
@class NSUserDefaults;
@class NSDictionary;
@class NSArray;
// 当前这款游戏的设置（11-11 维护者：画面设置不许串到别的游戏）。多款容器选了游戏（S2A_LAUNCH_GAME_ID）时，画面类的键
//   （s2a_ios_per_game_keys）读先看 S2AGame.<游戏号>、这款没设过再看 App 级——App 级就是「所有游戏的默认」，老玩家原有的设置
//   原样留在那里当默认、不迁不丢；写和删只动这一款。其余键原样走 App 级。单款壳 / 还没选游戏 = App 级本身。
//   起局读取、游戏内设置页、撑不住就问玩家、社区补丁都用它，不再直接用 standardUserDefaults 读写画面键。
NSUserDefaults *s2a_ios_settings_defaults(void);
NSArray *s2a_ios_per_game_keys(void);
// Discrete values compatible with Settings.bundle and normalized in-app values.
NSArray<NSDictionary *> *s2a_ios_graphics_options(void);
NSDictionary *s2a_ios_graphics_snapshot(NSUserDefaults *defaults);
// 设置页「完成」时比进页快照与当前快照（缺口扫描族 2）：掌机 / 主机运行中直接切、不算重启项。
//   返回 -1 = 底座没变，0 / 1 = 要切成掌机 / 主机；*restart = 除底座外还有要重启才生效的画面设置变了
NSInteger s2a_ios_settings_docked_change(NSDictionary *entry, NSDictionary *now, BOOL *restart);
void s2a_ios_apply_graphics_settings(NSUserDefaults *defaults);
// 当前渲染倍率（S2A_RES_SCALE，0.25–4；没设或越界退回设置值，再退回 1）。每次现读，不缓存：进程内重启后环境变量会被重写（审计 P1-9）
float s2a_ios_res_scale(NSUserDefaults *defaults);
void s2a_ios_reapply_graphics_settings(NSUserDefaults *defaults);
// 把包内 Info.plist 的画面模式 / 直连库版本 / 出包时间发布成设置页「关于本包」的只读文本（#377 F09）
void s2a_ios_publish_bundle_info(NSDictionary *info, NSUserDefaults *defaults);
#endif

using System;
using System.Reflection;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types;

namespace S2A.Services.Tests
{
    // 审计 P1-94：横握只转触屏 / 自动玩家那份，外接实体手柄报的物理键不转；收局 Stop 清掉「挪出掌机格」旗标。
    //   期望按横握规则手写（InputPump.RotateSideways 注释：右只横握顺时针 90°，屏上 A → 物理 X，摇杆 (x, y) → (−y, x)），不跑被测实现推期望。
    public static unsafe partial class Program
    {
        [SoloTest("--input-sideways")]
        private static int InputSideways()
        {
            int bad = 0;
            void Row(string what, string expect, string actual)
            {
                bool ok = expect == actual; if (!ok) bad++;
                Console.WriteLine($"{(ok ? "✓" : "✗")} 横握输入 {what}：{expect} → {actual}");
            }
            const ControllerType R = ControllerType.JoyconRight; const NpadJoyHoldType H = NpadJoyHoldType.Horizontal;
            // ① 触屏按 A（横握后看到的「确认」）→ 物理 X
            GamepadInput t = new() { Buttons = ControllerKeys.A };
            InputPump.ComposeAutoplay(ref t, false, 0, false, 0, 0, 0, 0, R, H);
            Row("触屏 A", "X", t.Buttons.ToString());
            // ② 实体手柄报物理 A、摇杆 (1000, 0)：原样
            GamepadInput p = new() { Buttons = ControllerKeys.A }; p.LStick.Dx = 1000;
            InputPump.ComposeAutoplay(ref p, true, 0, false, 0, 0, 0, 0, R, H);
            Row("实体手柄 A 与摇杆", "A / 1000,0", $"{p.Buttons} / {p.LStick.Dx},{p.LStick.Dy}");
            // ③ 实体手柄按 B，同时自动玩家给 A：B 原样，A 转成 X
            GamepadInput m = new() { Buttons = ControllerKeys.B };
            InputPump.ComposeAutoplay(ref m, true, (uint)ControllerKeys.A, false, 0, 0, 0, 0, R, H);
            Row("实体手柄 B + 自动玩家 A", "B, X", m.Buttons.ToString());
            // ④ 实体手柄摇杆 (1000, 0)，自动玩家给摇杆 (0, 2000)：自动玩家覆盖并转成物理，落在右只的右摇杆槽 (−2000, 0)
            GamepadInput s = new(); s.LStick.Dx = 1000;
            InputPump.ComposeAutoplay(ref s, true, 0, true, 0, 2000, 0, 0, R, H);
            Row("自动玩家摇杆覆盖并转", "-2000,0", $"{s.RStick.Dx},{s.RStick.Dy}");
            // ⑤ 收局清旗标
            FieldInfo f = typeof(InputPump).GetField("_movedOffHandheld", BindingFlags.NonPublic | BindingFlags.Static)!;
            f.SetValue(null, true); InputPump.Stop();
            Row("Stop 后挪出掌机格旗标", "False", f.GetValue(null)!.ToString()!);
            Console.WriteLine($"{(bad == 0 ? "🟢" : "🔴")} 横握输入单测：5 项，不符 {bad}");
            return bad == 0 ? 0 : 1;
        }
    }
}

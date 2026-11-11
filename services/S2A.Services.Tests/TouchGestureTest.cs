using System;
using System.Collections.Generic;
using Ryujinx.HLE.HOS.Services.Hid;

namespace S2A.Services.Tests
{
    // 触点 → 手势（TouchGestureState.FeedContacts，苹果端 InputPump 用；#568 太阁立志传Ⅴ DX 只读手势区）。
    //   期望按识别规则手写：按下即 Touch；500 ms 内抬起且没挪出 10 点 → Tap + Complete；按住满 500 ms → Press；挪出 10 点 → Pan（带位移）；
    //   两指同时按 → Cancel 并报一次不支持，全抬起后恢复；坐标原样（加宽出图的 1564 宽也不裁）
    public static class TouchGestureTest
    {
        public static void Run(Action<string, object, object> check)
        {
            var got = new List<GestureInput>(); int unsupported = 0;
            var g = new TouchGestureState(got.Add, _ => unsupported++);
            const ulong ms = 1_000_000;
            g.FeedContacts(1, 0, 942, 298, 1 * ms);
            g.FeedContacts(1, 0, 943, 299, 50 * ms);   // 挪 1 点：不算拖动
            g.FeedContacts(0, 0, 0, 0, 120 * ms);
            check("手势 点按：Touch → Tap → Complete", "Touch,Tap,Complete", string.Join(",", got.ConvertAll(x => x.Type.ToString())));
            check("手势 点按：坐标原样、Tap 带一个点", "943,299,1", $"{got[1].X},{got[1].Y},{got[1].PointCount}");
            check("手势 点按：同一次按压同一个上下文号", "1,1,1", $"{got[0].ContextNumber},{got[1].ContextNumber},{got[2].ContextNumber}");

            got.Clear();
            g.FeedContacts(1, 0, 1500, 600, 200 * ms);   // 加宽空间的坐标
            g.FeedContacts(1, 0, 1500, 600, 701 * ms);   // 按住 501 ms
            g.FeedContacts(0, 0, 0, 0, 800 * ms);
            check("手势 长按：Touch → Press → Complete（不再报 Tap），坐标不裁", "Touch,Press,Complete|1500,600|2", string.Join(",", got.ConvertAll(x => x.Type.ToString())) + $"|{got[1].X},{got[1].Y}|{got[0].ContextNumber}");

            got.Clear();
            g.FeedContacts(1, 0, 100, 100, 900 * ms);
            g.FeedContacts(1, 0, 120, 100, 920 * ms);   // 挪 20 点 > 10
            g.FeedContacts(0, 0, 0, 0, 940 * ms);
            check("手势 拖动：Touch → Pan(+20,0) → Complete", "Touch,Pan,Complete|20,0", string.Join(",", got.ConvertAll(x => x.Type.ToString())) + $"|{got[1].DeltaX},{got[1].DeltaY}");

            got.Clear();
            g.FeedContacts(1, 0, 10, 10, 1000 * ms);
            g.FeedContacts(2, 0, 0, 0, 1010 * ms);
            g.FeedContacts(2, 0, 0, 0, 1020 * ms);
            g.FeedContacts(0, 0, 0, 0, 1030 * ms);
            g.FeedContacts(1, 0, 50, 50, 1040 * ms);
            check("手势 两指：Cancel、只报一次不支持、全抬起后恢复", "Touch,Cancel,Touch|1", string.Join(",", got.ConvertAll(x => x.Type.ToString())) + $"|{unsupported}");
        }
    }
}

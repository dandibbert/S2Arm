using Ryujinx.Audio;
using Ryujinx.Audio.Backends.Apple;   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
using Ryujinx.Audio.Backends.Apple.Native;   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
using Ryujinx.Audio.Common;
using System;
using System.Runtime.InteropServices;
using static Ryujinx.Audio.Backends.Apple.Native.AudioToolbox;   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑

namespace S2A.Services.Tests
{
    public static partial class Program
    {
        // Pre-boot solo test（#189）：不出声——只试建队列读默认输出设备的真实声道数、起真实驱动看它上报几声道，不起播、不需要服务层夹具。
        //   试建的 callback 必须非空：macOS 27 起 AudioQueueNewOutput 对 NULL callback 直接回 -50（与声道数无关）
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void AqOutputCallback(nint userData, nint audioQueue, nint buffer);

        private static readonly AqOutputCallback _noOpCallback = static (_, _, _) => { };

        [SoloTest("--apple-audio-real-channels")]
        internal static int AppleAudioRealChannels()
        {
            int failures = 0;

            void Check(string name, bool ok, string detail = "")
            {
                Console.WriteLine($"{(ok ? "通过" : "【红】")} {name}{(detail.Length > 0 ? "：" + detail : "")}");

                if (!ok)
                {
                    failures++;
                }
            }

            if (!AppleHardwareDeviceDriver.IsSupported)   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
            {
                Console.WriteLine("【红】Apple 音频后端不可用");
                return 1;
            }

            // ① 5.1 布局常量 = (121 << 16) | 6；旧值 0x650006 是 (101 << 16) | 6（Stereo 族带 6 声道），
            //   AudioQueue 收下但当作布局未知，隐式 6→2 只复制 FL/FR、中置 / 环绕整段丢失
            Check("MPEG_5_1_A 常量 = (121<<16)|6",
                AudioToolbox.kAudioChannelLayoutTag_MPEG_5_1_A == ((121u << 16) | 6u),
                $"0x{AudioToolbox.kAudioChannelLayoutTag_MPEG_5_1_A:X6}");

            // ② 独立读默认输出设备的真实声道数（与驱动探针同一队列属性，2 声道队列即可读）
            AudioStreamBasicDescription format =
                AppleHardwareDeviceDriver.GetAudioFormat(SampleFormat.PcmFloat, Constants.TargetSampleRate, 2);   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑

            int newResult = AudioQueueNewOutput(
                ref format,
                Marshal.GetFunctionPointerForDelegate(_noOpCallback),
                nint.Zero, nint.Zero, nint.Zero, 0, out nint queue);

            uint deviceChannels = 0;
            bool readOk = false;

            if (newResult == 0)
            {
                uint size = sizeof(uint);
                int getResult = AudioQueueGetProperty(
                    queue, kAudioQueueDeviceProperty_NumberChannels, out deviceChannels, ref size);
                AudioQueueDispose(queue, true);
                readOk = getResult == 0;
                Check("独立读默认输出设备声道数", readOk, $"rc={getResult}");
            }
            else
            {
                Check("独立读默认输出设备声道数（试建队列）", false, $"AudioQueueNewOutput rc={newResult}");
            }

            if (readOk)
            {
                // ③ 后端上报 6 声道 ⇔ 设备真实声道数 ≥ 6：2 声道设备上报立体声（隐式 6→2 丢中置 / 环绕整段修掉），
                //   真 5.1 设备照报 6（不降混）。换算不依赖本机设备，两声道数同源同断言
                string oldCh2 = Environment.GetEnvironmentVariable("S2A_AUDIO_CH2");
                string oldStereo = Environment.GetEnvironmentVariable("S2A_AUDIO_STEREO");
                // S2A_AUDIO_OUT=sys 显式下混会照报 5.1（#189 后续）：这里测的是缺省上报，先隔离它
                string oldOut = Environment.GetEnvironmentVariable("S2A_AUDIO_OUT");
                try
                {
                    Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", null);
                    Environment.SetEnvironmentVariable("S2A_AUDIO_STEREO", null);
                    Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", null);

                    using AppleHardwareDeviceDriver driver = new();   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
                    bool supports6 = driver.SupportsChannelCount(6);
                    Check("上报 6 声道 ⇔ 设备 ≥ 6 声道", supports6 == (deviceChannels >= 6),
                        $"设备 {deviceChannels} 声道、SupportsChannelCount(6)={supports6}（期望 {deviceChannels >= 6}）");
                    Check("2 声道始终上报", driver.SupportsChannelCount(2));

                    // ④ S2A_AUDIO_CH2=1 仍强制立体声（既有减法开关不受影响；显式下混也让位于它）
                    Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", "1");
                    Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", "sys");
                    using AppleHardwareDeviceDriver forced = new();   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
                    Check("S2A_AUDIO_CH2=1 强制立体声（sys 显式下混也让位）",
                        !forced.SupportsChannelCount(6) && forced.SupportsChannelCount(2));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", oldCh2);
                    Environment.SetEnvironmentVariable("S2A_AUDIO_STEREO", oldStereo);
                    Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", oldOut);
                }
            }

            Console.WriteLine(failures == 0
                ? $"Apple 音频真实声道数：全部通过（默认输出设备 {deviceChannels} 声道）"
                : $"Apple 音频真实声道数：{failures} 项失败");

            return failures == 0 ? 0 : 1;
        }
    }
}

using Ryujinx.Audio.Backends.Apple;   // 平台例外：本用例只测 Apple 音频后端的显式下混（#189），只在 macOS 上跑
using Ryujinx.Audio.Backends.CompatLayer;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S2A.Services.Tests
{
    public static partial class Program
    {
        // Pre-boot solo test (#189): the explicit 5.1 -> 2.0 downmix is pure CPU work (AudioConverter / Q15 matrix),
        // no device session is opened for the processor part; the driver part only probes (trial queue, no Start).
        // The headline is the no-clipping comparison: the renderer's fallback fold (the matrix coefficients) clips
        // at full-scale input, the system's explicit downmix does not.
        [SoloTest("--apple-audio-output-modes")]
        internal static int AppleAudioOutputModes()
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

            string oldMode = Environment.GetEnvironmentVariable("S2A_AUDIO_OUT");
            string oldCh2 = Environment.GetEnvironmentVariable("S2A_AUDIO_CH2");
            string oldStereo = Environment.GetEnvironmentVariable("S2A_AUDIO_STEREO");

            try
            {
                Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", null);
                Environment.SetEnvironmentVariable("S2A_AUDIO_STEREO", null);

                AppleOutputMode Select(string value)
                {
                    Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", value);

                    return AppleOutputModeSelector.FromEnvironment();
                }

                // ① 档位选择：缺省 native（现状），sys / matrix 显式选，不认识的回落 native
                Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", null);
                Check("缺省档 = native（现状）", AppleOutputModeSelector.FromEnvironment() == AppleOutputMode.Native);
                Check("matrix", Select("matrix") == AppleOutputMode.Matrix);
                Check("q15 别名", Select("q15") == AppleOutputMode.Matrix);
                Check("sys", Select("sys") == AppleOutputMode.System);
                Check("System 别名（不分大小写）", Select("System") == AppleOutputMode.System);
                Check("converter 别名", Select("converter") == AppleOutputMode.System);
                Check("native", Select("native") == AppleOutputMode.Native);
                Check("off 别名", Select("off") == AppleOutputMode.Native);
                Check("不认识的档回落 native", Select("bogus") == AppleOutputMode.Native);

                // ② 驱动上报门：sys 显式下混时照报 5.1（6 声道混音进会话、由会话折叠），S2A_AUDIO_CH2=1 仍强制立体声
                if (!AppleHardwareDeviceDriver.IsSupported)   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
                {
                    Console.WriteLine("【红】Apple 音频后端不可用");
                    return 1;
                }

                Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", "sys");
                using (AppleHardwareDeviceDriver sysDriver = new())   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
                {
                    Check("sys 照报 5.1（显式下混在会话侧）",
                        sysDriver.SupportsChannelCount(6) && sysDriver.SupportsChannelCount(2),
                        $"SupportsChannelCount(6)={sysDriver.SupportsChannelCount(6)}");
                }

                Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", "1");
                using (AppleHardwareDeviceDriver forcedDriver = new())   // 平台例外：本用例只测 Apple 音频后端（#189），只在 macOS 上跑
                {
                    Check("sys + S2A_AUDIO_CH2=1 仍强制立体声",
                        !forcedDriver.SupportsChannelCount(6) && forcedDriver.SupportsChannelCount(2));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("S2A_AUDIO_OUT", oldMode);
                Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", oldCh2);
                Environment.SetEnvironmentVariable("S2A_AUDIO_STEREO", oldStereo);
            }

            const int rate = 48000;
            const int frames = 4800;
            const int skip = 480;
            const float amplitude = 16000.0f;
            double inputRms = amplitude / Math.Sqrt(2.0);

            byte[] Tone(int channel)
            {
                byte[] data = new byte[frames * 6 * sizeof(short)];
                Span<short> samples = MemoryMarshal.Cast<byte, short>(data.AsSpan());

                for (int frame = 0; frame < frames; frame++)
                {
                    samples[frame * 6 + channel] = (short)MathF.Round(amplitude * MathF.Sin(2.0f * MathF.PI * 997.0f * frame / rate));
                }

                return data;
            }

            static (double Left, double Right) Rms(byte[] stereo, int frameCount, int skipFrames)
            {
                ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(stereo.AsSpan());
                double left = 0, right = 0;
                int count = 0;

                for (int frame = skipFrames; frame < frameCount; frame++)
                {
                    left += (double)samples[frame * 2] * samples[frame * 2];
                    right += (double)samples[frame * 2 + 1] * samples[frame * 2 + 1];
                    count++;
                }

                return count > 0 ? (Math.Sqrt(left / count), Math.Sqrt(right / count)) : (0, 0);
            }

            // 到满幅（|采样| = 32767）的采样数与峰值（折叠削波的直接读数）
            static (int Clipped, double Peak) ClippedCount(byte[] stereo)
            {
                ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(stereo.AsSpan());
                int clipped = 0;
                int peak = 0;

                for (int i = 0; i < samples.Length; i++)
                {
                    int magnitude = Math.Abs((int)samples[i]);   // short.MinValue 的绝对值放 int 里算，不抛

                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }

                    if (magnitude >= 32767)
                    {
                        clipped++;
                    }
                }

                return (clipped, peak);
            }

            // ③ matrix：与 compat 层逐字节一致（同一套系数，对拍基准）
            using (AppleOutputProcessor matrix = AppleOutputProcessor.Create(AppleOutputMode.Matrix, rate, 2400))
            {
                Check("matrix 器件建立", matrix.EffectiveMode == AppleOutputMode.Matrix && matrix.InitError == null);

                byte[] input = Tone(2);
                byte[] output = new byte[frames * 2 * sizeof(short)];
                byte[] expected = new byte[output.Length];
                matrix.Process(input, output);

                Downmixing.DownMixSurroundToStereo(MemoryMarshal.Cast<byte, short>(input.AsSpan()), MemoryMarshal.Cast<byte, short>(expected.AsSpan()));
                Check("matrix 与 Downmixing 逐字节一致", output.AsSpan().SequenceEqual(expected));

                (double left, double right) = Rms(output, frames, skip);
                Check("matrix 中置有量", left > 0.6 * inputRms && right > 0.6 * inputRms, $"L {left:F0} R {right:F0}（输入 {inputRms:F0}）");
            }

            // ④ sys：AudioConverter PerformDownmix；实测增益前 0.4142、中置 / 同侧环绕 0.2929、LFE 丢弃
            using (AppleOutputProcessor system = AppleOutputProcessor.Create(AppleOutputMode.System, rate, 2400))
            {
                Check("sys 器件建立", system.EffectiveMode == AppleOutputMode.System && system.InitError == null,
                    system.InitError ?? "");

                if (system.EffectiveMode == AppleOutputMode.System)
                {
                    byte[] output = new byte[frames * 2 * sizeof(short)];

                    system.Process(Tone(0), output);
                    (double left, double right) = Rms(output, frames, skip);
                    Check("sys 左前 ≈ 0.4142", Math.Abs(left / inputRms - 0.4142) < 0.01 && right < 1, $"L {left / inputRms:F4} R {right / inputRms:F4}");

                    system.Process(Tone(2), output);
                    (left, right) = Rms(output, frames, skip);
                    Check("sys 中置 ≈ 0.2929 两路", Math.Abs(left / inputRms - 0.2929) < 0.01 && Math.Abs(right / inputRms - 0.2929) < 0.01,
                        $"L {left / inputRms:F4} R {right / inputRms:F4}");

                    system.Process(Tone(3), output);
                    (left, right) = Rms(output, frames, skip);
                    Check("sys 低音丢弃", left < 1 && right < 1, $"L {left:F2} R {right:F2}");
                }
            }

            // ⑤ 不削波对拍（本 PR 的主张）：满幅附近输入，渲染器回落那套系数（matrix 同系数）削波，
            //    系统显式下混（归一化：0.4142 + 2×0.2929 = 1.0）不削波
            const float hot = 0.9f * short.MaxValue;

            byte[] Hot(params int[] channels)
            {
                byte[] data = new byte[frames * 6 * sizeof(short)];
                Span<short> samples = MemoryMarshal.Cast<byte, short>(data.AsSpan());

                for (int frame = 0; frame < frames; frame++)
                {
                    foreach (int channel in channels)
                    {
                        samples[frame * 6 + channel] = (short)MathF.Round(hot * MathF.Sin(2.0f * MathF.PI * 997.0f * frame / rate));
                    }
                }

                return data;
            }

            using (AppleOutputProcessor system = AppleOutputProcessor.Create(AppleOutputMode.System, rate, 2400))
            using (AppleOutputProcessor matrix = AppleOutputProcessor.Create(AppleOutputMode.Matrix, rate, 2400))
            {
                if (system.EffectiveMode == AppleOutputMode.System)
                {
                    byte[] stereo = new byte[frames * 2 * sizeof(short)];

                    // FL+FC 各 0.9：回落和 1.536、系统和 0.7071
                    system.Process(Hot(0, 2), stereo);
                    (int clipped, double peak) = ClippedCount(stereo);
                    Check("sys FL+FC 各 0.9 不削波", clipped == 0, $"{clipped} 个满幅采样、峰值 {peak / short.MaxValue:F3}");
                    Check("sys FL+FC 各 0.9 峰值 ≈ 0.707", Math.Abs(peak / hot - 0.7071) < 0.02, $"峰值 {peak / short.MaxValue:F3}");

                    matrix.Process(Hot(0, 2), stereo);
                    (clipped, peak) = ClippedCount(stereo);
                    Check("matrix（渲染器回落同系数）FL+FC 各 0.9 削波", clipped > frames / 20 && peak >= 32767,
                        $"{clipped} 个满幅采样（{clipped / (double)frames:P0}）、峰值 {peak / short.MaxValue:F3}（负对照）");

                    // 六路全 0.9：回落和 2.399、系统和 1.0（最坏情况仍不削波）
                    system.Process(Hot(0, 1, 2, 3, 4, 5), stereo);
                    (clipped, peak) = ClippedCount(stereo);
                    Check("sys 六路全 0.9 不削波", clipped == 0, $"{clipped} 个满幅采样、峰值 {peak / short.MaxValue:F3}");
                    Check("sys 六路全 0.9 峰值 ≈ 0.9（和 = 1.0）", Math.Abs(peak / hot - 1.0) < 0.02, $"峰值 {peak / short.MaxValue:F3}");

                    matrix.Process(Hot(0, 1, 2, 3, 4, 5), stereo);
                    (clipped, peak) = ClippedCount(stereo);
                    Check("matrix 六路全 0.9 削波", clipped > frames / 20 && peak >= 32767,
                        $"{clipped} 个满幅采样（{clipped / (double)frames:P0}）、峰值 {peak / short.MaxValue:F3}（负对照）");

                    // 读数：一片 2400 帧的耗时（参考量级：µs 级，单核占比极小）
                    byte[] slice = new byte[2400 * 6 * sizeof(short)];
                    long start = Stopwatch.GetTimestamp();

                    for (int i = 0; i < 100; i++)
                    {
                        system.Process(slice, stereo);
                    }

                    double microseconds = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency / 100;
                    Console.WriteLine($"读数：sys 一片 2400 帧 {microseconds:F1} µs（50 ms 音频，单核约 {microseconds * 100 / (2400 / (double)rate * 1e6):F3}%）");
                }
            }

            Console.WriteLine(failures == 0 ? "5.1 显式下混：全部通过" : $"5.1 显式下混：{failures} 项失败");

            return failures == 0 ? 0 : 1;
        }
    }
}

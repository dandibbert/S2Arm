using Ryujinx.Audio.Backends.Apple.Native;   // 平台例外：Apple 后端平台实现内部引用本平台 Native 声明（#189）
using Ryujinx.Audio.Backends.CompatLayer;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Audio.Backends.Apple   // 平台例外：Apple 后端平台实现的命名空间（#189 显式下混档）
{
    // S2Arm (#189): the explicit 5.1 (6-channel PCM16) -> stereo downmix for the Apple backend.
    //
    // Why: since the real-channel-count fix the backend reports stereo on <6-channel devices, and the renderer folds
    // the 5.1 mix itself: with the game's DownMixParameter when the game provides one, otherwise with
    // Constants.DefaultSurroundToStereoCoefficients = [1.0, 0.707, 0.251, 0.707] (CommandGenerator's fallback),
    // which is unnormalized — L = FL + 0.707*C + 0.251*LFE + 0.707*BL. Measured at 48 kHz with a 997 Hz sine:
    // FL+FC both at 0.9 folds to 1.536 (2632/4800 samples clipped), all six at 0.9 folds to 2.399 (3484/4800 clipped),
    // saturating to PCM16. The system's explicit downmix (AudioConverter, kAudioConverterPropertyPerformDownmix)
    // is normalized: front 0.4142, centre/surround 0.2929, LFE dropped — worst case (front + centre + one surround,
    // 0.4142 + 2*0.2929) sums to 1.0 and does not clip (measured: all six at 0.9 folds to exactly 0.9).
    //
    // Modes (S2A_AUDIO_OUT, opt-in; the default is native and keeps the current path):
    //   sys     - AudioConverter with kAudioConverterPropertyPerformDownmix + channel layouts (MPEG_5_1_A in, stereo
    //             out): the system's explicit downmix, normalized, no clipping.
    //   matrix  - host Q15 downmix with the same coefficients as Downmixing.DownMixSurroundToStereo (i.e. the same
    //             fold the renderer does without game coefficients). This is the fallback when the converter cannot
    //             be built or a slice fails; it can also be selected explicitly for A/B against the compat layer.
    //   native  - keep the current behaviour: the driver reports stereo on <6-channel devices and the renderer folds
    //             (default; identical to not setting the variable).
    internal enum AppleOutputMode
    {
        Native = 0,
        Matrix = 1,
        System = 2,
    }

    internal static class AppleOutputModeSelector
    {
        private const AppleOutputMode DefaultMode = AppleOutputMode.Native;

        public static AppleOutputMode FromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable("S2A_AUDIO_OUT");

            if (string.IsNullOrWhiteSpace(value))
            {
                return DefaultMode;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "matrix":
                case "q15":
                    return AppleOutputMode.Matrix;
                case "sys":
                case "system":
                case "converter":
                    return AppleOutputMode.System;
                case "native":
                case "off":
                    return AppleOutputMode.Native;
                default:
                    Console.Error.WriteLine($"[声] 【红】S2A_AUDIO_OUT=「{value}」不认识（可用 sys / matrix / native）：按 {ModeName(DefaultMode)}");
                    return DefaultMode;
            }
        }

        public static string ModeName(AppleOutputMode mode)
        {
            return mode switch
            {
                AppleOutputMode.Matrix => "矩阵（Q15，系数同渲染器回落）",
                AppleOutputMode.System => "系统显式下混（AudioConverter，归一化不削波）",
                _ => "native（缺省：不上报 5.1，渲染器自己下混）",
            };
        }
    }

    // One instance per folded 6-channel output session. Process() is called from the device pump (guest buffers) and
    // from the renderer thread (Data buffers), so it serialises internally. Input is interleaved PCM16 6-channel
    // (FL FR FC LFE BL BR, the renderer's channel order), output is interleaved PCM16 stereo; the frame count is
    // preserved, so the session's byte accounting and release cadence stay unchanged.
    internal sealed unsafe class AppleOutputProcessor : IDisposable
    {
        private const int InputChannels = 6;
        private const int OutputChannels = 2;
        private const int InputBytesPerFrame = InputChannels * sizeof(short);
        private const int OutputBytesPerFrame = OutputChannels * sizeof(short);

        private readonly object _lock = new();
        private readonly AppleOutputMode _requestedMode;
        private readonly int _sampleRate;
        private readonly int _maxFrames;
        private AppleOutputMode _mode;
        private int _failures;
        private bool _disposed;

        // AudioConverter ('sys')
        private nint _converter;
        private nint _convInputProcPtr;
        private nint _convOutputAbl;
        private AudioConverterInterop.AudioConverterComplexInputDataProc _convInputProc;
        private int _convFrames;
        private nint _convInput;
        private int _convDone;

        private GCHandle _self;

        public string InitError { get; private set; }
        public AppleOutputMode EffectiveMode => _mode;

        private AppleOutputProcessor(AppleOutputMode mode, int sampleRate, int maxFrames)
        {
            _requestedMode = mode;
            _sampleRate = sampleRate;
            _maxFrames = Math.Max(16, maxFrames);
            _mode = mode;
        }

        // Never returns null and never leaves a mode that cannot produce audio: a converter that cannot be built
        // falls back to matrix (InitError says why; the caller logs it once).
        public static AppleOutputProcessor Create(AppleOutputMode mode, int sampleRate, int maxFrames)
        {
            AppleOutputProcessor processor = new(mode, sampleRate, maxFrames);

            if (mode == AppleOutputMode.System && !processor.InitSystem())
            {
                processor._mode = AppleOutputMode.Matrix;
            }

            return processor;
        }

        private static AudioToolbox.AudioStreamBasicDescription MakeFormat(int rate, int channels)
        {
            return new AudioToolbox.AudioStreamBasicDescription
            {
                SampleRate = rate,
                FormatID = AudioToolbox.kAudioFormatLinearPCM,
                FormatFlags = AudioToolbox.kAudioFormatFlagIsSignedInteger | AudioToolbox.kAudioFormatFlagIsPacked,
                BytesPerPacket = (uint)(channels * sizeof(short)),
                FramesPerPacket = 1,
                BytesPerFrame = (uint)(channels * sizeof(short)),
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 16,
                Reserved = 0,
            };
        }

        private static AudioToolbox.AudioChannelLayout Layout(uint tag)
        {
            return new AudioToolbox.AudioChannelLayout
            {
                AudioChannelLayoutTag = tag,
                AudioChannelBitmap = 0,
                NumberChannelDescriptions = 0,
            };
        }

        private bool InitSystem()
        {
            AudioToolbox.AudioStreamBasicDescription input = MakeFormat(_sampleRate, InputChannels);
            AudioToolbox.AudioStreamBasicDescription output = MakeFormat(_sampleRate, OutputChannels);

            int rc = AudioConverterInterop.AudioConverterNew(ref input, ref output, out _converter);

            if (rc != 0 || _converter == 0)
            {
                InitError = $"AudioConverterNew rc={rc}";

                return false;
            }

            AudioToolbox.AudioChannelLayout inputLayout = Layout(AudioToolbox.kAudioChannelLayoutTag_MPEG_5_1_A);
            AudioToolbox.AudioChannelLayout outputLayout = Layout(AudioConverterInterop.kAudioChannelLayoutTag_Stereo);

            rc = AudioConverterInterop.AudioConverterSetProperty(_converter, AudioConverterInterop.PropertyInputChannelLayout,
                (uint)sizeof(AudioToolbox.AudioChannelLayout), &inputLayout);

            if (rc == 0)
            {
                rc = AudioConverterInterop.AudioConverterSetProperty(_converter, AudioConverterInterop.PropertyOutputChannelLayout,
                    (uint)sizeof(AudioToolbox.AudioChannelLayout), &outputLayout);
            }

            uint downmix = 1;

            if (rc == 0)
            {
                rc = AudioConverterInterop.AudioConverterSetProperty(_converter, AudioConverterInterop.PropertyPerformDownmix,
                    sizeof(uint), &downmix);
            }

            if (rc != 0)
            {
                InitError = $"AudioConverter 布局 / PerformDownmix rc={rc}";
                AudioConverterInterop.AudioConverterDispose(_converter);
                _converter = 0;

                return false;
            }

            _self = GCHandle.Alloc(this);
            _convInputProc = ConverterInput;
            _convInputProcPtr = Marshal.GetFunctionPointerForDelegate(_convInputProc);
            _convOutputAbl = Marshal.AllocHGlobal(AudioConverterInterop.AudioBufferListBuffersOffset + sizeof(AudioConverterInterop.AudioBuffer));

            return true;
        }

        public bool Process(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int frames = Math.Min(input.Length / InputBytesPerFrame, output.Length / OutputBytesPerFrame);

            if (frames <= 0)
            {
                return true;
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_mode == AppleOutputMode.Matrix)
                {
                    MatrixDownmix(input, output, frames);

                    return true;
                }

                if (_mode == AppleOutputMode.System && ConvertSystem(input, output, frames))
                {
                    return true;
                }

                MatrixDownmix(input, output, frames);
                CountFailure();

                return false;
            }
        }

        private void CountFailure()
        {
            _failures++;

            if (_failures == 1)
            {
                Console.Error.WriteLine($"[声] 【红】5.1→2.0 档「{AppleOutputModeSelector.ModeName(_requestedMode)}」这一次没出数：这一片按矩阵下混");
            }
        }

        private static void MatrixDownmix(ReadOnlySpan<byte> input, Span<byte> output, int frames)
        {
            ReadOnlySpan<short> source = MemoryMarshal.Cast<byte, short>(input)[..(frames * InputChannels)];
            Span<short> destination = MemoryMarshal.Cast<byte, short>(output)[..(frames * OutputChannels)];

            Downmixing.DownMixSurroundToStereo(source, destination);
        }

        private bool ConvertSystem(ReadOnlySpan<byte> input, Span<byte> output, int frames)
        {
            int remaining = frames;
            int frameOffset = 0;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, _maxFrames);
                int inputBytes = chunk * InputBytesPerFrame;
                int outputBytes = chunk * OutputBytesPerFrame;
                bool ok;

                fixed (byte* inputPtr = input.Slice(frameOffset * InputBytesPerFrame, inputBytes))
                fixed (byte* outputPtr = output.Slice(frameOffset * OutputBytesPerFrame, outputBytes))
                {
                    _convInput = (nint)inputPtr;
                    _convFrames = chunk;
                    _convDone = 0;

                    AudioConverterInterop.AudioBuffer* buffer = (AudioConverterInterop.AudioBuffer*)(_convOutputAbl + AudioConverterInterop.AudioBufferListBuffersOffset);

                    *(uint*)_convOutputAbl = 1;
                    buffer->NumberChannels = OutputChannels;
                    buffer->DataByteSize = (uint)outputBytes;
                    buffer->Data = outputPtr;

                    uint outputFrames = (uint)chunk;
                    int rc = AudioConverterInterop.AudioConverterFillComplexBuffer(_converter, _convInputProcPtr,
                        GCHandle.ToIntPtr(_self), ref outputFrames, _convOutputAbl, 0);

                    ok = rc == 0 && outputFrames == (uint)chunk;
                }

                if (!ok)
                {
                    return false;
                }

                frameOffset += chunk;
                remaining -= chunk;
            }

            return true;
        }

        private static int ConverterInput(nint converter, ref uint ioPackets, nint ioData, nint packetDescription, nint userData)
        {
            AppleOutputProcessor processor = (AppleOutputProcessor)GCHandle.FromIntPtr(userData).Target;

            if (processor._convDone != 0)
            {
                ioPackets = 0;

                return 0;
            }

            AudioConverterInterop.AudioBuffer* buffer = (AudioConverterInterop.AudioBuffer*)(ioData + AudioConverterInterop.AudioBufferListBuffersOffset);

            *(uint*)ioData = 1;
            buffer->NumberChannels = InputChannels;
            buffer->DataByteSize = (uint)(processor._convFrames * InputBytesPerFrame);
            buffer->Data = (void*)processor._convInput;
            processor._convDone = 1;
            ioPackets = (uint)processor._convFrames;

            return 0;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_converter != 0)
                {
                    AudioConverterInterop.AudioConverterDispose(_converter);
                    _converter = 0;
                }

                if (_convOutputAbl != 0)
                {
                    Marshal.FreeHGlobal(_convOutputAbl);
                    _convOutputAbl = 0;
                }

                if (_self.IsAllocated)
                {
                    _self.Free();
                }
            }
        }
    }
}

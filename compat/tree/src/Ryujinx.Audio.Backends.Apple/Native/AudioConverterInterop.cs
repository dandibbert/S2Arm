using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Audio.Backends.Apple.Native   // 平台例外：Apple 后端平台实现的 Native 命名空间（#189 显式下混档）
{
    // S2Arm (#189): the AudioConverter C API slice used by the explicit 5.1 -> 2.0 downmix (AppleOutputProcessor).
    //   The AudioQueue bindings live in AudioToolbox.cs; this is the system downmix converter
    //   (kAudioConverterPropertyPerformDownmix + channel layouts, the normalized fold that does not clip).
    internal static unsafe class AudioConverterInterop
    {
        private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        // AudioConverterPropertyID
        public const uint PropertyPerformDownmix = 0x646D6978;          // 'dmix'
        public const uint PropertyInputChannelLayout = 0x69636C20;     // 'icl '
        public const uint PropertyOutputChannelLayout = 0x6F636C20;    // 'ocl '

        // AudioChannelLayoutTag: stereo, (101 << 16) | 2 (the 5.1 tag is AudioToolbox.kAudioChannelLayoutTag_MPEG_5_1_A)
        public const uint kAudioChannelLayoutTag_Stereo = 0x650002;

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioBuffer
        {
            public uint NumberChannels;
            public uint DataByteSize;
            public void* Data;
        }

        // AudioBufferList is { UInt32 mNumberBuffers; AudioBuffer mBuffers[1]; }; on 64-bit the array starts at
        // offset 8 (the pointer in AudioBuffer aligns the struct to 8). Users of the constant write mBuffers[0] there.
        public const int AudioBufferListBuffersOffset = 8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int AudioConverterComplexInputDataProc(nint inAudioConverter, ref uint ioNumberDataPackets, nint ioData, nint outDataPacketDescription, nint inUserData);

        [DllImport(Lib)]
        public static extern int AudioConverterNew(ref AudioToolbox.AudioStreamBasicDescription inSourceFormat, ref AudioToolbox.AudioStreamBasicDescription inDestinationFormat, out nint outAudioConverter);

        [DllImport(Lib)]
        public static extern int AudioConverterDispose(nint inAudioConverter);

        [DllImport(Lib)]
        public static extern int AudioConverterSetProperty(nint inAudioConverter, uint inID, uint inPropertyDataSize, void* inPropertyData);

        [DllImport(Lib)]
        public static extern int AudioConverterFillComplexBuffer(nint inAudioConverter, nint inInputDataProc, nint inInputDataProcUserData, ref uint ioOutputDataPacketSize, nint outOutputData, nint outPacketDescription);
    }
}

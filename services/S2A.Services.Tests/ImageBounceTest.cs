// SPDX-License-Identifier: Apache-2.0
// #602：单文件镜像（romfs.img，RomFsIndex.IndexedStorage；单一 IMG 的预建路径同一个类）读进游戏内存、目标页读盘期间被写保护 → pread 回 EFAULT。
//   原先 IndexedStorage 直接 RandomAccess.Read 进游戏内存、没有中转：IOException「Bad address」一路抛到服务调用，游戏 Break 退出（iPad 实机两次）。
//   期望（同目录布局 LazyHostFile 那条，GuestMemoryAuditTest --far-bounce-va）：改中转读成功、内容同文件、写钩子收到游戏地址 1 次。
//   夹具：远端（影子页）游戏内存经 GetWritableRegion 取区域后设只读；镜像三件套现造（romfs/ 一个文件、romfs.s2aidx、romfs.img、romfs.imgmap），
//   经 LibHac RomFsFileSystem 打开文件读——与游戏读 RomFS 同一条调用链（IStorage.Read → IndexedStorage.Read）
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Path = System.IO.Path;

namespace S2A.Services.Tests
{
    public static unsafe partial class Program
    {
        private static readonly List<(ulong Va, ulong Size)> _imgBounceWrites = new();
        private static byte* _imgBounceShadow;

        private static void UnprotectImgShadow(ulong va, ulong size)
        {
            _imgBounceWrites.Add((va, size));
            mprotect((nint)(_imgBounceShadow + 0x4000), 0x8000, 3);
        }

        [SoloTest("--image-bounce")]
        private static int ImageBounce()
        {
            const int Chunk = 2 << 20;
            byte* g = (byte*)NativeMemory.AlignedAlloc(Chunk, 0x4000), sh = (byte*)NativeMemory.AlignedAlloc(Chunk, 0x4000);
            long* dir = (long*)NativeMemory.AllocZeroed(1 << 15, sizeof(long));
            FarDesc* desc = (FarDesc*)NativeMemory.AllocZeroed((nuint)sizeof(FarDesc));
            FarDesc* farWas = Native.Far;
            string root = Path.Combine(Path.GetTempPath(), $"s2a_image_bounce_{Environment.ProcessId}"), rom = Path.Combine(root, "romfs");
            byte[] data = new byte[0x8000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 5 + 1);
            ulong lo = (ulong)g;
            IStorage s = null;
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(rom);
                File.WriteAllBytes(Path.Combine(rom, "a.bin"), data);
                RomFsIndex.Write(rom, Path.Combine(root, RomFsIndex.FileName));
                File.WriteAllBytes(Path.Combine(root, RomFsIndex.ImageName), data);   // 镜像 = 索引顺序首尾相接（只有一个文件）
                using (var w = new BinaryWriter(File.Create(Path.Combine(root, RomFsIndex.ImageMapName))))
                {
                    w.Write("S2ARIM01"u8.ToArray()); w.Write((long)data.Length); w.Write(1); w.Write(0L);
                }
                Directory.Delete(rom, true);   // 手机上没有 romfs/：只能读镜像
                s = RomFsIndex.OpenImage(root);

                dir[0] = (long)((ulong)sh - lo);
                desc->Lo = lo;
                desc->Dir = (nint)dir;
                Native.Far = desc;
                GuestMemory.WriteHook = null;
                GuestMemory.WriteQuietHook = null;
                GuestMemory.GpuModifiedHook = null;
                GuestMemory gm = new();
                WritableRegion region = gm.GetWritableRegion(lo + 0x4000, 0x8000, tracked: true);
                Span<byte> dst = region.Memory.Span;
                mprotect((nint)(sh + 0x4000), 0x8000, 1 /* READ */);
                _imgBounceShadow = sh;
                _imgBounceWrites.Clear();
                GuestMemory.WriteHook = &UnprotectImgShadow;
                string res; long got = 0;
                try
                {
                    RomFsFileSystem fs = new(s);
                    using var f = new UniqueRef<IFile>();
                    fs.OpenFile(ref f.Ref, "/a.bin".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                    LibHac.Result r = f.Get.Read(out got, 0x10, dst[..0x7000], ReadOption.None);
                    res = r.IsSuccess() ? "成功" : r.ToString();
                }
                catch (IOException e) { res = "IOException " + e.Message; }
                GuestMemory.WriteHook = null;
                bool same = new Span<byte>(sh + 0x4000, 0x7000).SequenceEqual(data.AsSpan(0x10, 0x7000));
                string hooks = string.Join("、", _imgBounceWrites.ConvertAll(w => w.Va - lo < Chunk ? $"游戏+0x{w.Va - lo:X} 0x{w.Size:X}" : $"0x{w.Va:X} 0x{w.Size:X}"));
                region.Dispose();
                string expect = "成功 / 0x7000 / 同文件 / 游戏+0x4000 0x7000";
                string actual = $"{res} / 0x{got:X} / {(same ? "同文件" : "不同")} / {hooks}";
                bool ok = expect == actual;
                Console.WriteLine($"{(ok ? "✓" : "✗")} 单文件镜像读进写保护的游戏内存（结果 / 读到 / 内容 / 写钩子）：{expect} → {actual}");
                return ok ? 0 : 1;
            }
            finally
            {
                GuestMemory.WriteHook = null;
                Native.Far = farWas;
                mprotect((nint)(sh + 0x4000), 0x8000, 3);
                (s as IDisposable)?.Dispose();
                NativeMemory.AlignedFree(g);
                NativeMemory.AlignedFree(sh);
                NativeMemory.Free(dir);
                NativeMemory.Free(desc);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}

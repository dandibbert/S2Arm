// 测试契约 tests/真机契约.md §1：S2A_COLD_START=1 时，任何 Metal 调用前清首启缓存（着色器 / 管线 / 程序二进制），清完写 Documents/cold_start.txt 一行。
// 清：① 渲染器持久着色器缓存 NXGAL_SHADER_CACHE_DIR；② Ryujinx 主机着色器缓存（程序二进制）<Ryujinx 数据根>/games/*/cache——数据根取法同 services Exports.cs；
//     ③ iOS：本 App 容器里 Metal 自己的着色器 / 管线缓存 Library/Caches/com.apple.metal（Mac 上它不在本进程容器里，不碰，行里注明）。
//     ④ 渲染器设备归档（gal_core.m：设备上现编的管线与特化结果，<NXGAL_PSO_DEV_DIR 或 $HOME/Library/Application Support/S2A/pso_dev；老版本的 Library/Caches/S2A/pso_dev 首次会被挪过去，一并清>）——它也是首启没有的东西，不清会挡住首局现编、首局判分失真；
//        S2A_COLD_KEEP_PSODEV=1 时保留（验「系统 Metal 缓存清空、只剩设备归档」的第二次冷启动用）。
// 随包离线库（各片 metallib）与管线归档是出包产物，不是首启缓存，不清。
using System;
using System.IO;

namespace NxGal
{
    static class NxColdStart
    {
        static bool _done;
        // Documents：S2A_DOCS 优先（Mac 起局方给局目录，与原生层 host_main.c 同口径）；否则 iOS = App 容器 Documents、Mac = 当前目录。相对路径（契约给的是相对名）按它解析
        internal static string Doc(string p)
        {
            if (Path.IsPathRooted(p)) return p;
            string d = Environment.GetEnvironmentVariable("S2A_DOCS");
            if (string.IsNullOrEmpty(d)) d = OperatingSystem.IsIOS() ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Directory.GetCurrentDirectory();
            return Path.Combine(d, p);
        }

        [System.Runtime.InteropServices.DllImport("libc")] static extern unsafe nuint confstr(int name, byte* buf, nuint len);
        static unsafe string UserCacheDir()
        {
            const int CS_DARWIN_USER_CACHE_DIR = 65538; byte* b = stackalloc byte[1024];
            try { nuint n = confstr(CS_DARWIN_USER_CACHE_DIR, b, 1024); return n > 1 && n <= 1024 ? new string((sbyte*)b) : null; }
            catch (Exception) { return null; }
        }

        [System.Runtime.InteropServices.DllImport("libc")] static extern int setenv(string name, string value, int overwrite);
        // Ryujinx 数据根：与 services Exports.cs 同口径
        internal static string RyuData() => Environment.GetEnvironmentVariable("S2A_RYU_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "S2A", "ryujinx");
        // 渲染器持久着色器缓存目录（gal_prog.m 读 NXGAL_SHADER_CACHE_DIR）：起局方没给就放 Ryujinx 数据根下 gal_msl，与 Ryujinx 主机缓存同生命周期。
        // 主机缓存里的程序二进制只存各阶段键；离线库没有的键（包外、首遇现编过的）下次开机靠这里的 MSL 重建，缺了 Ryujinx 会整份重写主机缓存。
        // 写进原生环境（libc setenv）：.NET 在 Unix 上的 SetEnvironmentVariable 只改托管副本，gal_prog.m 的 getenv 看不到
        internal static string ShaderCacheDir()
        {
            string d = Environment.GetEnvironmentVariable("NXGAL_SHADER_CACHE_DIR");
            if (!string.IsNullOrEmpty(d)) return d;
            d = Path.Combine(RyuData(), "gal_msl");
            Environment.SetEnvironmentVariable("NXGAL_SHADER_CACHE_DIR", d);
            if (setenv("NXGAL_SHADER_CACHE_DIR", d, 1) != 0) Console.Error.WriteLine($"[gal] 【红】设不了 NXGAL_SHADER_CACHE_DIR={d}：包外着色器下次开机建不回，Ryujinx 会整份重写主机缓存");
            return d;
        }

        internal static void Run()
        {
            ShaderCacheDir();
            if (_done) return; _done = true;
            if (Environment.GetEnvironmentVariable("S2A_COLD_START") != "1") return;
            long files = 0, bytes = 0; var what = new System.Text.StringBuilder();
            void Clear(string dir, string tag)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { what.Append($"{tag}（无）；"); return; }
                long f0 = files, b0 = bytes;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { long n = new FileInfo(f).Length; File.Delete(f); files++; bytes += n; }
                    catch (Exception e) { what.Append($"【红】删不掉 {f}：{e.Message}；"); }
                }
                what.Append($"{tag} {files - f0} 个 {(bytes - b0) / 1024} KB；");
            }
            Clear(ShaderCacheDir(), "渲染器着色器缓存");
            string ryu = RyuData();
            string games = Path.Combine(ryu, "games");
            if (Directory.Exists(games)) foreach (string g in Directory.EnumerateDirectories(games)) Clear(Path.Combine(g, "cache"), $"Ryujinx 主机缓存 {Path.GetFileName(g)}");
            else what.Append("Ryujinx 主机缓存（无）；");
            string pd = Environment.GetEnvironmentVariable("NXGAL_PSO_DEV_DIR");
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (string.IsNullOrEmpty(pd)) pd = Path.Combine(home, "Library", "Application Support", "S2A", "pso_dev");
            if (Environment.GetEnvironmentVariable("S2A_COLD_KEEP_PSODEV") == "1") what.Append("渲染器设备归档（S2A_COLD_KEEP_PSODEV=1，保留）；");
            else { Clear(pd, "渲染器设备归档"); Clear(Path.Combine(home, "Library", "Caches", "S2A", "pso_dev"), "渲染器设备归档（老位置 Caches）"); }
            if (OperatingSystem.IsIOS())
            {
                string c1 = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library", "Caches", "com.apple.metal");
                Clear(c1, "Metal 系统缓存（容器 Library/Caches）");
                string uc = UserCacheDir();   // DARWIN_USER_CACHE_DIR：iOS 上也在本 App 容器里，与上一条可能是同一处
                if (uc != null && Path.GetFullPath(Path.Combine(uc, "com.apple.metal")) != Path.GetFullPath(c1)) Clear(Path.Combine(uc, "com.apple.metal"), "Metal 系统缓存（DARWIN_USER_CACHE_DIR）");
            }
            else what.Append("Metal 系统缓存（Mac 上 DARWIN_USER_CACHE_DIR 是整个用户共用，不在本进程容器，未清）；");
            string line = $"冷启动：已清 {files} 个文件 {bytes / 1024} KB——{what}\n";
            try { File.WriteAllText(Doc("cold_start.txt"), line); } catch (Exception e) { Console.Error.WriteLine($"[gal] 【红】cold_start.txt 写不了：{e.Message}"); }
            Console.Error.Write("[gal] " + line);
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 S2Arm contributors
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using S2Arm.Launcher.Protocol;

internal static class WindowsLibraryCases
{
    public static async Task Run(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temp, "s2arm-private-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var privateRoot = Path.Combine(root, "Private");
            string file;
            using (var request = PrivateJobRequest.Create(privateRoot, "{\"schema\":1}"))
            {
                file = request.FilePath;
                check(File.ReadAllText(file) == "{\"schema\":1}", "private request content round trips");
                check(new FileInfo(file).GetAccessControl().AreAccessRulesProtected, "request owns protected DACL from creation");
                var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("S2A_TEST_PYTHON")!)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); import winproc; assert winproc.request_is_private(Path(sys.argv[2])), 'backend rejected request ACL'; print('private=True')");
                info.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("S2A_TEST_ENGINE")!, "pack", "jobs"));
                info.ArgumentList.Add(file);
                using var process = Process.Start(info)!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout; var error = await stderr;
                check(process.ExitCode == 0 && output.Contains("private=True"), "actual backend accepts Windows request DACL: " + error);
            }
            check(!File.Exists(file), "private request deleted after use");
            var weak = Path.Combine(root, "Weak");
            var acl = new DirectorySecurity();
            var sid = WindowsIdentity.GetCurrent().User!;
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            new DirectoryInfo(weak).Create(acl);
            try { using var bad = PrivateJobRequest.Create(weak, "{}"); throw new Exception("accepted public inherited request access"); }
            catch (IOException) { check(true, "reject ACL granting inherited request access to Everyone"); }
            check(!Directory.EnumerateFiles(weak).Any(), "rejected ACL writes no request data");
            // #580：私有目录被 Directory.CreateDirectory 先建出来（继承临时目录的权限：本人 / SYSTEM / Administrators），原先永远报 inherits permissions
            var inherited = Path.Combine(root, "Inherited");
            Directory.CreateDirectory(Path.Combine(inherited, "jobs"));
            {   // 玩家机上普通用户建的目录属主就是本人；管理员会话里缺省属主是 Administrators 组，夹具改回本人，只留「继承」这一个问题。
                //   只写属主这一节（新建的描述符只设了 Owner）：取回整套再写回会顺带把权限改成不继承，夹具就不像玩家的目录了
                var own = new DirectorySecurity();
                own.SetOwner(sid);
                new DirectoryInfo(inherited).SetAccessControl(own);
            }
            check(!new DirectoryInfo(inherited).GetAccessControl().AreAccessRulesProtected, "fixture: directory starts with inherited DACL");
            check(sid.Equals(new DirectoryInfo(inherited).GetAccessControl().GetOwner(typeof(SecurityIdentifier))), "fixture: directory owned by the current user");
            using (var healed = PrivateJobRequest.Create(inherited, "{}"))
                check(File.Exists(healed.FilePath), "inherited-only private root is healed and accepts a request");
            var after = new DirectoryInfo(inherited).GetAccessControl();
            check(after.AreAccessRulesProtected, "healed private root no longer inherits");
            check(after.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl),
                "healed private root keeps the current user's full control as an explicit rule");
            var engine = Path.Combine(root, "中文 & %S2A_MARKER% !", "Engine");
            var runtime = Path.Combine(root, "Runtime");
            Directory.CreateDirectory(Path.Combine(engine, "pack", "jobs"));
            Directory.CreateDirectory(runtime);
            var python = Environment.GetEnvironmentVariable("S2A_TEST_PYTHON")!;
            await File.WriteAllTextAsync(Path.Combine(runtime, "s2arm_env.cmd"),
                "@echo off\r\nset \"PATH=" + Path.GetDirectoryName(python) + ";%PATH%\"\r\n");
            await File.WriteAllTextAsync(Path.Combine(engine, "pack", "jobs", "main.py"), """
import argparse,json,os,sys
p=argparse.ArgumentParser(); p.add_argument('command'); p.add_argument('--request'); p.add_argument('--private-root'); p.add_argument('--task'); a=p.parse_args()
if a.command=='cancel':
    from pathlib import Path
    Path(a.private_root,'cancel-called').write_text(a.task)
    sys.exit(0)
assert a.command=='run'
with open(a.request,encoding='utf-8') as f: r=json.load(f)
assert r['schema']==1 and os.path.normcase(r['engineDir'])==os.path.normcase(os.getcwd()), 'job selected engine reaches backend'
assert r['operation'] in ['setup','catalog','keys','import']
assert r['library'].endswith('Output') and r.get('libraryId')=='opaque-id'
if r.get('paths')==['wait']:
    import time
    from pathlib import Path
    while not Path(r['privateRoot'],'cancel-called').exists(): time.sleep(.02)
    assert Path(r['privateRoot'],'cancel-called').read_text()==r['jobId']
    print(json.dumps(dict(schema=1,jobId=r['jobId'],seq=1,type='result',operation=r['operation'],state='cancelled',message='cancelled',result={})))
    sys.exit(130)
assert r.get('paths')==['C:/Synthetic/中文 & %NAME% !.nsp'], 'original paths reach job request'
print(json.dumps(dict(schema=1,jobId=r['jobId'],taskId=r['jobId'],seq=1,type='result',operation=r['operation'],state='succeeded',message='合成操作完成',result={'accepted':r['operation']}),ensure_ascii=False))
print('request-file='+a.request,file=sys.stderr)
""");
            var paths = new LauncherPaths(engine, runtime, Path.Combine(root, "Shell"));
            foreach (var operation in new[] { "setup", "catalog", "keys", "import" })
            {
                var job = await LibraryJobClient.RunAsync(paths, privateRoot, Path.Combine(root, "Output"), "opaque-id",
                    operation, new[] { "C:/Synthetic/中文 & %NAME% !.nsp" }, CancellationToken.None);
                check(job.Report.Succeeded && job.Report.Result.GetProperty("accepted").GetString() == operation,
                    "literal request paths and selected Engine reach job " + operation);
                var requestPath = job.Output.StandardError.Split('\n').Single(x => x.StartsWith("request-file="))[13..].Trim();
                check(!File.Exists(requestPath), "job temporary request cleaned for " + operation);
            }
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var timer = Stopwatch.StartNew();
            try
            {
                await LibraryJobClient.RunAsync(paths, privateRoot, Path.Combine(root, "Output"), "opaque-id", "import", ["wait"], cancel.Token);
                throw new Exception("accepted library cancellation");
            }
            catch (OperationCanceledException)
            { check(File.Exists(Path.Combine(privateRoot, "cancel-called")) && timer.Elapsed < TimeSpan.FromSeconds(7),
                "library cancellation uses main.py cancel before owned process fallback"); }
            check(!Directory.GetFiles(privateRoot, "request-*.json").Any(), "cancelled job request removed after process exits");
            Console.WriteLine("Windows library cases: 16 assertions passed (6 ACL + 8 invocation + 2 cancellation; no device/game/key probe)");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Private request test cleanup escaped temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}

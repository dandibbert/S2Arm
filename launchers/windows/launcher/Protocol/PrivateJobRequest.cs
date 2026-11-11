// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 S2Arm contributors
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace S2Arm.Launcher.Protocol;

// Requests carry player paths. The parent is private before a request file is created.
public sealed class PrivateJobRequest : IDisposable
{
    public string FilePath { get; }
    private PrivateJobRequest(string path) => FilePath = path;

    // 必须先于私有目录下的任何子目录调用：Directory.CreateDirectory 顺带建出的父目录带继承权限，之后的作业会被权限校验拒绝。
    public static void EnsurePrivateRoot(string privateRoot)
    {
        if (OperatingSystem.IsWindows()) PrepareWindowsRoot(privateRoot);
    }

    public static PrivateJobRequest Create(string privateRoot, string json)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows request ACL required.");
        var directory = PrepareWindowsRoot(privateRoot);
        var path = Path.Combine(directory, "request-" + Guid.NewGuid().ToString("N") + ".json");
        var created = false;
        try
        {
            using var file = CreateWindowsFile(path);
            created = true;
            var bytes = Encoding.UTF8.GetBytes(json);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            return new(path);
        }
        catch { if (created) File.Delete(path); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new IOException("Current user SID is missing.");
        var security = new FileSecurity();
        security.SetOwner(current);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static string PrepareWindowsRoot(string path)
    {
        path = Path.GetFullPath(path);
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new IOException("Current user SID is missing.");
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            var security = new DirectorySecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Private job root cannot be a reparse point.");
        var acl = directory.GetAccessControl();
        if (!current.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new IOException("Private job root is not owned by this user.");
        var readers = FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes |
            FileSystemRights.ReadPermissions | FileSystemRights.Write | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.FileSystemRights & readers) != 0 && !rule.IdentityReference.Equals(current) &&
                !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(administrators))
                throw new IOException("Private job root grants another identity access.");
        }
        if (!acl.AreAccessRulesProtected)
        {
            // 自愈（#580）：目录已存在、属主是本人、不是重解析点、上面也没有给其他身份的权限（都已核过），只是还继承上级——
            //   常见于先用 Directory.CreateDirectory 建了子目录、顺带建出的父目录（见 EnsurePrivateRoot 的说明）。原先这里直接报错，
            //   玩家每次添加游戏都卡在「Private job root inherits permissions」，换游戏库位置也没用（这个目录不在游戏库里）。
            //   就地改成不继承、把现有规则原样转成显式规则：有效权限不变，只是以后上级目录的变动不再传进来
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            directory.SetAccessControl(acl);
            directory.Refresh();
            if (!directory.GetAccessControl().AreAccessRulesProtected) throw new IOException("Private job root inherits permissions.");
        }
        return path;
    }

    public void Dispose() => File.Delete(FilePath);
}

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using ManagerWindows.Core.Abstractions;
using static ManagerWindows.Platform.NativeMethods;

namespace ManagerWindows.Platform;

/// <summary>
/// Directory junction (mount point) của NTFS. Khác symlink: không cần quyền admin hay Developer Mode,
/// chỉ trỏ được tới thư mục trên ổ cục bộ — đúng nhu cầu chuyển AppData sang ổ D.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JunctionApi : ILinkApi
{
    public void CreateDirectoryLink(string linkPath, string targetPath)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        Directory.CreateDirectory(linkPath);
        try
        {
            using var h = CreateFile(linkPath, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception();

            var buffer = BuildMountPointBuffer(target);
            if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception();
        }
        catch
        {
            // Chỉ xoá thư mục rỗng vừa tạo, không đụng gì khác.
            try { Directory.Delete(linkPath, false); } catch { }
            throw;
        }
    }

    /// <summary>REPARSE_DATA_BUFFER cho IO_REPARSE_TAG_MOUNT_POINT.</summary>
    internal static byte[] BuildMountPointBuffer(string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        // PathBuffer = substitute + NUL + print + NUL
        var pathBufferLength = substitute.Length + 2 + print.Length + 2;
        var dataLength = 8 + pathBufferLength;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(IO_REPARSE_TAG_MOUNT_POINT);
        w.Write((ushort)dataLength);
        w.Write((ushort)0);                           // Reserved
        w.Write((ushort)0);                           // SubstituteNameOffset
        w.Write((ushort)substitute.Length);           // SubstituteNameLength
        w.Write((ushort)(substitute.Length + 2));     // PrintNameOffset
        w.Write((ushort)print.Length);                // PrintNameLength
        w.Write(substitute);
        w.Write((ushort)0);
        w.Write(print);
        w.Write((ushort)0);
        return ms.ToArray();
    }

    public string? GetLinkTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0 || !info.Exists && info.LinkTarget == null) return null;
        var target = info.LinkTarget;
        return target?.StartsWith(@"\??\") == true ? target[4..] : target;
    }

    /// <summary>Gỡ junction: chỉ xoá mục liên kết, không xoá nội dung thư mục đích.</summary>
    public void RemoveLink(string linkPath)
    {
        var info = new DirectoryInfo(linkPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException($"{linkPath} không phải junction");
        info.Delete(false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeApi : IVolumeApi
{
    public VolumeInfo? Get(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return null;
        var d = new DriveInfo(root);
        if (!d.IsReady) return null;
        return new VolumeInfo(
            string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
            d.DriveType == DriveType.Fixed,
            d.AvailableFreeSpace,
            root.TrimEnd('\\'));
    }
}

/// <summary>
/// Copy bằng robocopy: giữ thuộc tính và thời gian, bỏ qua junction bên trong (/XJ).
/// Quyền NTFS kế thừa từ thư mục đích riêng tư (xem <see cref="PrivateFolder"/>), không copy ACL gốc.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RobocopyCopier : IDirectoryCopier
{
    public void Copy(string source, string destination, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { source, destination, "/E", "/COPY:DAT", "/DCOPY:DAT", "/XJ", "/R:1", "/W:1", "/MT:8", "/NFL", "/NDL", "/NP", "/NJH" })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new IOException("Không chạy được robocopy");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        // Mã thoát robocopy >= 8 là có lỗi.
        if (p.ExitCode >= 8)
            throw new IOException($"robocopy lỗi (mã {p.ExitCode}): {output.Result.Trim()}");
    }
}

/// <summary>Thư mục chỉ user hiện tại, SYSTEM và Administrators truy cập được (data Zalo… là dữ liệu riêng tư).</summary>
[SupportedOSPlatform("windows")]
public static class PrivateFolder
{
    public static void Ensure(string path)
    {
        var dir = Directory.CreateDirectory(path);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const System.Security.AccessControl.InheritanceFlags inherit =
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        foreach (var sid in new System.Security.Principal.SecurityIdentifier[]
                 {
                     user,
                     new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                     new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl, inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        dir.SetAccessControl(sec);
    }
}

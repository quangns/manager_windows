using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static ManagerWindows.Platform.NativeMethods;

namespace ManagerWindows.Platform;

[SupportedOSPlatform("windows")]
public static class SystemInfo
{
    /// <summary>Số giây kể từ lần cuối người dùng chạm phím/chuột (chỉ đúng khi gọi từ phiên người dùng).</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        // dwTime là tick 32-bit: phép trừ uint xử lý đúng cả khi tick quay vòng sau 49 ngày.
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    public static (double FreePercent, ulong TotalBytes, ulong AvailBytes) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (100, 0, 0);
        return (m.ullAvailPhys * 100.0 / m.ullTotalPhys, m.ullTotalPhys, m.ullAvailPhys);
    }

    /// <summary>
    /// Windows có cập nhật LastAccessTime không. NtfsDisableLastAccessUpdate: bit thấp = 1 nghĩa là tắt
    /// (0x80000000/0x80000001 là "do hệ thống quản lý", vẫn xét bit thấp).
    /// </summary>
    public static bool LastAccessReliable()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        var v = k?.GetValue("NtfsDisableLastAccessUpdate");
        return v is not int i || (i & 1) == 0;
    }

    /// <summary>Đường dẫn exe của cửa sổ đang dùng.</summary>
    public static (int Pid, string? Exe) ForegroundProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, null);
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? (0, null) : ((int)pid, ProcessPath((int)pid));
    }

    public static string? ProcessPath(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return null;
        var buf = new char[1024];
        var size = (uint)buf.Length;
        return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }
}

/// <summary>Thông tin Office: 32 hay 64-bit, danh sách file Excel mở gần đây.</summary>
[SupportedOSPlatform("windows")]
public static class OfficeInfo
{
    public static bool? Is64Bit()
    {
        // Office Click-to-Run (Microsoft 365, 2019+)
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
            if (k?.GetValue("Platform") is string p)
                return p.Equals("x64", StringComparison.OrdinalIgnoreCase);
        // Bản MSI: khoá Outlook ghi Bitness
        foreach (var ver in new[] { "16.0", "15.0", "14.0" })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Office\{ver}\Outlook");
            if (k?.GetValue("Bitness") is string b) return b.Equals("x64", StringComparison.OrdinalIgnoreCase);
        }
        // Excel đang chạy: process 32-bit trên Windows 64-bit
        foreach (var p in Process.GetProcessesByName("EXCEL"))
            using (p)
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null) return !path.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
        return null;
    }

    /// <summary>File Excel mở gần đây theo registry MRU, mới nhất trước.</summary>
    public static List<string> RecentFiles()
    {
        var result = new List<string>();
        foreach (var ver in new[] { "16.0", "15.0" })
        {
            using var excel = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{ver}\Excel");
            if (excel == null) continue;
            var mruKeys = new List<RegistryKey?>();
            using (var userMru = excel.OpenSubKey("User MRU"))
                if (userMru != null)
                    foreach (var id in userMru.GetSubKeyNames())
                        mruKeys.Add(userMru.OpenSubKey($@"{id}\File MRU"));
            mruKeys.Add(excel.OpenSubKey("File MRU"));

            foreach (var k in mruKeys)
                using (k)
                {
                    if (k == null) continue;
                    // Giá trị dạng: [F00000000][T01D9...][O00000000]*C:\path\file.xlsx
                    foreach (var name in k.GetValueNames().Where(n => n.StartsWith("Item ")).OrderBy(n => int.TryParse(n[5..], out var i) ? i : 999))
                        if (k.GetValue(name) is string v && v.LastIndexOf('*') is var star and >= 0)
                            result.Add(v[(star + 1)..]);
                }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Đoán file Excel đang mở từ tiêu đề cửa sổ ("Bao cao.xlsx - Excel") đối chiếu với MRU.
    /// </summary>
    public static string? GuessOpenFile(string windowTitle)
    {
        var sep = windowTitle.LastIndexOf(" - ", StringComparison.Ordinal);
        var name = (sep > 0 ? windowTitle[..sep] : windowTitle).Trim();
        foreach (var suffix in new[] { "  -  Compatibility Mode", " [Compatibility Mode]", " - Saved", " [Read-Only]" })
            name = name.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0) return null;
        return RecentFiles().FirstOrDefault(f =>
            Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

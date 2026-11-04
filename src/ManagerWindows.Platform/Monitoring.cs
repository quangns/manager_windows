using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ManagerWindows.Core.Monitoring;
using Microsoft.Win32.SafeHandles;

namespace ManagerWindows.Platform;

/// <summary>% CPU toàn máy (giống Task Manager) tính từ GetSystemTimes giữa hai lần gọi.</summary>
[SupportedOSPlatform("windows")]
public sealed class CpuMeter
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    private ulong _idle, _total;

    public double Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel.Value + user.Value; // kernel đã gồm idle
        var dIdle = idle.Value - _idle;
        var dTotal = total - _total;
        var first = _total == 0;
        (_idle, _total) = (idle.Value, total);
        return first || dTotal == 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }
}

/// <summary>
/// Lấy mẫu mọi tiến trình: %CPU (so với toàn máy), RAM, phiên, có cửa sổ hay không.
/// Cửa sổ được dò một lượt bằng EnumWindows thay vì gọi Process.MainWindowHandle cho từng tiến trình.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessMonitorSampler
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private const uint GW_OWNER = 4;

    private readonly Dictionary<int, (DateTime Start, TimeSpan Cpu, DateTime At, string? Path)> _last = new();

    /// <summary>PID có cửa sổ chính hiển thị (cửa sổ cấp cao nhất, không có chủ, có tiêu đề).</summary>
    public static HashSet<int> PidsWithWindows()
    {
        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out var pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    public List<ProcessSample> Sample()
    {
        var now = DateTime.UtcNow;
        var windows = PidsWithWindows();
        var result = new List<ProcessSample>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;
                try
                {
                    var ws = p.WorkingSet64;
                    var session = p.SessionId;
                    double cpu = 0;
                    string? path = null;
                    try
                    {
                        var start = p.StartTime;
                        var total = p.TotalProcessorTime;
                        if (_last.TryGetValue(p.Id, out var prev) && prev.Start == start)
                        {
                            path = prev.Path;
                            var wall = (now - prev.At).TotalMilliseconds * Environment.ProcessorCount;
                            if (wall > 0) cpu = Math.Clamp((total - prev.Cpu).TotalMilliseconds * 100.0 / wall, 0, 100);
                        }
                        else path = SystemInfo.ProcessPath(p.Id);
                        _last[p.Id] = (start, total, now, path);
                    }
                    catch (Win32Exception) { }               // tiến trình được bảo vệ: không đọc được thời gian CPU
                    catch (InvalidOperationException) { continue; }
                    seen.Add(p.Id);
                    result.Add(new ProcessSample(p.Id, p.ProcessName, path, session, windows.Contains(p.Id), cpu, ws));
                }
                catch (InvalidOperationException) { } // vừa thoát
            }
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        return result;
    }
}

/// <summary>Giải phóng RAM: ép tiến trình trả working set, và xả danh sách standby của Windows (cần quyền SYSTEM/admin).</summary>
[SupportedOSPlatform("windows")]
public static class MemoryApi
{
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);

    /// <summary>Đẩy trang bộ nhớ của tiến trình ra khỏi RAM; app tự nạp lại phần cần khi dùng tiếp.</summary>
    public static bool TrimWorkingSet(int pid)
    {
        using var h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, pid);
        return !h.IsInvalid && EmptyWorkingSet(h);
    }

    /// <summary>Trim mọi tiến trình thoả điều kiện; trả về số thành công/thất bại.</summary>
    public static (int Ok, int Failed) TrimAll(Func<Process, bool> include)
    {
        int ok = 0, failed = 0;
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4 || !include(p)) continue;
                    if (TrimWorkingSet(p.Id)) ok++; else failed++;
                }
                catch (InvalidOperationException) { }
            }
        return (ok, failed);
    }

    // ---- Xả standby list (như RAMMap "Empty Standby List") ----
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TOKEN_PRIVILEGES state,
        uint len, IntPtr prev, IntPtr retLen);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    /// <summary>Xả bộ nhớ đệm standby. Chỉ thành công khi chạy với quyền SYSTEM/Administrators (dịch vụ nền).</summary>
    public static bool PurgeStandbyList()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                return false;
        }
        var cmd = MemoryPurgeStandbyList;
        return NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)) == 0;
    }
}

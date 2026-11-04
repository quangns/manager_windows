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
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IO_COUNTERS counters);

    private readonly Dictionary<int, (DateTime Start, TimeSpan Cpu, DateTime At, string? Path, ulong Io)> _last = new();

    /// <summary>Tổng byte đọc + ghi của tiến trình (gồm cả I/O mạng/thiết bị, như cột I/O của Task Manager).</summary>
    private static ulong IoBytes(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return !h.IsInvalid && GetProcessIoCounters(h, out var c) ? c.ReadTransferCount + c.WriteTransferCount : 0;
    }

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
                    double cpu = 0, io = 0;
                    string? path = null;
                    try
                    {
                        var start = p.StartTime;
                        var total = p.TotalProcessorTime;
                        var ioBytes = IoBytes(p.Id);
                        if (_last.TryGetValue(p.Id, out var prev) && prev.Start == start)
                        {
                            path = prev.Path;
                            var seconds = (now - prev.At).TotalSeconds;
                            var wall = seconds * 1000 * Environment.ProcessorCount;
                            if (wall > 0) cpu = Math.Clamp((total - prev.Cpu).TotalMilliseconds * 100.0 / wall, 0, 100);
                            if (seconds > 0 && ioBytes >= prev.Io) io = (ioBytes - prev.Io) / seconds;
                        }
                        else path = SystemInfo.ProcessPath(p.Id);
                        _last[p.Id] = (start, total, now, path, ioBytes);
                    }
                    catch (Win32Exception) { }               // tiến trình được bảo vệ: không đọc được thời gian CPU
                    catch (InvalidOperationException) { continue; }
                    seen.Add(p.Id);
                    result.Add(new ProcessSample(p.Id, p.ProcessName, path, session, windows.Contains(p.Id), cpu, ws, io));
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

/// <summary>Một lần đo ổ đĩa: % bận của ổ bận nhất (Active time như Task Manager) và tổng tốc độ đọc ghi.</summary>
public sealed record DiskSample(double BusyPercent, string? BusiestDisk, double BytesPerSec);

/// <summary>
/// Đo ổ đĩa vật lý bằng PDH với tên bộ đếm tiếng Anh (PdhAddEnglishCounter) nên chạy đúng trên Windows mọi ngôn ngữ.
/// Lần đo đầu chưa có số (PDH cần hai lần thu), trả 0.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double DoubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr Name; public PDH_FMT_COUNTERVALUE Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private readonly IntPtr _query;
    private readonly IntPtr _idle, _bytes;
    private readonly bool _ok;

    public DiskMeter()
    {
        try
        {
            _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                  && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out _idle) == 0
                  && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Bytes/sec", IntPtr.Zero, out _bytes) == 0;
            if (_ok) PdhCollectQueryData(_query);
        }
        catch (DllNotFoundException) { _ok = false; }
    }

    public DiskSample Sample()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) return new DiskSample(0, null, 0);
        double busy = 0, bytes = 0;
        string? busiest = null;
        foreach (var (name, idle) in Read(_idle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100))
        {
            if (name == "_Total") continue;
            var b = Math.Clamp(100 - idle, 0, 100);
            if (b > busy) (busy, busiest) = (b, DiskLabel(name));
        }
        foreach (var (name, v) in Read(_bytes, PDH_FMT_DOUBLE))
            if (name == "_Total") bytes = v;
        return new DiskSample(busy, busiest, bytes);
    }

    /// <summary>Tên phiên bản PDH dạng "0 C: D:" → "C: D:" (ổ có thể chứa nhiều phân vùng).</summary>
    private static string DiskLabel(string instance)
    {
        var i = instance.IndexOf(' ');
        return i > 0 ? instance[(i + 1)..] : instance;
    }

    private static List<(string Name, double Value)> Read(IntPtr counter, uint format)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, format, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, format, ref size, out var count, buffer) != 0) return list;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus is 0 or 1) // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    public void Dispose()
    {
        if (_ok) PdhCloseQuery(_query);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ManagerWindows.Core.Monitoring;
using Microsoft.Win32.SafeHandles;

namespace ManagerWindows.Platform;

/// <summary>Chụp toàn bộ tiến trình kèm PID cha, thời điểm khởi động, cửa sổ treo — dùng để tìm tiến trình sót.</summary>
[SupportedOSPlatform("windows")]
public static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>PID → PID cha, một lượt qua Toolhelp32 (nhanh, không cần WMI).</summary>
    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        using var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid) return map;
        var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snap, ref e)) return map;
        do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        while (Process32NextW(snap, ref e));
        return map;
    }

    /// <summary>
    /// Tên tiến trình (không đuôi .exe) đang có cửa sổ hiển thị. Nhẹ: chỉ duyệt cửa sổ + một lượt Toolhelp32,
    /// không mở từng tiến trình — gọi mỗi phút được.
    /// </summary>
    public static HashSet<string> WindowedAppNames()
    {
        var pids = ProcessMonitorSampler.PidsWithWindows();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pids.Count == 0) return names;
        using var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid) return names;
        var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snap, ref e)) return names;
        do
        {
            if (pids.Contains((int)e.th32ProcessID)) names.Add(Path.GetFileNameWithoutExtension(e.szExeFile));
        }
        while (Process32NextW(snap, ref e));
        return names;
    }

    /// <summary>Thời điểm khởi động và tổng thời gian CPU (đọc được cả tiến trình chạy quyền cao với quyền truy vấn hạn chế).</summary>
    public static (DateTime? Start, TimeSpan Cpu) Times(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid || !GetProcessTimes(h, out var creation, out _, out var kernel, out var user)) return (null, TimeSpan.Zero);
        return (DateTime.FromFileTimeUtc(creation), TimeSpan.FromTicks(kernel + user));
    }

    /// <summary>PID có cửa sổ hiển thị đang "Not responding".</summary>
    public static HashSet<int> HungWindowPids()
    {
        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && IsHungAppWindow(h))
            {
                GetWindowThreadProcessId(h, out var pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>Chụp mọi tiến trình thành <see cref="ProcInfo"/>.</summary>
    public static List<ProcInfo> Snapshot()
    {
        var parents = ParentMap();
        var windows = ProcessMonitorSampler.PidsWithWindows();
        var hung = HungWindowPids();
        var result = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var (start, cpu) = Times(p.Id);
                    result.Add(new ProcInfo(p.Id, parents.GetValueOrDefault(p.Id), p.ProcessName, SystemInfo.ProcessPath(p.Id),
                        p.SessionId, start, windows.Contains(p.Id), hung.Contains(p.Id), cpu, p.WorkingSet64));
                }
                catch (InvalidOperationException) { } // vừa thoát
            }
        }
        return result;
    }
}

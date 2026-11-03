using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ManagerWindows.Core.Priority;
using static ManagerWindows.Platform.NativeMethods;

namespace ManagerWindows.Platform;

[SupportedOSPlatform("windows")]
public static class ProcessPriorityApi
{
    public static PriorityLevel? Get(int pid)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return null;
        return FromClass(GetPriorityClass(h));
    }

    /// <summary>Đặt mức ưu tiên CPU, EcoQoS và ưu tiên bộ nhớ. Trả false nếu không đủ quyền.</summary>
    public static bool Set(int pid, PriorityLevel level, bool ecoQos)
    {
        using var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h.IsInvalid) return false;
        if (!SetPriorityClass(h, ToClass(level))) return false;
        SetEcoQoS(h, ecoQos);
        SetMemoryPriority(h, ecoQos ? MEMORY_PRIORITY_LOW : MEMORY_PRIORITY_NORMAL);
        return true;
    }

    /// <summary>EcoQoS (Windows 11): cho process chạy ở nhân tiết kiệm điện / xung thấp. Windows 10 bỏ qua.</summary>
    private static void SetEcoQoS(Microsoft.Win32.SafeHandles.SafeProcessHandle h, bool on)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        SetStruct(h, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, state);
    }

    private static void SetMemoryPriority(Microsoft.Win32.SafeHandles.SafeProcessHandle h, uint priority) =>
        SetStruct(h, PROCESS_INFORMATION_CLASS.ProcessMemoryPriority, new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority });

    private static void SetStruct<T>(Microsoft.Win32.SafeHandles.SafeProcessHandle h, PROCESS_INFORMATION_CLASS cls, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            SetProcessInformation(h, cls, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static uint ToClass(PriorityLevel l) => l switch
    {
        PriorityLevel.Idle => IDLE_PRIORITY_CLASS,
        PriorityLevel.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        PriorityLevel.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        PriorityLevel.High => HIGH_PRIORITY_CLASS, // không bao giờ dùng REALTIME
        _ => NORMAL_PRIORITY_CLASS,
    };

    private static PriorityLevel? FromClass(uint c) => c switch
    {
        IDLE_PRIORITY_CLASS => PriorityLevel.Idle,
        BELOW_NORMAL_PRIORITY_CLASS => PriorityLevel.BelowNormal,
        NORMAL_PRIORITY_CLASS => PriorityLevel.Normal,
        ABOVE_NORMAL_PRIORITY_CLASS => PriorityLevel.AboveNormal,
        HIGH_PRIORITY_CLASS => PriorityLevel.High,
        _ => null, // REALTIME hoặc lỗi: không đụng
    };
}

/// <summary>Chụp danh sách process kèm %CPU (tính từ chênh lệch thời gian CPU giữa hai lần chụp).</summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessSampler
{
    private readonly Dictionary<int, (DateTime StartTime, TimeSpan Cpu, DateTime At)> _last = new();

    public List<ProcessSnapshot> Sample(int foregroundPid)
    {
        var now = DateTime.UtcNow;
        var result = new List<ProcessSnapshot>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;
                double cpu = 0;
                long ws;
                try
                {
                    ws = p.WorkingSet64;
                    var start = p.StartTime;
                    var total = p.TotalProcessorTime;
                    // So khớp cả StartTime để không nhầm khi PID bị dùng lại.
                    if (_last.TryGetValue(p.Id, out var prev) && prev.StartTime == start)
                    {
                        var wall = (now - prev.At).TotalMilliseconds * Environment.ProcessorCount;
                        if (wall > 0) cpu = Math.Clamp((total - prev.Cpu).TotalMilliseconds * 100.0 / wall, 0, 100);
                    }
                    _last[p.Id] = (start, total, now);
                }
                catch { continue; } // process hệ thống không đọc được
                seen.Add(p.Id);
                result.Add(new ProcessSnapshot(p.Id, p.ProcessName, ProcessPriorityApi.Get(p.Id), p.Id == foregroundPid, cpu, ws));
            }
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        return result;
    }
}

/// <summary>
/// Áp dụng quyết định của <see cref="PriorityPolicy"/> và nhớ mức ban đầu để trả lại khi process ra khỏi
/// diện ưu tiên hoặc khi tắt tính năng/thoát app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PriorityApplier(Func<int, PriorityLevel, bool, bool>? elevatedFallback = null)
{
    private readonly Dictionary<int, (PriorityLevel Original, PriorityLevel Applied, string Name)> _managed = new();

    public IReadOnlyDictionary<int, (PriorityLevel Original, PriorityLevel Applied, string Name)> Managed => _managed;

    public void Apply(IReadOnlyCollection<PriorityDecision> decisions, IReadOnlyCollection<ProcessSnapshot> snapshot)
    {
        var current = snapshot.ToDictionary(p => p.Pid);
        var wanted = decisions.ToDictionary(d => d.Pid);

        // Trả lại mức cũ cho process không còn trong diện ưu tiên.
        foreach (var (pid, info) in _managed.ToList())
        {
            if (wanted.ContainsKey(pid)) continue;
            if (current.TryGetValue(pid, out var p) && p.Name == info.Name) SetLevel(pid, info.Original, false);
            _managed.Remove(pid);
        }

        foreach (var d in decisions)
        {
            if (!current.TryGetValue(d.Pid, out var p) || p.Current == null) continue;
            if (_managed.TryGetValue(d.Pid, out var m) && m.Applied == d.Target && p.Current == d.Target) continue;
            var original = _managed.TryGetValue(d.Pid, out var existing) ? existing.Original : p.Current.Value;
            if (SetLevel(d.Pid, d.Target, d.EcoQoS))
                _managed[d.Pid] = (original, d.Target, d.Name);
        }
    }

    public void RestoreAll()
    {
        foreach (var (pid, info) in _managed)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName == info.Name) SetLevel(pid, info.Original, false);
            }
            catch (ArgumentException) { } // đã thoát
        }
        _managed.Clear();
    }

    private bool SetLevel(int pid, PriorityLevel level, bool eco) =>
        ProcessPriorityApi.Set(pid, level, eco) || (elevatedFallback?.Invoke(pid, level, eco) ?? false);
}

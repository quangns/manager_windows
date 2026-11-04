namespace ManagerWindows.Core.Monitoring;

/// <summary>App: có cửa sổ. Background: chạy ngầm trong phiên người dùng. System: dịch vụ, tiến trình Windows.</summary>
public enum ProcessKind { App, Background, System }

public sealed record ProcessSample(
    int Pid,
    string Name,
    string? Path,
    int SessionId,
    bool HasWindow,
    double CpuPercent,
    long WorkingSetBytes,
    double IoBytesPerSec = 0);

/// <summary>Các tiến trình cùng tên gộp thành một app (Chrome có thể vài chục tiến trình).</summary>
public sealed record AppGroup(
    string Name,
    string? Path,
    ProcessKind Kind,
    int Count,
    double CpuPercent,
    long WorkingSetBytes,
    IReadOnlyList<int> Pids,
    double IoBytesPerSec = 0);

public static class ProcessGrouping
{
    public static List<AppGroup> Group(IEnumerable<ProcessSample> samples, int currentSession, string windowsDir)
    {
        return samples
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var path = g.Select(s => s.Path).FirstOrDefault(p => p != null);
                return new AppGroup(g.Key, path, Classify(g.ToList(), path, currentSession, windowsDir), g.Count(),
                    g.Sum(s => s.CpuPercent), g.Sum(s => s.WorkingSetBytes), g.Select(s => s.Pid).ToList(),
                    g.Sum(s => s.IoBytesPerSec));
            })
            .ToList();
    }

    internal static ProcessKind Classify(IReadOnlyList<ProcessSample> group, string? path, int currentSession, string windowsDir)
    {
        if (group.Any(s => s.HasWindow)) return ProcessKind.App;
        // Không thuộc phiên của người dùng (session 0: dịch vụ) hoặc nằm trong thư mục Windows → hệ thống.
        if (group.All(s => s.SessionId != currentSession)) return ProcessKind.System;
        if (path != null && windowsDir.Length > 0 && path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
            return ProcessKind.System;
        return ProcessKind.Background;
    }
}

public sealed class MonitorOptions
{
    /// <summary>Báo khi app chạy ngầm dùng nhiều tài nguyên liên tục.</summary>
    public bool AlertsEnabled { get; set; } = true;
    public int CpuPercent { get; set; } = 25;
    public int RamMb { get; set; } = 1500;
    /// <summary>Vượt ngưỡng liên tục bao lâu mới báo (tránh báo khi chỉ tăng vọt chốc lát).</summary>
    public int SustainSeconds { get; set; } = 60;
    /// <summary>Đã báo một app thì bao lâu sau mới báo lại app đó.</summary>
    public int CooldownMinutes { get; set; } = 30;
    /// <summary>Tên tiến trình không cần báo (vd. app sao lưu chạy đêm).</summary>
    public List<string> Ignored { get; set; } = new();
}

public enum AlertReason { Cpu, Ram }

public sealed record BackgroundAlert(string Name, AlertReason Reason, double CpuPercent, long WorkingSetBytes);

/// <summary>Theo dõi app chạy ngầm vượt ngưỡng CPU/RAM liên tục, báo một lần rồi chờ hết thời gian nghỉ.</summary>
public sealed class BackgroundAlertTracker
{
    private readonly Dictionary<string, (DateTime? Since, DateTime? LastAlert)> _state = new(StringComparer.OrdinalIgnoreCase);

    public List<BackgroundAlert> Update(IEnumerable<AppGroup> groups, DateTime nowUtc, MonitorOptions opt)
    {
        var alerts = new List<BackgroundAlert>();
        if (!opt.AlertsEnabled) return alerts;
        var ignored = new HashSet<string>(opt.Ignored, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups.Where(g => g.Kind == ProcessKind.Background && !ignored.Contains(g.Name)))
        {
            seen.Add(g.Name);
            _state.TryGetValue(g.Name, out var st);
            var cpuOver = g.CpuPercent >= opt.CpuPercent;
            var ramOver = g.WorkingSetBytes >= (long)opt.RamMb << 20;
            if (!cpuOver && !ramOver)
            {
                _state[g.Name] = (null, st.LastAlert);
                continue;
            }

            var since = st.Since ?? nowUtc;
            var sustained = nowUtc - since >= TimeSpan.FromSeconds(opt.SustainSeconds);
            var cooled = st.LastAlert == null || nowUtc - st.LastAlert >= TimeSpan.FromMinutes(opt.CooldownMinutes);
            if (sustained && cooled)
            {
                alerts.Add(new BackgroundAlert(g.Name, cpuOver ? AlertReason.Cpu : AlertReason.Ram, g.CpuPercent, g.WorkingSetBytes));
                _state[g.Name] = (since, nowUtc);
            }
            else _state[g.Name] = (since, st.LastAlert);
        }

        // App đã thoát: quên trạng thái "đang vượt ngưỡng" nhưng nhớ lần báo để không báo dồn khi app mở lại ngay.
        foreach (var name in _state.Keys.Where(k => !seen.Contains(k)).ToList())
            _state[name] = (null, _state[name].LastAlert);
        return alerts;
    }
}

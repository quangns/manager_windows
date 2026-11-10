using ManagerWindows.Core.Priority;

namespace ManagerWindows.Core.Monitoring;

/// <summary>Một tiến trình với đủ thông tin để xét có phải tiến trình sót hay không.</summary>
public sealed record ProcInfo(
    int Pid,
    int ParentPid,
    string Name,
    string? Path,
    int SessionId,
    DateTime? StartUtc,
    bool HasWindow,
    bool IsHung,
    TimeSpan CpuTime,
    long WorkingSetBytes);

[Flags]
public enum LeftoverReason
{
    None = 0,
    /// <summary>Tiến trình cha đã thoát (hoặc PID của cha đã bị tiến trình khác dùng lại).</summary>
    Orphan = 1,
    /// <summary>Cửa sổ không phản hồi (Not responding).</summary>
    NotResponding = 2,
    /// <summary>Chạy ngầm lâu, gần như không dùng CPU nhưng giữ nhiều RAM.</summary>
    IdleBackground = 4,
    /// <summary>
    /// App đã đóng hết cửa sổ nhưng các tiến trình (kể cả tiến trình con) vẫn chạy, giữ nhiều RAM.
    /// Ví dụ Chrome/Edge bật "tiếp tục chạy ứng dụng nền khi đóng trình duyệt".
    /// </summary>
    WindowClosed = 8,
}

/// <summary>Các tiến trình sót cùng tên gộp thành một dòng.</summary>
public sealed record LeftoverGroup(
    string Name,
    string? Path,
    LeftoverReason Reasons,
    IReadOnlyList<int> Pids,
    long WorkingSetBytes,
    DateTime? OldestStartUtc);

public sealed class LeftoverOptions
{
    /// <summary>Tiến trình mới chạy chưa đủ lâu thì bỏ qua (installer, app đang khởi động…).</summary>
    public int MinAgeMinutes { get; set; } = 10;
    public int IdleHours { get; set; } = 2;
    public int IdleRamMb { get; set; } = 100;
    /// <summary>CPU trung bình từ lúc chạy dưới mức này (% một nhân) coi là không hoạt động.</summary>
    public double IdleCpuPercent { get; set; } = 0.05;
    /// <summary>Báo ở khay khi phát hiện app treo hoặc tiến trình sót giữ từ ngần này RAM.</summary>
    public bool Notify { get; set; } = true;
    public int NotifyRamMb { get; set; } = 500;
    public List<string> Ignored { get; set; } = new();
    /// <summary>App đóng cửa sổ cuối cùng quá ngần này phút mà vẫn chạy thì coi là "đã đóng nhưng vẫn chạy".</summary>
    public int WindowClosedMinutes { get; set; } = 5;
    /// <summary>Chỉ báo khi cả app (mọi tiến trình cùng tên) giữ từ ngần này RAM.</summary>
    public int WindowClosedRamMb { get; set; } = 150;
    /// <summary>
    /// App hay chạy ngầm sau khi đóng cửa sổ: báo cả khi Manager Windows chưa kịp thấy cửa sổ của nó
    /// (app mở trước khi Manager Windows chạy, hoặc tự chạy ngầm lúc đăng nhập).
    /// </summary>
    public List<string> LingeringApps { get; set; } = [.. DefaultLingeringApps];

    /// <summary>Tự dừng app chạy ngầm lâu, không hoạt động (tắt sẵn; trừ danh sách "không bao giờ ngủ" và app chạy nền hợp lệ).</summary>
    public bool AutoStopIdle { get; set; }
    /// <summary>Tự dừng app đã đóng hết cửa sổ mà vẫn chạy ngầm (tắt sẵn).</summary>
    public bool AutoStopClosed { get; set; }

    /// <summary>Trình duyệt Chromium: Chrome, Edge, Brave, Cốc Cốc (browser.exe), Opera, Vivaldi.</summary>
    public static readonly string[] DefaultLingeringApps = ["chrome", "msedge", "brave", "browser", "opera", "vivaldi"];
}

/// <summary>
/// Tìm tiến trình sót trong phiên người dùng. Chỉ đưa ra danh sách kèm lý do; dừng hay không do người dùng quyết.
/// </summary>
public static class LeftoverDetector
{
    /// <param name="lastWindowUtc">Lần cuối thấy app (theo tên tiến trình) có cửa sổ; null = không theo dõi.</param>
    /// <param name="keepRunning">App được phép chạy nền không cửa sổ (chat, mail, VPN, bộ gõ, nhạc, danh sách "không bao giờ ngủ").</param>
    public static List<LeftoverGroup> Detect(IReadOnlyList<ProcInfo> all, int currentSession, int selfPid,
        DateTime nowUtc, LeftoverOptions opt, string windowsDir = "",
        IReadOnlyDictionary<string, DateTime>? lastWindowUtc = null, IReadOnlySet<string>? keepRunning = null)
    {
        var byPid = all.ToDictionary(p => p.Pid);
        var ignored = new HashSet<string>(opt.Ignored, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<int, (ProcInfo P, LeftoverReason R)>();
        var candidates = new List<ProcInfo>();

        foreach (var p in all)
        {
            if (p.Pid == selfPid || p.Pid is 0 or 4 || p.SessionId != currentSession) continue;
            // conhost được bảo vệ khi chỉnh ưu tiên, nhưng conhost mồ côi (cửa sổ console đã đóng) là tiến trình sót điển hình.
            if (ProtectedProcesses.IsProtected(p.Name) && !p.Name.Equals("conhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (ignored.Contains(p.Name)) continue;
            candidates.Add(p);
            var age = p.StartUtc is { } s ? nowUtc - s : (TimeSpan?)null;

            var reasons = LeftoverReason.None;
            if (p.IsHung) reasons |= LeftoverReason.NotResponding;

            var oldEnough = age == null || age >= TimeSpan.FromMinutes(opt.MinAgeMinutes);
            if (!p.HasWindow && oldEnough && IsOrphan(p, byPid)) reasons |= LeftoverReason.Orphan;

            if (!p.HasWindow && !InWindowsDir(p, windowsDir) && age is { } a && a >= TimeSpan.FromHours(opt.IdleHours)
                && p.WorkingSetBytes >= (long)opt.IdleRamMb << 20
                && p.CpuTime.TotalSeconds / a.TotalSeconds * 100 < opt.IdleCpuPercent)
                reasons |= LeftoverReason.IdleBackground;

            if (reasons != LeftoverReason.None) found[p.Pid] = (p, reasons);
        }

        // App đã đóng cửa sổ nhưng vẫn chạy: xét cả app (mọi tiến trình cùng tên), vì RAM thường chia ra hàng chục tiến trình con nhỏ.
        var lingering = new HashSet<string>(opt.LingeringApps, StringComparer.OrdinalIgnoreCase);
        // Tên tiến trình trên Windows không phân biệt hoa thường: gộp về một bảng tra không phân biệt, giữ lần thấy gần nhất.
        Dictionary<string, DateTime>? seen = null;
        if (lastWindowUtc != null)
        {
            seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, t) in lastWindowUtc)
                if (!seen.TryGetValue(name, out var prev) || t > prev) seen[name] = t;
        }
        foreach (var g in candidates.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (keepRunning?.Contains(g.Key) == true || g.Any(p => p.HasWindow) || g.Any(p => InWindowsDir(p, windowsDir))) continue;
            if (g.Sum(p => p.WorkingSetBytes) < (long)opt.WindowClosedRamMb << 20) continue;
            var closedLongEnough = seen != null && seen.TryGetValue(g.Key, out var last)
                ? nowUtc - last >= TimeSpan.FromMinutes(opt.WindowClosedMinutes)
                : lingering.Contains(g.Key) && g.Min(p => p.StartUtc) is var oldest
                  && (oldest == null || nowUtc - oldest >= TimeSpan.FromMinutes(opt.MinAgeMinutes));
            if (!closedLongEnough) continue;
            foreach (var p in g)
                found[p.Pid] = found.TryGetValue(p.Pid, out var f) ? (p, f.R | LeftoverReason.WindowClosed) : (p, LeftoverReason.WindowClosed);
        }

        return found.Values
            .GroupBy(x => x.P.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LeftoverGroup(
                g.Key,
                g.Select(x => x.P.Path).FirstOrDefault(x => x != null),
                g.Aggregate(LeftoverReason.None, (r, x) => r | x.R),
                g.Select(x => x.P.Pid).ToList(),
                g.Sum(x => x.P.WorkingSetBytes),
                g.Select(x => x.P.StartUtc).Where(t => t != null).DefaultIfEmpty().Min()))
            .OrderByDescending(g => g.Reasons.HasFlag(LeftoverReason.NotResponding))
            .ThenByDescending(g => g.WorkingSetBytes)
            .ToList();
    }

    /// <summary>
    /// Nhóm được phép tự dừng theo tuỳ chọn: chạy ngầm lâu không hoạt động (<see cref="LeftoverOptions.AutoStopIdle"/>)
    /// hoặc đã đóng cửa sổ mà vẫn chạy (<see cref="LeftoverOptions.AutoStopClosed"/>).
    /// Không bao giờ tự dừng: app treo (có thể mất dữ liệu chưa lưu, để người dùng tự quyết), app trong <paramref name="neverStop"/>
    /// (danh sách "không bao giờ ngủ" + app chạy nền hợp lệ), app người dùng đã bỏ qua.
    /// </summary>
    public static List<LeftoverGroup> AutoStopTargets(IEnumerable<LeftoverGroup> groups, LeftoverOptions opt, IReadOnlySet<string> neverStop)
    {
        if (!opt.AutoStopIdle && !opt.AutoStopClosed) return [];
        var ignored = new HashSet<string>(opt.Ignored, StringComparer.OrdinalIgnoreCase);
        return groups.Where(g =>
                !g.Reasons.HasFlag(LeftoverReason.NotResponding)
                && !neverStop.Contains(g.Name) && !ignored.Contains(g.Name)
                && ((opt.AutoStopIdle && g.Reasons.HasFlag(LeftoverReason.IdleBackground))
                    || (opt.AutoStopClosed && g.Reasons.HasFlag(LeftoverReason.WindowClosed))))
            .ToList();
    }

    private static bool InWindowsDir(ProcInfo p, string windowsDir) =>
        windowsDir.Length > 0 && p.Path?.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Cha không còn, hoặc PID của cha giờ là một tiến trình khởi động SAU con (PID bị dùng lại).</summary>
    internal static bool IsOrphan(ProcInfo p, IReadOnlyDictionary<int, ProcInfo> byPid)
    {
        if (p.ParentPid is 0 or 4) return false; // tiến trình gốc của hệ thống
        if (!byPid.TryGetValue(p.ParentPid, out var parent)) return true;
        return parent.StartUtc is { } ps && p.StartUtc is { } cs && ps > cs;
    }
}

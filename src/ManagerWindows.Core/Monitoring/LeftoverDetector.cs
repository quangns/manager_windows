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
}

/// <summary>
/// Tìm tiến trình sót trong phiên người dùng. Chỉ đưa ra danh sách kèm lý do; dừng hay không do người dùng quyết.
/// </summary>
public static class LeftoverDetector
{
    public static List<LeftoverGroup> Detect(IReadOnlyList<ProcInfo> all, int currentSession, int selfPid,
        DateTime nowUtc, LeftoverOptions opt, string windowsDir = "")
    {
        var byPid = all.ToDictionary(p => p.Pid);
        var ignored = new HashSet<string>(opt.Ignored, StringComparer.OrdinalIgnoreCase);
        var found = new List<(ProcInfo P, LeftoverReason R)>();

        foreach (var p in all)
        {
            if (p.Pid == selfPid || p.Pid is 0 or 4 || p.SessionId != currentSession) continue;
            // conhost được bảo vệ khi chỉnh ưu tiên, nhưng conhost mồ côi (cửa sổ console đã đóng) là tiến trình sót điển hình.
            if (ProtectedProcesses.IsProtected(p.Name) && !p.Name.Equals("conhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (ignored.Contains(p.Name)) continue;
            var age = p.StartUtc is { } s ? nowUtc - s : (TimeSpan?)null;

            var reasons = LeftoverReason.None;
            if (p.IsHung) reasons |= LeftoverReason.NotResponding;

            var oldEnough = age == null || age >= TimeSpan.FromMinutes(opt.MinAgeMinutes);
            if (!p.HasWindow && oldEnough && IsOrphan(p, byPid)) reasons |= LeftoverReason.Orphan;

            var inWindowsDir = windowsDir.Length > 0 && p.Path?.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase) == true;
            if (!p.HasWindow && !inWindowsDir && age is { } a && a >= TimeSpan.FromHours(opt.IdleHours)
                && p.WorkingSetBytes >= (long)opt.IdleRamMb << 20
                && p.CpuTime.TotalSeconds / a.TotalSeconds * 100 < opt.IdleCpuPercent)
                reasons |= LeftoverReason.IdleBackground;

            if (reasons != LeftoverReason.None) found.Add((p, reasons));
        }

        return found
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

    /// <summary>Cha không còn, hoặc PID của cha giờ là một tiến trình khởi động SAU con (PID bị dùng lại).</summary>
    internal static bool IsOrphan(ProcInfo p, IReadOnlyDictionary<int, ProcInfo> byPid)
    {
        if (p.ParentPid is 0 or 4) return false; // tiến trình gốc của hệ thống
        if (!byPid.TryGetValue(p.ParentPid, out var parent)) return true;
        return parent.StartUtc is { } ps && p.StartUtc is { } cs && ps > cs;
    }
}

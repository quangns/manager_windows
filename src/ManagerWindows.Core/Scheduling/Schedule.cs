namespace ManagerWindows.Core.Scheduling;

public enum Frequency { Off, Daily, Weekly }

public sealed class Schedule
{
    public Frequency Frequency { get; set; } = Frequency.Weekly;
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    /// <summary>Giờ trong ngày (giờ máy) được phép bắt đầu.</summary>
    public int Hour { get; set; } = 12;
    /// <summary>Chỉ chạy khi người dùng đã rảnh ít nhất N phút (0 = không cần).</summary>
    public int RequireIdleMinutes { get; set; } = 10;
    public bool SkipOnBattery { get; set; } = true;
}

public static class ScheduleEvaluator
{
    /// <summary>Mốc chạy gần nhất đã tới (≤ now). Lỡ mốc vì tắt máy thì chạy bù ở lần kiểm tra kế tiếp.</summary>
    public static DateTime? LastDueAt(Schedule s, DateTime nowLocal)
    {
        if (s.Frequency == Frequency.Off) return null;
        var today = nowLocal.Date.AddHours(Math.Clamp(s.Hour, 0, 23));
        if (s.Frequency == Frequency.Daily)
            return today <= nowLocal ? today : today.AddDays(-1);

        var back = ((int)nowLocal.DayOfWeek - (int)s.Day + 7) % 7;
        var candidate = today.AddDays(-back);
        return candidate <= nowLocal ? candidate : candidate.AddDays(-7);
    }

    public static bool IsDue(Schedule s, DateTime? lastRunLocal, DateTime nowLocal, double idleMinutes, bool onBattery)
    {
        var due = LastDueAt(s, nowLocal);
        if (due == null) return false;
        if (lastRunLocal != null && lastRunLocal >= due) return false;
        if (s.SkipOnBattery && onBattery) return false;
        return idleMinutes >= s.RequireIdleMinutes;
    }
}

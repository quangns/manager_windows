namespace ManagerWindows.Core.Monitoring;

public enum LagResource { Cpu, Disk, Memory }

/// <summary>Một nguyên nhân có thể gây lag: tài nguyên đang quá tải và các app dùng nhiều nhất.</summary>
public sealed record LagCause(LagResource Resource, double Percent, IReadOnlyList<AppGroup> TopApps, string? Detail = null);

public sealed class LagThresholds
{
    public double CpuPercent { get; set; } = 85;
    /// <summary>% thời gian ổ đĩa bận (Active time trong Task Manager).</summary>
    public double DiskBusyPercent { get; set; } = 80;
    public double RamUsedPercent { get; set; } = 90;
    public int TopApps { get; set; } = 3;
}

/// <summary>Đoán vì sao máy lag từ số liệu tức thời: tài nguyên nào quá tải, app nào dùng nó nhiều nhất.</summary>
public static class LagDiagnosis
{
    /// <param name="diskDetail">Tên ổ đang bận nhất (vd. "C:"), để hiện cho người dùng.</param>
    public static List<LagCause> Analyze(double cpuPercent, double diskBusyPercent, double ramUsedPercent,
        IReadOnlyList<AppGroup> groups, LagThresholds? t = null, string? diskDetail = null)
    {
        t ??= new LagThresholds();
        var causes = new List<LagCause>();
        if (diskBusyPercent >= t.DiskBusyPercent)
            causes.Add(new LagCause(LagResource.Disk, diskBusyPercent,
                Top(groups.Where(g => g.IoBytesPerSec >= 1024), g => g.IoBytesPerSec, t.TopApps), diskDetail));
        if (cpuPercent >= t.CpuPercent)
            causes.Add(new LagCause(LagResource.Cpu, cpuPercent,
                Top(groups.Where(g => g.CpuPercent >= 0.5), g => g.CpuPercent, t.TopApps)));
        if (ramUsedPercent >= t.RamUsedPercent)
            causes.Add(new LagCause(LagResource.Memory, ramUsedPercent,
                Top(groups, g => g.WorkingSetBytes, t.TopApps)));
        // Tài nguyên quá tải nặng nhất lên đầu.
        return causes.OrderByDescending(c => c.Percent).ToList();
    }

    private static List<AppGroup> Top(IEnumerable<AppGroup> groups, Func<AppGroup, double> key, int n) =>
        groups.OrderByDescending(key).Take(n).ToList();
}

namespace ManagerWindows.Core.Excel;

public sealed class ExcelOptions
{
    public bool Enabled { get; set; } = true;
    public int HeavyFileMb { get; set; } = 30;
    /// <summary>Excel dùng RAM quá ngưỡng này cũng tính là nặng (kể cả khi không biết file nào đang mở).</summary>
    public int HeavyWorkingSetMb { get; set; } = 1500;
    public int HeavyCpuPercent { get; set; } = 50;
}

public static class ExcelHeavyDetector
{
    /// <param name="openFileBytes">Kích thước file Excel đang mở (null nếu không xác định).</param>
    /// <param name="sustainedCpu">CPU trung bình của Excel trong khoảng theo dõi gần nhất.</param>
    public static bool IsHeavy(ExcelOptions opt, long? openFileBytes, long workingSetBytes, double sustainedCpu) =>
        opt.Enabled && (
            openFileBytes >= (long)opt.HeavyFileMb << 20 ||
            workingSetBytes >= (long)opt.HeavyWorkingSetMb << 20 ||
            sustainedCpu >= opt.HeavyCpuPercent && workingSetBytes >= 500L << 20);
}

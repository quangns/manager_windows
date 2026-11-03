namespace ManagerWindows.Core.Usage;

public sealed record UsageSession(string Exe, DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>
/// Gom các lần lấy mẫu "cửa sổ đang dùng" (mỗi vài giây) thành phiên sử dụng liên tục.
/// Khi người dùng rảnh (không chạm phím/chuột quá ngưỡng) thì đóng phiên, không cộng giờ.
/// </summary>
public sealed class UsageAggregator(TimeSpan sampleInterval)
{
    private string? _exe;
    private DateTime _start;
    private DateTime _last;
    private readonly List<UsageSession> _closed = new();

    /// <param name="exe">Đường dẫn exe của cửa sổ đang dùng, null nếu không xác định.</param>
    /// <param name="idle">Người dùng đang rảnh.</param>
    public void Sample(DateTime utc, string? exe, bool idle)
    {
        if (idle || exe == null)
        {
            Close();
            return;
        }

        // Mất mẫu quá lâu (máy ngủ, app bị treo) → coi như phiên cũ đã kết thúc ở mẫu cuối.
        var gap = utc - _last;
        if (_exe != null && (!string.Equals(_exe, exe, StringComparison.OrdinalIgnoreCase) || gap > sampleInterval * 3))
            Close();

        if (_exe == null)
        {
            _exe = exe;
            _start = utc;
        }
        _last = utc;
    }

    /// <summary>Đóng phiên hiện tại (vd. khi thoát app).</summary>
    public void Close()
    {
        if (_exe != null)
        {
            // Mẫu cuối đại diện cho một khoảng sampleInterval.
            _closed.Add(new UsageSession(_exe, _start, _last + sampleInterval));
            _exe = null;
        }
    }

    /// <summary>Lấy ra các phiên đã đóng để ghi xuống DB.</summary>
    public List<UsageSession> Drain()
    {
        var list = _closed.ToList();
        _closed.Clear();
        return list;
    }
}

public sealed record AppUsageSummary(string Exe, double ActiveSeconds, int Launches, int Sessions)
{
    public string Name => ExeName.Of(Exe);
}

public static class ExeName
{
    /// <summary>Tên process từ đường dẫn exe Windows (C:\x\EXCEL.EXE → EXCEL), chạy đúng cả khi test trên Linux.</summary>
    public static string Of(string exePath)
    {
        var i = exePath.LastIndexOfAny(['\\', '/']);
        var name = i >= 0 ? exePath[(i + 1)..] : exePath;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

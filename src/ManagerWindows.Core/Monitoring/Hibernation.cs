using ManagerWindows.Core.Priority;

namespace ManagerWindows.Core.Monitoring;

public sealed class HibernationOptions
{
    /// <summary>Mặc định tắt: tạm dừng app là can thiệp sâu, người dùng tự bật.</summary>
    public bool Enabled { get; set; }
    /// <summary>App không được dùng (không ở cửa sổ trước) quá ngần này phút thì cho ngủ đông.</summary>
    public int IdleMinutes { get; set; } = 15;
    /// <summary>Chỉ cho ngủ app mà mọi cửa sổ đang thu nhỏ (an toàn hơn: không đóng băng cửa sổ đang nhìn thấy).</summary>
    public bool OnlyMinimized { get; set; }
    /// <summary>App dùng ít RAM hơn ngần này thì không đáng cho ngủ.</summary>
    public int MinRamMb { get; set; } = 50;
    /// <summary>App còn đang làm việc (CPU tổng các tiến trình ≥ ngần này %) thì không cho ngủ.</summary>
    public double MaxCpuPercent { get; set; } = 2;
    /// <summary>App còn đang đọc ghi (≥ ngần này KB/s, vd. đang tải file) thì không cho ngủ.</summary>
    public int MaxIoKbPerSec { get; set; } = 200;

    /// <summary>
    /// Không bao giờ cho ngủ: app nhắn tin/gọi (sẽ không nhận tin), nhạc/video, điều khiển từ xa, VPN, tải file, ghi hình.
    /// </summary>
    public List<string> Excluded { get; set; } =
    [
        "Zalo", "ZaloCall", "Telegram", "Teams", "ms-teams", "Skype", "Slack", "Discord", "Viber", "WeChat", "Weixin",
        "WhatsApp", "Messenger", "OUTLOOK", "thunderbird", "Spotify", "vlc", "wmplayer", "PotPlayerMini64", "Music.UI",
        "mstsc", "AnyDesk", "TeamViewer", "UltraViewer_Desktop", "openvpn-gui", "IDMan", "obs64", "ShareX",
    ];
}

/// <summary>Một app (gộp theo tên tiến trình) tại thời điểm xét ngủ đông.</summary>
public sealed record HibernationCandidate(
    string Name,
    IReadOnlyList<int> Pids,
    bool HasWindow,
    bool AllWindowsMinimized,
    double CpuPercent,
    double IoBytesPerSec,
    long WorkingSetBytes,
    bool PlayingAudio,
    bool OwnsClipboard,
    bool IsForeground);

public enum HibernationVerdict
{
    Hibernate,
    InUse,           // đang ở cửa sổ trước hoặc mới dùng
    Excluded,        // trong danh sách không cho ngủ
    Busy,            // đang dùng CPU/ổ đĩa
    PlayingAudio,
    OwnsClipboard,
    NotMinimized,
    TooSmall,
    NoWindow,        // tiến trình chạy ngầm không có cửa sổ: không xét (dùng "Tiến trình sót")
    Protected,
    AlreadyAsleep,
}

public sealed record HibernationDecision(string Name, HibernationVerdict Verdict, TimeSpan Idle);

/// <summary>Quyết định app nào được cho ngủ đông. Thuần logic để kiểm thử.</summary>
public static class HibernationPlanner
{
    public static List<HibernationDecision> Decide(IEnumerable<HibernationCandidate> apps, Func<string, DateTime> lastActiveUtc,
        DateTime nowUtc, HibernationOptions opt, IReadOnlySet<string> asleep)
    {
        var excluded = new HashSet<string>(opt.Excluded, StringComparer.OrdinalIgnoreCase);
        var result = new List<HibernationDecision>();
        foreach (var a in apps)
        {
            var idle = nowUtc - lastActiveUtc(a.Name);
            var verdict =
                asleep.Contains(a.Name) ? HibernationVerdict.AlreadyAsleep
                : ProtectedProcesses.IsProtected(a.Name) || a.Name.Equals("ManagerWindows.App", StringComparison.OrdinalIgnoreCase)
                    ? HibernationVerdict.Protected
                : !a.HasWindow ? HibernationVerdict.NoWindow
                : excluded.Contains(a.Name) ? HibernationVerdict.Excluded
                : a.IsForeground || idle < TimeSpan.FromMinutes(opt.IdleMinutes) ? HibernationVerdict.InUse
                : a.PlayingAudio ? HibernationVerdict.PlayingAudio
                : a.OwnsClipboard ? HibernationVerdict.OwnsClipboard
                : a.CpuPercent >= opt.MaxCpuPercent || a.IoBytesPerSec >= opt.MaxIoKbPerSec * 1024.0 ? HibernationVerdict.Busy
                : opt.OnlyMinimized && !a.AllWindowsMinimized ? HibernationVerdict.NotMinimized
                : a.WorkingSetBytes < (long)opt.MinRamMb << 20 ? HibernationVerdict.TooSmall
                : HibernationVerdict.Hibernate;
            result.Add(new HibernationDecision(a.Name, verdict, idle));
        }
        return result;
    }

    /// <summary>Các PID gốc cùng mọi tiến trình con cháu (vd. renderer của Chrome, tiến trình nền app tự mở).</summary>
    public static HashSet<int> WithDescendants(IEnumerable<int> roots, IReadOnlyDictionary<int, int> parentOf)
    {
        var children = parentOf.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var all = new HashSet<int>();
        var stack = new Stack<int>(roots);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (pid is 0 or 4 || !all.Add(pid)) continue;
            if (children.TryGetValue(pid, out var kids))
                foreach (var k in kids) stack.Push(k);
        }
        return all;
    }
}

/// <summary>Nhớ lần cuối mỗi app ở cửa sổ trước. App chưa từng thấy thì tính từ lúc bắt đầu theo dõi.</summary>
public sealed class AppActivityTracker(DateTime startedUtc)
{
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void MarkActive(string name, DateTime utc)
    {
        lock (_gate) _last[name] = utc;
    }

    public DateTime LastActive(string name)
    {
        lock (_gate) return _last.TryGetValue(name, out var t) ? t : startedUtc;
    }
}

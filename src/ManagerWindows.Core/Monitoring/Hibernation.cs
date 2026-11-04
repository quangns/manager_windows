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
    /// Không bao giờ cho ngủ: app nhắn tin/gọi (sẽ không nhận tin), mail, điều khiển từ xa (máy khác sẽ mất kết nối),
    /// VPN, bộ gõ (ngủ là không gõ được tiếng Việt), nhạc/video, tải file, ghi hình.
    /// </summary>
    public List<string> Excluded { get; set; } = [.. DefaultExcluded];

    /// <summary>Phiên bản bộ mặc định đã gộp vào <see cref="Excluded"/> (cấu hình cũ không có trường này = 0).</summary>
    public int DefaultsVersion { get; set; }

    public const int CurrentDefaultsVersion = 2;

    // Chat, gọi, họp
    private static readonly string[] Chat =
    [
        "Zalo", "ZaloCall", "Telegram", "Teams", "ms-teams", "Skype", "lync", "Slack", "Discord", "Viber", "WeChat", "Weixin",
        "WhatsApp", "WhatsApp.Root", "Messenger", "Signal", "LINE", "KakaoTalk", "Lark", "Feishu", "Mattermost", "Element",
        "Rocket.Chat", "Ferdium", "Rambox", "Zoom", "CiscoCollabHost", "atmgr",
    ];
    // Mail
    private static readonly string[] Mail =
        ["OUTLOOK", "olk", "HxOutlook", "HxTsr", "thunderbird", "MailClient", "Mailbird", "Spark", "BlueMail", "Postbox"];
    // Điều khiển từ xa
    private static readonly string[] Remote =
    [
        "mstsc", "msrdc", "AnyDesk", "TeamViewer", "TeamViewer_Service", "UltraViewer_Desktop", "UltraViewer_Service",
        "DeskIn", "DeskInService", "ToDesk", "ToDesk_Service", "rustdesk", "parsecd", "remoting_host", "SRManager", "SRStreamer",
        "strwinclt", "vncviewer", "vncserver", "winvnc", "tvnserver", "Radmin", "rserver3", "mRemoteNG", "RDCMan",
    ];
    // VPN
    private static readonly string[] Vpn =
    [
        "tailscale-ipn", "tailscaled", "openvpn-gui", "openvpn", "OpenVPNConnect", "wireguard", "zerotier_desktop_ui",
        "zerotier-one_x64", "FortiClient", "FortiTray", "vpnui", "csc_ui", "Cloudflare WARP", "warp-svc", "ProtonVPN",
        "NordVPN", "expressvpn", "Pulse", "PanGPA", "SoftEther VPN Client Manager", "Hamachi-2-ui",
    ];
    // Bộ gõ tiếng Việt và IME của Windows
    private static readonly string[] Typing =
    [
        "UniKeyNT", "UniKey", "EVKey", "EVKey64", "EVKey32", "OpenKey", "OpenKey64", "OpenKey32", "GoTiengViet", "VietKey",
        "LabanKey", "ctfmon", "TextInputHost",
    ];
    // Nhạc/video, tải file, ghi hình
    private static readonly string[] Media =
        ["Spotify", "vlc", "wmplayer", "PotPlayerMini64", "Music.UI", "IDMan", "obs64", "ShareX"];

    public static IReadOnlyList<string> DefaultExcluded { get; } =
        [.. Chat, .. Mail, .. Remote, .. Vpn, .. Typing, .. Media];

    /// <summary>Bộ mặc định đến v0.5.1 (DefaultsVersion 0/1).</summary>
    private static readonly string[] DefaultsV1 =
    [
        "Zalo", "ZaloCall", "Telegram", "Teams", "ms-teams", "Skype", "Slack", "Discord", "Viber", "WeChat", "Weixin",
        "WhatsApp", "Messenger", "OUTLOOK", "thunderbird", "Spotify", "vlc", "wmplayer", "PotPlayerMini64", "Music.UI",
        "mstsc", "AnyDesk", "TeamViewer", "UltraViewer_Desktop", "openvpn-gui", "IDMan", "obs64", "ShareX",
    ];

    /// <summary>App mới thêm vào bộ mặc định ở từng phiên bản (để cập nhật cấu hình cũ).</summary>
    private static readonly Dictionary<int, string[]> AddedIn = new()
    {
        [2] = DefaultExcluded.Except(DefaultsV1, StringComparer.OrdinalIgnoreCase).ToArray(),
    };

    /// <summary>
    /// Gộp các app mới của bộ mặc định vào cấu hình cũ, đúng một lần cho mỗi phiên bản,
    /// để không thêm lại app người dùng đã tự bỏ. Trả về true nếu có thay đổi cần lưu.
    /// </summary>
    public bool UpgradeDefaults()
    {
        if (DefaultsVersion >= CurrentDefaultsVersion) return false;
        var set = new HashSet<string>(Excluded, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(Excluded);
        foreach (var (v, names) in AddedIn)
            if (v > DefaultsVersion)
                foreach (var n in names)
                    if (set.Add(n)) list.Add(n);
        Excluded = list;
        DefaultsVersion = CurrentDefaultsVersion;
        return true;
    }
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

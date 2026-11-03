namespace ManagerWindows.Core.Priority;

public enum PriorityLevel { Idle = 0, BelowNormal = 1, Normal = 2, AboveNormal = 3, High = 4 }

/// <summary>Lựa chọn của người dùng cho một app trong danh sách chọn.</summary>
public enum AppChoice { Normal, Priority, Background }

public sealed class PriorityOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Tự ưu tiên N app dùng nhiều nhất trong 7 ngày (theo thống kê sử dụng).</summary>
    public int TopUsedCount { get; set; } = 3;
    /// <summary>App người dùng chỉ định luôn được ưu tiên (tên process, không có .exe).</summary>
    public List<string> Pinned { get; set; } = new();
    /// <summary>App nền ngốn tài nguyên sẽ bị hạ ưu tiên.</summary>
    public List<string> Background { get; set; } =
        ["OneDrive", "Dropbox", "GoogleDriveFS", "MicrosoftEdgeUpdate", "GoogleUpdate", "msedgewebview2",
         "SearchIndexer", "TiWorker", "CompatTelRunner", "Teams", "ms-teams"];
    /// <summary>Cho phép mức High cho app ghim. Mặc định chỉ dùng AboveNormal.</summary>
    public bool AllowHigh { get; set; }
    /// <summary>Hạ ưu tiên các app nền khi đang mở file Excel nặng.</summary>
    public AppChoice GetChoice(string processName)
    {
        if (Pinned.Contains(processName, StringComparer.OrdinalIgnoreCase)) return AppChoice.Priority;
        if (Background.Contains(processName, StringComparer.OrdinalIgnoreCase)) return AppChoice.Background;
        return AppChoice.Normal;
    }

    /// <summary>
    /// Đặt lựa chọn cho một app. Gán danh sách MỚI thay vì sửa tại chỗ vì vòng điều phối chạy ở luồng nền
    /// đang đọc danh sách cũ.
    /// </summary>
    public void SetChoice(string processName, AppChoice choice)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        var pinned = Pinned.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var background = Background.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (choice == AppChoice.Priority) pinned.Add(name);
        if (choice == AppChoice.Background) background.Add(name);
        Pinned = pinned;
        Background = background;
    }

    public List<string> ThrottleWhenExcelHeavy { get; set; } =
        ["chrome", "msedge", "firefox", "browser", "Zalo", "Telegram", "OneDrive", "Teams", "ms-teams"];
}

public sealed record ProcessSnapshot(
    int Pid,
    string Name,
    PriorityLevel? Current,
    bool IsForeground,
    double CpuPercent,
    long WorkingSetBytes);

public sealed record PriorityDecision(int Pid, string Name, PriorityLevel Target, bool EcoQoS, string Reason);

public static class ProtectedProcesses
{
    /// <summary>Không bao giờ đổi ưu tiên: tiến trình hệ thống và phần mềm bảo mật.</summary>
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "Memory Compression", "MsMpEng", "NisSrv", "SecurityHealthService",
        "audiodg", "conhost", "sihost", "ctfmon", "explorer", "ManagerWindows.App", "ManagerWindows.Service",
    };

    public static bool IsProtected(string name) => Names.Contains(name);
}

/// <summary>
/// Quyết định ưu tiên cho từng process. Chỉ trả về những process cần đổi so với hiện tại.
/// Nguyên tắc: không bao giờ Realtime, High phải bật riêng, không đụng process được bảo vệ,
/// app nền đang được dùng (cửa sổ trước) thì không hạ.
/// </summary>
public static class PriorityPolicy
{
    public static List<PriorityDecision> Decide(
        IEnumerable<ProcessSnapshot> processes,
        IReadOnlyCollection<string> topUsed,
        PriorityOptions opt,
        bool excelHeavy)
    {
        var result = new List<PriorityDecision>();
        if (!opt.Enabled) return result;

        var cmp = StringComparer.OrdinalIgnoreCase;
        var pinned = new HashSet<string>(opt.Pinned, cmp);
        var top = new HashSet<string>(topUsed.Take(Math.Max(0, opt.TopUsedCount)), cmp);
        var background = new HashSet<string>(opt.Background, cmp);
        var excelThrottle = new HashSet<string>(opt.ThrottleWhenExcelHeavy, cmp);

        foreach (var p in processes)
        {
            if (ProtectedProcesses.IsProtected(p.Name) || p.Current == null) continue;

            PriorityLevel target;
            bool eco;
            string reason;
            if (excelHeavy && cmp.Equals(p.Name, "EXCEL"))
            {
                (target, eco, reason) = (opt.AllowHigh ? PriorityLevel.High : PriorityLevel.AboveNormal, false, "excel-heavy");
            }
            else if (pinned.Contains(p.Name))
            {
                (target, eco, reason) = (opt.AllowHigh ? PriorityLevel.High : PriorityLevel.AboveNormal, false, "pinned");
            }
            else if (excelHeavy && !p.IsForeground && excelThrottle.Contains(p.Name))
            {
                // Đang mở file Excel nặng: nhường tài nguyên cho Excel, kể cả app thuộc nhóm dùng nhiều.
                (target, eco, reason) = (PriorityLevel.BelowNormal, true, "excel-heavy-throttle");
            }
            else if (top.Contains(p.Name))
            {
                (target, eco, reason) = (PriorityLevel.AboveNormal, false, "top-used");
            }
            else if (!p.IsForeground && background.Contains(p.Name))
            {
                (target, eco, reason) = (PriorityLevel.BelowNormal, true, "background");
            }
            else continue;

            if (target > PriorityLevel.High) target = PriorityLevel.High;
            if (!opt.AllowHigh && target > PriorityLevel.AboveNormal) target = PriorityLevel.AboveNormal;
            result.Add(new PriorityDecision(p.Pid, p.Name, target, eco, reason));
        }
        return result;
    }
}

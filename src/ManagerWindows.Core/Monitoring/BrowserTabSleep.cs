using System.Text.RegularExpressions;

namespace ManagerWindows.Core.Monitoring;

/// <summary>Trình duyệt hỗ trợ ngủ đông tab (đều dựa trên Chromium, có sẵn Memory Saver).</summary>
public enum TabSleepBrowser { Chrome, CocCoc, Brave }

/// <summary>Mức tiết kiệm của Memory Saver (giá trị chính sách MemorySaverModeSavings): càng cao, tab càng sớm được cho ngủ.</summary>
public enum TabSleepSavings { Moderate = 0, Balanced = 1, Maximum = 2 }

/// <summary>
/// Ngủ đông tab trình duyệt: bật Memory Saver có sẵn của Chrome / Cốc Cốc / Brave bằng chính sách Windows.
/// Tab nền lâu không dùng bị trình duyệt "discard" để trả RAM; bấm vào tab thì trình duyệt tự tải lại.
/// </summary>
public sealed class BrowserTabSleepOptions
{
    /// <summary>Tắt sẵn: bật thì trình duyệt sẽ hiện dòng "do tổ chức của bạn quản lý".</summary>
    public bool Enabled { get; set; }
    public TabSleepSavings Savings { get; set; } = TabSleepSavings.Balanced;
    public List<TabSleepBrowser> Browsers { get; set; } = [TabSleepBrowser.Chrome, TabSleepBrowser.CocCoc, TabSleepBrowser.Brave];
    /// <summary>Mẫu URL không bao giờ cho ngủ (cú pháp URL blocklist của Chromium).</summary>
    public List<string> Exceptions { get; set; } = [.. DefaultExceptions];

    /// <summary>Trang chat / họp trực tuyến: ngủ là lỡ tin nhắn, rớt cuộc gọi.</summary>
    public static readonly string[] DefaultExceptions = ["chat.zalo.me", "meet.google.com", "teams.microsoft.com", "web.telegram.org"];
}

/// <summary>Khoá chính sách và nơi cài thường gặp của một trình duyệt.</summary>
public sealed record BrowserInfo(TabSleepBrowser Browser, string DisplayName, string PolicyKey, string ProcessName, string[] InstallPaths);

public static class BrowserCatalog
{
    /// <summary>
    /// Khoá chính sách dưới HKLM. Chrome và Brave theo tài liệu của hãng. Cốc Cốc chưa xác minh trên máy thật
    /// (kiểm tra ở coccoc://policy); nếu sai chỉ cần sửa ở đây.
    /// </summary>
    public static readonly BrowserInfo[] All =
    [
        new(TabSleepBrowser.Chrome, "Google Chrome", @"SOFTWARE\Policies\Google\Chrome", "chrome",
            [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"]),
        new(TabSleepBrowser.CocCoc, "Cốc Cốc", @"SOFTWARE\Policies\CocCoc\Browser", "browser",
            [@"%ProgramFiles%\CocCoc\Browser\Application\browser.exe", @"%ProgramFiles(x86)%\CocCoc\Browser\Application\browser.exe", @"%LOCALAPPDATA%\CocCoc\Browser\Application\browser.exe"]),
        new(TabSleepBrowser.Brave, "Brave", @"SOFTWARE\Policies\BraveSoftware\Brave", "brave",
            [@"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe"]),
    ];

    public static BrowserInfo Get(TabSleepBrowser b) => All.First(x => x.Browser == b);
}

/// <summary>Một thao tác registry dưới khoá chính sách của trình duyệt (HKLM).</summary>
public abstract record PolicyOp(string Key);
public sealed record SetDwordOp(string Key, string Name, int Value) : PolicyOp(Key);
public sealed record SetStringOp(string Key, string Name, string Value) : PolicyOp(Key);
public sealed record DeleteValueOp(string Key, string Name) : PolicyOp(Key);
public sealed record DeleteKeyOp(string Key) : PolicyOp(Key);

/// <summary>Giá trị chính sách đang có trên máy (null = chưa đặt).</summary>
public sealed record PolicySnapshot(int? Enabled, int? Savings, bool HasExceptions)
{
    public static readonly PolicySnapshot Empty = new(null, null, false);
}

/// <summary>Yêu cầu bật/tắt (từ App gửi cho dịch vụ nền hoặc cho chính app chạy quyền Admin ở bản portable).</summary>
public sealed record TabSleepRequest(bool Enabled, int Savings, List<string> Browsers, List<string> Exceptions);

/// <summary>Kết quả từng trình duyệt: Applied = đã bật; Conflicts = mục quản trị viên đã đặt nên bỏ qua.</summary>
public sealed record TabSleepBrowserResult(string Browser, bool Applied, List<string> Conflicts);

/// <summary>Kết quả lập kế hoạch: thao tác cần làm, các mục app sở hữu sau khi làm, các mục bỏ qua vì quản trị viên đã đặt.</summary>
public sealed record PolicyPlan(List<PolicyOp> Ops, HashSet<string> Owned, List<string> Conflicts);

/// <summary>
/// Lập danh sách thao tác registry để bật/tắt ngủ đông tab. Không chạm registry nên kiểm thử được.
/// Nguyên tắc: chỉ ghi/xoá những mục do Manager Windows đặt (<paramref name="owned"/>); mục quản trị viên đã đặt thì để nguyên.
/// </summary>
public static partial class BrowserPolicyPlanner
{
    public const string EnabledValue = "HighEfficiencyModeEnabled";
    public const string SavingsValue = "MemorySaverModeSavings";
    public const string ExceptionsKey = "TabDiscardingExceptions";
    public const int MaxExceptions = 100;
    public const int MaxExceptionLength = 200;

    public static PolicyPlan Build(TabSleepBrowser browser, bool enable, TabSleepSavings savings, IReadOnlyList<string> exceptions,
        PolicySnapshot current, IReadOnlySet<string> owned)
    {
        var key = BrowserCatalog.Get(browser).PolicyKey;
        var exKey = key + "\\" + ExceptionsKey;
        var ops = new List<PolicyOp>();
        var nowOwned = new HashSet<string>(owned, StringComparer.Ordinal);
        var conflicts = new List<string>();
        bool Mine(string item, bool present) => !present || owned.Contains(item);

        if (!enable)
        {
            if (owned.Contains(EnabledValue)) ops.Add(new DeleteValueOp(key, EnabledValue));
            if (owned.Contains(SavingsValue)) ops.Add(new DeleteValueOp(key, SavingsValue));
            if (owned.Contains(ExceptionsKey)) ops.Add(new DeleteKeyOp(exKey));
            return new PolicyPlan(ops, [], conflicts);
        }

        if (Mine(EnabledValue, current.Enabled != null))
        {
            ops.Add(new SetDwordOp(key, EnabledValue, 1));
            nowOwned.Add(EnabledValue);
        }
        else conflicts.Add(EnabledValue);

        if (Mine(SavingsValue, current.Savings != null))
        {
            ops.Add(new SetDwordOp(key, SavingsValue, (int)savings));
            nowOwned.Add(SavingsValue);
        }
        else conflicts.Add(SavingsValue);

        if (Mine(ExceptionsKey, current.HasExceptions))
        {
            // Ghi lại cả danh sách: xoá khoá cũ rồi tạo các giá trị 1..n (cách Chromium đọc chính sách dạng danh sách).
            if (owned.Contains(ExceptionsKey)) ops.Add(new DeleteKeyOp(exKey));
            var list = exceptions.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < list.Count; i++) ops.Add(new SetStringOp(exKey, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), list[i]));
            if (list.Count > 0) nowOwned.Add(ExceptionsKey);
            else nowOwned.Remove(ExceptionsKey);
        }
        else conflicts.Add(ExceptionsKey);

        return new PolicyPlan(ops, nowOwned, conflicts);
    }

    /// <summary>
    /// Kiểm tra yêu cầu trước khi ghi registry quyền cao (người gửi có thể là bất kỳ người dùng nào):
    /// chỉ 3 trình duyệt đã biết, mức 0–2, tối đa 100 mẫu URL hợp lệ. Trả lỗi hoặc null nếu hợp lệ.
    /// </summary>
    public static string? Validate(TabSleepRequest r, out HashSet<TabSleepBrowser> browsers)
    {
        browsers = [];
        if (r.Savings is < 0 or > 2) return "Mức tiết kiệm không hợp lệ";
        foreach (var b in r.Browsers ?? [])
        {
            if (!Enum.TryParse<TabSleepBrowser>(b, ignoreCase: false, out var v) || !Enum.IsDefined(v) || int.TryParse(b, out _))
                return "Trình duyệt không hỗ trợ";
            browsers.Add(v);
        }
        var ex = r.Exceptions ?? [];
        if (ex.Count > MaxExceptions || !ex.All(IsValidException)) return "Danh sách trang không hợp lệ";
        return null;
    }

    /// <summary>Mẫu URL hợp lệ: không rỗng, ≤ 200 ký tự, chỉ gồm ký tự được phép trong URL (không khoảng trắng, không ký tự điều khiển).</summary>
    public static bool IsValidException(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && pattern.Trim().Length <= MaxExceptionLength && UrlPattern().IsMatch(pattern.Trim());

    /// <summary>Tách ô nhập (mỗi dòng hoặc dấu phẩy một mẫu) → danh sách hợp lệ + các dòng bị bỏ.</summary>
    public static (List<string> Valid, List<string> Invalid) ParseExceptions(string text)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValidException(raw)) invalid.Add(raw);
            else if (!valid.Contains(raw, StringComparer.OrdinalIgnoreCase)) valid.Add(raw);
        }
        if (valid.Count > MaxExceptions)
        {
            invalid.AddRange(valid.Skip(MaxExceptions));
            valid = valid.Take(MaxExceptions).ToList();
        }
        return (valid, invalid);
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+$")]
    private static partial Regex UrlPattern();
}

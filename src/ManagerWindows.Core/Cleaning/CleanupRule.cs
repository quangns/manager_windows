using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManagerWindows.Core.Cleaning;

public enum RuleScope { User, System }

public enum CleanupAction { Delete, Recycle }

public enum RuleRisk { Safe, Review }

/// <summary>Kiểu luật đặc biệt không xử lý bằng quét file.</summary>
public enum RuleKind { Files, RecycleBin }

public sealed class CleanupRule
{
    public string Id { get; set; } = "";
    /// <summary>Tên hiển thị theo ngôn ngữ: { "vi": "...", "en": "..." }.</summary>
    public Dictionary<string, string> Name { get; set; } = new();
    public RuleKind Kind { get; set; } = RuleKind.Files;
    /// <summary>Thư mục gốc; hỗ trợ biến môi trường và ký tự đại diện trong từng đoạn (vd. User Data\*\Cache).</summary>
    public List<string> Paths { get; set; } = new();
    public List<string> Patterns { get; set; } = new() { "*" };
    public List<string> ExcludePatterns { get; set; } = new();
    public int MinAgeDays { get; set; } = 1;
    public bool Recursive { get; set; } = true;
    public bool DeleteEmptyDirectories { get; set; } = true;
    public RuleScope Scope { get; set; } = RuleScope.User;
    public CleanupAction Action { get; set; } = CleanupAction.Delete;
    public RuleRisk Risk { get; set; } = RuleRisk.Safe;
    public bool EnabledByDefault { get; set; } = true;

    public string DisplayName(string lang) =>
        Name.TryGetValue(lang, out var n) ? n : Name.Values.FirstOrDefault() ?? Id;
}

public static class RuleLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // NaN/Infinity trong một số thực bất kỳ không được làm hỏng việc lưu cả file cấu hình.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<CleanupRule> Load(string jsonPath)
    {
        using var s = File.OpenRead(jsonPath);
        return Parse(s);
    }

    public static List<CleanupRule> Parse(Stream json)
    {
        var rules = JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions) ?? new();
        var dup = rules.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"Trùng id luật dọn: {dup.Key}");
        if (rules.Any(r => string.IsNullOrWhiteSpace(r.Id)))
            throw new InvalidDataException("Luật dọn thiếu id");
        return rules;
    }
}

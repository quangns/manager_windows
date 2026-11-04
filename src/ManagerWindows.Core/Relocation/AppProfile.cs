using System.Text.Json;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;

namespace ManagerWindows.Core.Relocation;

public enum RelocationMethod
{
    /// <summary>Chuyển data rồi để junction ở chỗ cũ.</summary>
    Junction,
    /// <summary>Không nên dùng junction (Outlook, WSL, OneDrive…): chỉ hiển thị hướng dẫn.</summary>
    Manual,
}

public sealed class AppProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Tên process (không có .exe) cần đóng trước khi chuyển.</summary>
    public List<string> ProcessNames { get; set; } = new();
    /// <summary>Thư mục data ứng viên, có biến môi trường; chỉ những thư mục tồn tại mới được hiển thị.</summary>
    public List<string> Folders { get; set; } = new();
    public RelocationMethod Method { get; set; } = RelocationMethod.Junction;
    public Dictionary<string, string> Notes { get; set; } = new();

    public string Note(string lang) => Notes.TryGetValue(lang, out var n) ? n : Notes.Values.FirstOrDefault() ?? "";

    public static List<AppProfile> Load(string jsonPath)
    {
        using var s = File.OpenRead(jsonPath);
        return Parse(s);
    }

    public static List<AppProfile> Parse(Stream json) =>
        JsonSerializer.Deserialize<List<AppProfile>>(json, RuleLoader.JsonOptions) ?? new();
}

public sealed record DetectedFolder(AppProfile? Profile, string Path, long Bytes, bool IsLinked, string? LinkTarget);

/// <summary>Dò thư mục data của app trên máy: theo hồ sơ có sẵn và các thư mục lớn bất thường trong AppData.</summary>
public sealed class AppDataDetector(ILinkApi links)
{
    public List<DetectedFolder> Detect(IEnumerable<AppProfile> profiles, long unknownMinBytes, CancellationToken ct = default)
    {
        var result = new List<DetectedFolder>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in profiles)
            foreach (var folder in p.Folders.SelectMany(PathExpander.Expand))
            {
                ct.ThrowIfCancellationRequested();
                known.Add(folder);
                result.Add(Describe(p, folder, ct));
            }

        // Thư mục lớn trong AppData chưa có hồ sơ: vẫn gợi ý để người dùng tự quyết.
        string[] appDataRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (var root in appDataRoots.Where(r => !string.IsNullOrEmpty(r)))
            foreach (var dir in SafeFileWalker.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                if (known.Any(k => SafeFileWalker.IsUnder(k, dir) || SafeFileWalker.IsUnder(dir, k))) continue;
                var (bytes, _) = SafeFileWalker.Measure(dir, ct);
                if (bytes >= unknownMinBytes) result.Add(new DetectedFolder(null, dir, bytes, false, null));
            }

        return result.OrderByDescending(d => d.Bytes).ToList();
    }

    private DetectedFolder Describe(AppProfile p, string folder, CancellationToken ct)
    {
        var target = links.GetLinkTarget(folder);
        var bytes = target != null
            ? (Directory.Exists(target) ? SafeFileWalker.Measure(target, ct).Bytes : 0)
            : SafeFileWalker.Measure(folder, ct).Bytes;
        return new DetectedFolder(p, folder, bytes, target != null, target);
    }
}

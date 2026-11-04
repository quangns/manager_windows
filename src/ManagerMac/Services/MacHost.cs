using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Config;
using ManagerWindows.Storage;

namespace ManagerMac.Services;

/// <summary>Thành phần dùng chung của Manager Mac: cấu hình, CSDL, luật dọn rác.</summary>
public sealed class MacHost
{
    public static MacHost Current { get; private set; } = null!;

    public string DataDir { get; }
    public ConfigStore ConfigStore { get; }
    public AppConfig Config { get; }
    public CleanupRunRepository Runs { get; }
    public List<CleanupRule> Rules { get; }

    public MacHost()
    {
        Current = this;
        DataDir = Path.Combine(MacPlatform.Home, "Library", "Application Support", "ManagerMac");
        Directory.CreateDirectory(DataDir);
        Log.Dir = Path.Combine(MacPlatform.Home, "Library", "Logs", "ManagerMac");
        ConfigStore = new ConfigStore(Path.Combine(DataDir, "config.json"));
        var firstRun = !File.Exists(ConfigStore.Path);
        Config = ConfigStore.Load();
        if (firstRun) ApplyMacDefaults(Config);
        Runs = new CleanupRunRepository(new Database(Path.Combine(DataDir, "data.db")));

        // Luật: ưu tiên file cạnh app (sửa được), không có thì bản nhúng.
        var custom = Path.Combine(DataDir, "cleanup-rules.json");
        using var s = File.Exists(custom) ? File.OpenRead(custom)
            : typeof(MacHost).Assembly.GetManifestResourceStream("rules/cleanup-rules.macos.json")!;
        Rules = RuleLoader.Parse(s);
    }

    /// <summary>Giá trị mặc định của bản Windows không hợp macOS (thư mục, đuôi bộ cài).</summary>
    private static void ApplyMacDefaults(AppConfig c)
    {
        c.StartWithWindows = false;
        c.Cleanup.SuggestionRoots = ["%HOME%/Downloads", "%HOME%/Desktop", "%HOME%/Documents", "%HOME%/Movies"];
        c.Cleanup.Suggestions.InstallerExtensions = [".dmg", ".pkg", ".zip", ".iso", ".xip"];
        c.Cleanup.Suggestions.SkipDirectoryNames = ["Library", "Applications", ".Trash", "node_modules", ".git", "Photos Library.photoslibrary"];
        // macOS mặc định không cập nhật thời gian truy cập file đầy đủ (relatime) → chỉ dựa vào ngày sửa.
        c.Cleanup.Suggestions.LastAccessReliable = false;
    }

    public void SaveConfig() => ConfigStore.Save(Config);

    public bool IsEnabled(CleanupRule r) => Config.Cleanup.EnabledRuleIds?.Contains(r.Id) ?? r.EnabledByDefault;

    public CleanupPlan Plan(IEnumerable<CleanupRule> rules, CancellationToken ct = default) =>
        new CleanupPlanner(SystemClock.Instance).Plan(rules, ct);

    public CleanupResult Execute(CleanupPlan plan, string trigger)
    {
        var started = DateTime.UtcNow;
        var result = new CleanupExecutor(new MacTrashDeleter(), SystemClock.Instance).Execute(plan);
        Runs.Add(started, DateTime.UtcNow, trigger, result.FreedBytes, result.Deleted, result.Failed);
        Log.Info("cleanup", $"{trigger}: freed {result.FreedBytes}, deleted {result.Deleted}, failed {result.Failed}");
        return result;
    }
}

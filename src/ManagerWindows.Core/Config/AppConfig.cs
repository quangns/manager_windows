using System.Text.Json;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Excel;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Core.Priority;
using ManagerWindows.Core.Scheduling;

namespace ManagerWindows.Core.Config;

public sealed class CleanupConfig
{
    public Schedule Schedule { get; set; } = new();
    /// <summary>null = dùng EnabledByDefault của từng luật.</summary>
    public List<string>? EnabledRuleIds { get; set; }
    public DateTime? LastRunLocal { get; set; }
    public List<string> SuggestionRoots { get; set; } =
    [
        "%USERPROFILE%\\Downloads",
        "%USERPROFILE%\\Desktop",
        "%USERPROFILE%\\Documents",
        "%USERPROFILE%\\Videos",
    ];
    public SuggestionOptions Suggestions { get; set; } = new();
}

public sealed class RelocationConfig
{
    public string DefaultTargetRoot { get; set; } = "D:\\AppDataMoved";
    public int BackupKeepDays { get; set; } = 7;
    public int UnknownFolderMinMb { get; set; } = 1024;
}

public sealed class UsageConfig
{
    public bool Enabled { get; set; } = true;
    public int SampleSeconds { get; set; } = 2;
    public int IdleSeconds { get; set; } = 120;
    public int RetentionDays { get; set; } = 90;
}

public sealed class AppConfig
{
    /// <summary>"auto" (theo Windows), "vi" hoặc "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"auto" (theo Windows), "light" hoặc "dark".</summary>
    public string Theme { get; set; } = "auto";
    public bool StartWithWindows { get; set; } = true;
    public CleanupConfig Cleanup { get; set; } = new();
    public RelocationConfig Relocation { get; set; } = new();
    public UsageConfig Usage { get; set; } = new();
    public PriorityOptions Priority { get; set; } = new();
    public ExcelOptions Excel { get; set; } = new();
    public MonitorOptions Monitor { get; set; } = new();
    public LeftoverOptions Leftovers { get; set; } = new();
}

public sealed class ConfigStore(string path)
{
    public string Path { get; } = path;

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig();
        try
        {
            using var s = File.OpenRead(Path);
            return JsonSerializer.Deserialize<AppConfig>(s, RuleLoader.JsonOptions) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // File cấu hình hỏng: giữ lại để xem, dùng mặc định.
            File.Copy(Path, Path + ".broken", overwrite: true);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, RuleLoader.JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }
}

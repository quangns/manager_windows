using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;

namespace ManagerWindows.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    public List<Option<string>> Languages { get; } =
    [
        new("auto", Loc.Instance["Lang_Auto"]),
        new("vi", "Tiếng Việt"),
        new("en", "English"),
    ];

    [ObservableProperty] private string _language;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _usageEnabled;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private int _backupKeepDays;

    public string Version => (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "")
                             + (Host.IsPortable ? " (portable)" : "");
    public string DataDir => Host.DataDir;

    /// <summary>Báo cho App dựng lại cửa sổ theo ngôn ngữ mới.</summary>
    public static event Action? LanguageChanged;

    public SettingsViewModel()
    {
        var c = Host.Config;
        _language = c.Language;
        _startWithWindows = c.StartWithWindows;
        _usageEnabled = c.Usage.Enabled;
        _retentionDays = c.Usage.RetentionDays;
        _backupKeepDays = c.Relocation.BackupKeepDays;
    }

    [RelayCommand]
    private void Save()
    {
        var c = Host.Config;
        var langChanged = c.Language != Language;
        c.Language = Language;
        c.StartWithWindows = StartWithWindows;
        c.Usage.Enabled = UsageEnabled;
        c.Usage.RetentionDays = Math.Clamp(RetentionDays, 7, 3650);
        c.Relocation.BackupKeepDays = Math.Clamp(BackupKeepDays, 1, 365);
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
        if (langChanged)
        {
            Loc.Instance.SetLanguage(Language);
            LanguageChanged?.Invoke();
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = false });
}

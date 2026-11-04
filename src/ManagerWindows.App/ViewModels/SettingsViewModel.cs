using System.Diagnostics;
using System.IO;
using System.Windows;
using ManagerWindows.Core.Config;
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

    public List<Option<string>> Themes { get; } =
    [
        new("auto", Loc.Instance["Theme_Auto"]),
        new("light", Loc.Instance["Theme_Light"]),
        new("dark", Loc.Instance["Theme_Dark"]),
    ];

    [ObservableProperty] private string _language;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _usageEnabled;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private int _backupKeepDays;

    public string Version => (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "")
                             + (Host.IsPortable ? " (portable)" : "");
    public string DataDir => Host.DataDir;
    /// <summary>Đang dùng thư mục do người dùng chọn (không phải mặc định).</summary>
    public bool IsCustomDataDir => !DataLocation.SamePath(Host.DataDir, Host.DefaultDataDir);
    public string DefaultDataDir => Host.DefaultDataDir;

    /// <summary>Báo cho App dựng lại cửa sổ theo ngôn ngữ mới.</summary>
    public static event Action? LanguageChanged;
    /// <summary>Báo cho App khởi động lại (sau khi đổi nơi lưu dữ liệu).</summary>
    public static event Action? RestartRequested;

    public SettingsViewModel()
    {
        var c = Host.Config;
        _language = c.Language;
        _theme = c.Theme;
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
        var themeChanged = c.Theme != Theme;
        c.Theme = Theme;
        c.StartWithWindows = StartWithWindows;
        c.Usage.Enabled = UsageEnabled;
        c.Usage.RetentionDays = Math.Clamp(RetentionDays, 7, 3650);
        c.Relocation.BackupKeepDays = Math.Clamp(BackupKeepDays, 1, 365);
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
        if (themeChanged) ThemeManager.Apply(Theme);
        if (langChanged)
        {
            Loc.Instance.SetLanguage(Language);
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Chọn thư mục mới để lưu cấu hình, thống kê, log; chép dữ liệu sang rồi khởi động lại app.</summary>
    [RelayCommand]
    private Task ChangeDataFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Instance["Data_PickTitle"] };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        // Đặt trong thư mục con "ManagerWindows" để không lẫn với file khác của người dùng.
        var target = string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dlg.FolderName)), "ManagerWindows", StringComparison.OrdinalIgnoreCase)
            ? dlg.FolderName
            : Path.Combine(dlg.FolderName, "ManagerWindows");
        return MoveDataAsync(target);
    }

    /// <summary>Đưa dữ liệu về thư mục mặc định.</summary>
    [RelayCommand]
    private Task ResetDataFolderAsync() => MoveDataAsync(Host.DefaultDataDir);

    private Task MoveDataAsync(string target) => RunBusyAsync(async () =>
    {
        Status = "";
        if (DataLocation.Validate(target, Host.DataDir) is { } err)
        {
            Info(Loc.F(err, ("path", target)));
            return;
        }
        // Thư mục đích đã có dữ liệu của app (vd. từng dùng trước đây): hỏi dùng lại hay ghi đè bằng dữ liệu hiện tại.
        var copy = true;
        if (DataLocation.HasData(target))
        {
            var answer = MessageBox.Show(Loc.F("Data_ExistingAsk", ("path", target)), Loc.Instance["App_Title"],
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            copy = answer == MessageBoxResult.No;
        }
        if (!Confirm(Loc.F(copy ? "Data_ConfirmMove" : "Data_ConfirmUse", ("from", Host.DataDir), ("to", target)))) return;

        var from = Host.DataDir;
        Status = Loc.Instance["Data_Copying"];
        try
        {
            if (copy)
            {
                Host.SaveConfig();
                await Task.Run(() =>
                {
                    Host.UsageTracker.Flush();
                    DataLocation.CopyFiles(from, target);
                    Host.Db.BackupTo(Path.Combine(target, "data.db"));
                });
            }
            // Dữ liệu cũ chỉ xoá ở lần chạy sau (app mới đã dùng thư mục mới, không còn mở file cũ);
            // dùng lại dữ liệu có sẵn thì giữ nguyên dữ liệu cũ để không mất gì.
            DataLocation.Set(Host.DefaultDataDir, target, copy ? from : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("data", ex);
            Info(Loc.F("Data_Failed", ("error", ex.Message)));
            Status = "";
            return;
        }
        Log.Info("data", $"đổi nơi lưu dữ liệu {from} → {target} (chép: {copy})");
        RestartRequested?.Invoke();
    });

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = false });
}

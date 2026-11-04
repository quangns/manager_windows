using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerMac.Services;

namespace ManagerMac.ViewModels;

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
        new("auto", Loc.Instance["Mac_ThemeAuto"]),
        new("light", Loc.Instance["Theme_Light"]),
        new("dark", Loc.Instance["Theme_Dark"]),
    ];

    [ObservableProperty] private Option<string> _language;
    [ObservableProperty] private Option<string> _theme;
    [ObservableProperty] private bool _startAtLogin;

    public string DataDir => Host.DataDir;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Báo cho App dựng lại cửa sổ theo ngôn ngữ mới.</summary>
    public static event Action? LanguageChanged;

    public SettingsViewModel()
    {
        _language = Languages.FirstOrDefault(l => l.Value == Host.Config.Language) ?? Languages[0];
        _theme = Themes.FirstOrDefault(t => t.Value == Host.Config.Theme) ?? Themes[0];
        _startAtLogin = LaunchAgent.IsEnabled;
    }

    public static void ApplyTheme(string setting)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = setting switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // theo macOS
            };
    }

    [RelayCommand]
    private void Save()
    {
        var c = Host.Config;
        var langChanged = c.Language != Language.Value;
        c.Language = Language.Value;
        c.Theme = Theme.Value;
        Host.SaveConfig();
        LaunchAgent.Apply(StartAtLogin);
        ApplyTheme(c.Theme);
        Status = Loc.Instance["Saved"];
        if (langChanged)
        {
            Loc.Instance.SetLanguage(c.Language);
            LanguageChanged?.Invoke();
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => MacPlatform.Open(DataDir);
}

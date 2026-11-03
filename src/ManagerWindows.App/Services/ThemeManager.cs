using System.Windows;
using Microsoft.Win32;

namespace ManagerWindows.App.Services;

/// <summary>
/// Sáng/tối: "auto" theo Windows (AppsUseLightTheme), hoặc ép "light"/"dark".
/// Đổi cả bộ điều khiển Fluent của WPF (ThemeMode) lẫn bảng màu riêng của app (Themes/Palette.*.xaml).
/// </summary>
public static class ThemeManager
{
    private static string _setting = "auto";
    private static bool _listening;

    public static bool IsDark { get; private set; }

    public static void Apply(string setting)
    {
        _setting = setting;
        IsDark = setting switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };

        var app = Application.Current;
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Palette.") == true);
        var fresh = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Palette.{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        if (old != null) dicts[dicts.IndexOf(old)] = fresh;
        else dicts.Add(fresh);

        // Ghi lại để dò lỗi giao diện trên máy người dùng: bộ theme Fluent có được nạp không.
        var fluent = dicts.Any(d => d.Source?.OriginalString.Contains("Fluent", StringComparison.OrdinalIgnoreCase) == true);
        Log.Info("theme", $"setting={setting} dark={IsDark} fluent={fluent} os={Environment.OSVersion.Version}");

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                // Người dùng đổi sáng/tối trong Settings của Windows.
                if (e.Category == UserPreferenceCategory.General && _setting == "auto")
                    app.Dispatcher.BeginInvoke(() => Apply("auto"));
            };
        }
    }

    private static bool SystemPrefersDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ManagerWindows.App.Services;

/// <summary>
/// Sáng/tối: "auto" theo Windows (AppsUseLightTheme), hoặc ép "light"/"dark".
/// Mọi điều khiển được vẽ bằng style riêng của app (Themes/Controls.xaml, Themes/StandardControls.xaml)
/// lấy màu từ Themes/Palette.*.xaml, nên không phụ thuộc theme Fluent/Aero của máy: đổi bảng màu là đổi toàn bộ.
/// </summary>
public static class ThemeManager
{
    private static string _setting = "auto";
    private static bool _hooked;

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
        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Palette.") == true);
        var fresh = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Palette.{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        if (old != null) dicts[dicts.IndexOf(old)] = fresh;
        else dicts.Insert(0, fresh);

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Log.Info("theme", $"setting={setting} dark={IsDark} os={Environment.OSVersion.Version}");

        if (_hooked) return;
        _hooked = true;
        // Cửa sổ mở sau này (cửa sổ chính, hộp thoại xem file) cũng nhận thanh tiêu đề đúng màu.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => ApplyTitleBar((Window)s)));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            // Người dùng đổi sáng/tối trong Settings của Windows.
            if (e.Category == UserPreferenceCategory.General && _setting == "auto")
                app.Dispatcher.BeginInvoke(() => Apply("auto"));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 trước bản 20H1

    /// <summary>Thanh tiêu đề cửa sổ sáng/tối theo app (Windows 10 1809+ và Windows 11).</summary>
    private static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static bool SystemPrefersDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}

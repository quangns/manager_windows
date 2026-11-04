using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace ManagerWindows.App.Services;

/// <summary>
/// Chuỗi giao diện Việt/Anh, đổi ngôn ngữ tức thì không cần khởi động lại.
/// XAML: <c>Text="{l:T Tab_Cleanup}"</c>; code: <c>Loc.Instance["Key"]</c> hoặc <c>Loc.F("Key", ("size", x))</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();

    public string Lang { get; private set; } = "vi";

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() => SetLanguage("auto");

    public void SetLanguage(string setting)
    {
        Lang = setting switch
        {
            "vi" or "en" => setting,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en",
        };
        _strings = LoadLang(Lang);
        _fallback = LoadLang(Lang == "vi" ? "en" : "vi");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
    }

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    public static string F(string key, params (string Name, object? Value)[] args)
    {
        var s = Instance[key];
        foreach (var (name, value) in args) s = s.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.CurrentCulture));
        return s;
    }

    public static string F(string key, IReadOnlyDictionary<string, string>? args) =>
        F(key, args?.Select(a => (a.Key, (object?)a.Value)).ToArray() ?? []);

    private static Dictionary<string, string> LoadLang(string lang)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream($"ManagerWindows.App.Lang.{lang}.json");
        return s == null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
    }
}

/// <summary>Markup extension {l:T Key}: binding một chiều tới Loc.Instance[Key].</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {units[i]}";
    }

    /// <summary>Thời gian đã chạy: "3 ngày 2 giờ", "5 giờ 10 phút", "12 phút".</summary>
    public static string Age(TimeSpan t)
    {
        if (t.TotalDays >= 1) return Loc.F("Age_DH", ("d", (int)t.TotalDays), ("h", t.Hours));
        return Duration(Math.Max(t.TotalSeconds, 60));
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var h = (int)t.TotalHours;
        return h > 0
            ? Loc.F("Dur_HM", ("h", h), ("m", t.Minutes))
            : Loc.F("Dur_M", ("m", Math.Max(t.Minutes, seconds > 0 ? 1 : 0)));
    }
}

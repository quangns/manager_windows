using System.Globalization;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace ManagerMac.Services;

/// <summary>
/// Chuỗi giao diện Việt/Anh: dùng chung file ngôn ngữ của bản Windows, mac.*.json ghi đè/bổ sung chuỗi riêng macOS.
/// Đổi ngôn ngữ thì cửa sổ được dựng lại.
/// </summary>
public sealed class Loc
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();

    public string Lang { get; private set; } = "vi";

    private Loc() => SetLanguage("auto");

    public void SetLanguage(string setting)
    {
        Lang = setting is "vi" or "en" ? setting
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en";
        _strings = Load(Lang);
        _fallback = Load(Lang == "vi" ? "en" : "vi");
    }

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    public static string F(string key, params (string Name, object? Value)[] args)
    {
        var s = Instance[key];
        foreach (var (name, value) in args) s = s.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.CurrentCulture));
        return s;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        var result = new Dictionary<string, string>();
        foreach (var name in new[] { $"lang/{lang}.json", $"lang/mac-{lang}.json" })
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream(name);
            if (s == null) continue;
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new()) result[kv.Key] = kv.Value;
        }
        return result;
    }
}

/// <summary>{l:T Key}: chuỗi theo ngôn ngữ hiện tại.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Instance[Key];
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

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var h = (int)t.TotalHours;
        return h > 0 ? Loc.F("Dur_HM", ("h", h), ("m", t.Minutes)) : Loc.F("Dur_M", ("m", Math.Max(t.Minutes, seconds > 0 ? 1 : 0)));
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static string Dir { get; set; } = Path.GetTempPath();

    public static void Error(string source, Exception ex) => Write("ERROR", source, ex.ToString());
    public static void Info(string source, string message) => Write("INFO", source, message);

    private static void Write(string level, string source, string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Gate)
                File.AppendAllText(Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss} [{level}] {source}: {message}{Environment.NewLine}");
        }
        catch { }
    }
}

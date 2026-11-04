namespace ManagerWindows.Core.Tools;

/// <summary>Tổ hợp phím tắt toàn cục, đọc/ghi dạng "Ctrl+Shift+Alt+Win+Key" (thứ tự cố định khi ghi).</summary>
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    private static readonly string[] AloneOk = ["PrintScreen", "Snapshot", "Pause", "Scroll"];

    /// <summary>Có thể đăng ký: phải có phím chính, và có Ctrl/Alt/Win (Shift + chữ sẽ cản việc gõ chữ hoa) trừ F1–F24, Print Screen….</summary>
    public bool IsValid => Key.Length > 0 && (Ctrl || Alt || Win || IsFunctionKey(Key) || AloneOk.Contains(Key, StringComparer.OrdinalIgnoreCase));

    private static bool IsFunctionKey(string k) =>
        k.Length is 2 or 3 && (k[0] is 'F' or 'f') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        if (Key.Length > 0) parts.Add(Key);
        return string.Join("+", parts);
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        string key = "";
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key.Length > 0) return null; // hai phím chính
                    key = raw.Length == 1 ? raw.ToUpperInvariant() : raw;
                    break;
            }
        }
        var hk = new Hotkey(ctrl, alt, shift, win, key);
        return hk.IsValid ? hk : null;
    }
}

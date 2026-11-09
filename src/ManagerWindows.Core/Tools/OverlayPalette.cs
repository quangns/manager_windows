using System.Globalization;

namespace ManagerWindows.Core.Tools;

/// <summary>Màu của mini monitor (ARGB). <see cref="LightText"/> = chữ sáng trên nền tối, ngược lại chữ tối trên nền sáng.</summary>
public sealed record OverlayColors(uint Background, bool LightText, uint Label, uint Value, uint Warning, uint Critical, uint Outline);

/// <summary>
/// Chọn màu mini monitor để chữ luôn đọc được:
/// nền màu → chữ sáng/tối theo độ sáng của nền; nền trong suốt → theo chế độ sáng/tối của app
/// (chế độ sáng: cửa sổ phía sau thường sáng nên dùng chữ tối viền trắng). Người dùng có thể ép "light"/"dark".
/// </summary>
public static class OverlayPalette
{
    public const string DefaultColor = "#1F1F23";
    /// <summary>Màu gợi ý trong tab Công cụ: than, trắng, xanh teal đậm, xanh dương đậm, đen.</summary>
    public static readonly string[] Presets = ["#1F1F23", "#FFFFFF", "#0F3D3A", "#1E3A8A", "#000000"];

    public static OverlayColors Resolve(OverlayOptions o, bool appDark)
    {
        var solid = o.Background == "solid";
        var rgb = ParseRgb(o.BackgroundColor) ?? ParseRgb(DefaultColor)!.Value;
        var light = o.TextColor switch
        {
            "light" => true,
            "dark" => false,
            _ => solid ? Luminance(rgb) < 0.45 : appDark,
        };
        // Nền trong suốt vẫn để alpha 1 để bấm/kéo được; "click xuyên" thì trong suốt hẳn.
        uint alpha = solid
            ? (uint)Math.Round(Math.Clamp(o.BackgroundOpacity, 10, 100) * 255 / 100.0)
            : o.ClickThrough ? 0u : 1u;
        var bg = (alpha << 24) | rgb;
        return light
            ? new OverlayColors(bg, true, 0xFFB4E4DD, 0xFFFFFFFF, 0xFFFBD34D, 0xFFFF6B6B, 0xFF000000)
            : new OverlayColors(bg, false, 0xFF0F766E, 0xFF18181B, 0xFFB45309, 0xFFB91C1C, 0xFFFFFFFF);
    }

    /// <summary>#RRGGBB hoặc RRGGBB → 0xRRGGBB; sai định dạng → null.</summary>
    public static uint? ParseRgb(string? hex)
    {
        var h = hex?.Trim().TrimStart('#');
        return h is { Length: 6 } && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Độ sáng tương đối (WCAG) 0–1 của màu 0xRRGGBB.</summary>
    public static double Luminance(uint rgb)
    {
        static double Ch(uint c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch((rgb >> 16) & 0xFF) + 0.7152 * Ch((rgb >> 8) & 0xFF) + 0.0722 * Ch(rgb & 0xFF);
    }

    /// <summary>Tỉ lệ tương phản WCAG giữa hai màu 0xRRGGBB (1–21).</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}

using ManagerWindows.Core.Tools;

namespace ManagerWindows.Core.Tests;

public class ToolsTests
{
    [Theory]
    [InlineData("Ctrl+Alt+A", "Ctrl+Alt+A")]
    [InlineData("alt + ctrl + a", "Ctrl+Alt+A")]
    [InlineData("Win+Shift+S", "Shift+Win+S")]
    [InlineData("F9", "F9")]
    [InlineData("PrintScreen", "PrintScreen")]
    [InlineData("Ctrl+Shift+D1", "Ctrl+Shift+D1")]
    public void Parses_and_normalizes_hotkeys(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Parse(text)?.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("A")]          // thiếu phím bổ trợ: sẽ chặn việc gõ chữ
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Alt")]   // thiếu phím chính
    [InlineData("Ctrl+A+B")]
    public void Rejects_invalid_hotkeys(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Gpu_usage_sums_processes_per_engine_and_takes_busiest()
    {
        var samples = new[]
        {
            ("pid_100_luid_0x0_0xD1_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_200_luid_0x0_0xD1_phys_0_eng_0_engtype_3D", 15.0),
            ("pid_100_luid_0x0_0xD1_phys_0_eng_5_engtype_VideoDecode", 30.0),
            ("pid_300_luid_0x0_0xAA_phys_0_eng_0_engtype_3D", 5.0), // GPU khác
        };
        Assert.Equal(35.0, GpuUsage.Aggregate(samples));
        Assert.Equal(0, GpuUsage.Aggregate([]));
        Assert.Equal(100, GpuUsage.Aggregate([("pid_1_luid_a_eng_0_engtype_3D", 80.0), ("pid_2_luid_a_eng_0_engtype_3D", 70.0)]));
    }
}

public class OverlayPaletteTests
{
    private static OverlayOptions O(string bg = "transparent", string color = "#1F1F23", string text = "auto", int opacity = 80, bool through = false) =>
        new() { Background = bg, BackgroundColor = color, TextColor = text, BackgroundOpacity = opacity, ClickThrough = through };

    [Fact]
    public void Transparent_background_follows_app_theme()
    {
        Assert.True(OverlayPalette.Resolve(O(), appDark: true).LightText);
        Assert.False(OverlayPalette.Resolve(O(), appDark: false).LightText); // chế độ sáng: chữ tối
        Assert.Equal(1u, OverlayPalette.Resolve(O(), true).Background >> 24);       // vẫn bấm/kéo được
        Assert.Equal(0u, OverlayPalette.Resolve(O(through: true), true).Background >> 24);
    }

    [Theory]
    [InlineData("#FFFFFF", false)]
    [InlineData("#FDD835", false)]
    [InlineData("#1F1F23", true)]
    [InlineData("#1E3A8A", true)]
    [InlineData("#0F3D3A", true)]
    public void Solid_background_picks_readable_text(string color, bool lightText)
    {
        // Chế độ app không ảnh hưởng: chỉ theo màu nền.
        Assert.Equal(lightText, OverlayPalette.Resolve(O("solid", color), appDark: false).LightText);
        Assert.Equal(lightText, OverlayPalette.Resolve(O("solid", color), appDark: true).LightText);
    }

    [Fact]
    public void Chosen_text_colors_meet_contrast_on_every_preset()
    {
        foreach (var hex in OverlayPalette.Presets)
        {
            var bg = OverlayPalette.ParseRgb(hex)!.Value;
            var c = OverlayPalette.Resolve(O("solid", hex, opacity: 100), appDark: false);
            Assert.True(OverlayPalette.Contrast(c.Value & 0xFFFFFF, bg) >= 4.5, $"{hex}: số liệu");
            Assert.True(OverlayPalette.Contrast(c.Label & 0xFFFFFF, bg) >= 3.0, $"{hex}: nhãn");
        }
    }

    [Fact]
    public void Forced_text_color_opacity_and_bad_input()
    {
        Assert.True(OverlayPalette.Resolve(O("solid", "#FFFFFF", "light"), false).LightText);
        Assert.False(OverlayPalette.Resolve(O(text: "dark"), true).LightText);
        Assert.Equal(0xCCu, OverlayPalette.Resolve(O("solid", opacity: 80), true).Background >> 24);
        Assert.Equal(26u, OverlayPalette.Resolve(O("solid", opacity: 0), true).Background >> 24); // tối thiểu 10%
        Assert.Equal(0x1F1F23u, OverlayPalette.Resolve(O("solid", "xanh"), true).Background & 0xFFFFFF); // sai → màu mặc định
        Assert.Null(OverlayPalette.ParseRgb("#12345"));
        Assert.Equal(0xABCDEFu, OverlayPalette.ParseRgb(" abcdef "));
    }
}

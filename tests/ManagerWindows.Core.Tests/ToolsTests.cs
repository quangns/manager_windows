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

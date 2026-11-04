using System.Text.RegularExpressions;

namespace ManagerWindows.Core.Tests;

/// <summary>
/// Chặn lỗi "chữ đen ở chế độ tối": theme mặc định của WPF (Aero2) gán cứng màu chữ cho một số điều khiển
/// (TabItem = Black, Hyperlink = Red, DatePicker…). Style của app phải tự đặt Foreground cho các điều khiển đó,
/// và mọi màu chữ trong XAML phải là DynamicResource để đổi theo sáng/tối.
/// </summary>
public class ThemeXamlTests
{
    private static string AppDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "ManagerWindows.App"));

    private static IEnumerable<(string File, string Text)> Xaml() =>
        Directory.EnumerateFiles(AppDir(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    /// <summary>Kiểu điều khiển mà Aero2 gán cứng Foreground (không qua màu hệ thống).</summary>
    private static readonly string[] HardCodedForeground = ["TabItem", "Hyperlink", "DatePicker", "Slider", "Menu"];

    [Fact]
    public void Styles_for_hardcoded_foreground_controls_set_their_own_foreground()
    {
        foreach (var (file, text) in Xaml())
            foreach (Match m in Regex.Matches(text, @"<Style\b[^>]*TargetType=""(?:\{x:Type )?(\w+)\}?""[^>]*>(.*?)</Style>", RegexOptions.Singleline))
            {
                if (!HardCodedForeground.Contains(m.Groups[1].Value)) continue;
                Assert.True(Regex.IsMatch(m.Groups[2].Value, @"<Setter Property=""Foreground"" Value=""\{DynamicResource \w+\}"""),
                    $"{Path.GetFileName(file)}: style {m.Groups[1].Value} phải tự đặt Foreground bằng DynamicResource");
            }
    }

    [Fact]
    public void Used_hardcoded_foreground_controls_have_a_style()
    {
        var all = string.Join("\n", Xaml().Select(x => x.Text));
        foreach (var type in HardCodedForeground)
        {
            if (!Regex.IsMatch(all, $@"<{type}\b")) continue; // không dùng điều khiển này
            Assert.Matches($@"TargetType=""(\{{x:Type )?{type}\}}?""", all);
        }
        // TabItem dùng làm trang nên bắt buộc có style.
        Assert.Matches(@"<Style x:Key=""NavItem"" TargetType=""TabItem"">", all);
    }

    [Fact]
    public void Text_colors_are_dynamic_resources()
    {
        foreach (var (file, text) in Xaml())
        {
            // Màu chữ gán cứng (Foreground="Black", "#000"...) hoặc StaticResource sẽ không đổi khi chuyển sáng/tối.
            var bad = Regex.Matches(text, @"(?<![\w.])Foreground=""(?!\{DynamicResource |\{TemplateBinding |\{Binding |White"")[^""]+""")
                .Select(m => m.Value).ToList();
            Assert.True(bad.Count == 0, $"{Path.GetFileName(file)}: {string.Join(", ", bad)}");
        }
    }
}

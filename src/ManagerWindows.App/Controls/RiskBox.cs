using System.Windows;
using System.Windows.Controls;
using ManagerWindows.App.Services;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Khung cảnh báo rủi ro nổi bật (nền đỏ nhạt, viền đỏ, biểu tượng ⚠, danh sách gạch đầu dòng) cho thao tác phân vùng.
/// Màu lấy từ bảng màu (CriticalWash / Critical / Ink) nên đọc rõ ở cả chế độ sáng và tối.
/// </summary>
public static class RiskBox
{
    public static Border Create(IEnumerable<string> risks, string? title = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var icon = new TextBlock { Text = "\uE7BA", FontSize = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Critical");
        var head = new TextBlock { Text = title ?? Loc.Instance["Risk_Title"], FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Critical");
        header.Children.Add(icon);
        header.Children.Add(head);

        var list = new StackPanel();
        list.Children.Add(header);
        foreach (var r in risks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            var dot = new TextBlock { Text = "•", FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 8, 0) };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "Critical");
            var text = new TextBlock { Text = r, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.Children.Add(dot);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        var box = new Border
        {
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 10),
            Margin = new Thickness(0, 4, 0, 6),
            Child = list,
        };
        box.SetResourceReference(Border.BackgroundProperty, "CriticalWash");
        box.SetResourceReference(Border.BorderBrushProperty, "Critical");
        return box;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ManagerWindows.App.Controls;

/// <summary>Đầu trang: tiêu đề + mô tả một dòng bên trái, nút hành động bên phải (xem DESIGN.md mục 4).</summary>
public sealed class PageHeader : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader));
    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader));
    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}

/// <summary>Một dòng thiết lập: nhãn + mô tả bên trái, điều khiển (Content) bên phải.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>
/// c:Table.Apply="True": đặt các thuộc tính bảng chung mà vẫn giữ style Fluent mặc định của DataGrid
/// (gán Style riêng sẽ làm mất style Fluent sáng/tối).
/// </summary>
public static class Table
{
    public static readonly DependencyProperty ApplyProperty =
        DependencyProperty.RegisterAttached("Apply", typeof(bool), typeof(Table), new PropertyMetadata(false, OnApply));

    public static bool GetApply(DependencyObject d) => (bool)d.GetValue(ApplyProperty);
    public static void SetApply(DependencyObject d, bool value) => d.SetValue(ApplyProperty, value);

    private static void OnApply(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g || e.NewValue is not true) return;
        g.AutoGenerateColumns = false;
        g.CanUserAddRows = false;
        g.CanUserDeleteRows = false;
        g.CanUserResizeRows = false;
        g.HeadersVisibility = DataGridHeadersVisibility.Column;
        g.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        g.SelectionMode = DataGridSelectionMode.Single;
        g.RowHeight = 36;
        g.BorderThickness = new Thickness(0);
        g.Background = System.Windows.Media.Brushes.Transparent;
        g.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Hairline");

        // Màu chữ/nền hàng và tiêu đề cột theo bảng màu của app, để chế độ tối không ra chữ đen.
        g.SetResourceReference(Control.ForegroundProperty, "Ink");
        g.SetResourceReference(DataGrid.RowBackgroundProperty, "Surface");
        g.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Surface");
        var baseHeader = g.TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var header = new Style(typeof(DataGridColumnHeader), baseHeader);
        header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("InkMuted")));
        header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        header.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Hairline")));
        header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        g.ColumnHeaderStyle = header;
    }
}

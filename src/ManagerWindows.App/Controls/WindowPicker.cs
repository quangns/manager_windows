using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Biểu tượng tâm ngắm "kéo để chọn app" (giống EVKey): giữ chuột trái vào biểu tượng, kéo tới cửa sổ app bất kỳ rồi thả.
/// Trong lúc kéo gọi <see cref="HoverCommand"/> với PID dưới con trỏ; khi thả gọi <see cref="PickedCommand"/>.
/// Chuột được "bắt" (CaptureMouse) nên vẫn nhận sự kiện khi con trỏ ra ngoài cửa sổ app.
/// </summary>
public sealed class WindowPicker : Border
{
    public static readonly DependencyProperty PickedCommandProperty =
        DependencyProperty.Register(nameof(PickedCommand), typeof(ICommand), typeof(WindowPicker));
    public static readonly DependencyProperty HoverCommandProperty =
        DependencyProperty.Register(nameof(HoverCommand), typeof(ICommand), typeof(WindowPicker));

    public ICommand? PickedCommand { get => (ICommand?)GetValue(PickedCommandProperty); set => SetValue(PickedCommandProperty, value); }
    public ICommand? HoverCommand { get => (ICommand?)GetValue(HoverCommandProperty); set => SetValue(HoverCommandProperty, value); }

    private readonly System.Windows.Shapes.Path _icon;
    private bool _dragging;
    private int _lastPid = -1;

    public WindowPicker()
    {
        Width = 48;
        Height = 48;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        SetResourceReference(BackgroundProperty, "Raised");
        SetResourceReference(BorderBrushProperty, "Hairline");
        _icon = new System.Windows.Shapes.Path
        {
            // Tâm ngắm: vòng tròn + 4 vạch
            Data = Geometry.Parse("M12,4 A8,8 0 1 1 11.99,4 Z M12,0 L12,7 M12,17 L12,24 M0,12 L7,12 M17,12 L24,12"),
            StrokeThickness = 2,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _icon.SetResourceReference(Shape.StrokeProperty, "Accent");
        Child = _icon;
        ToolTip = Services.Loc.Instance["Hib_PickerTip"];
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = CaptureMouse();
        if (!_dragging) return;
        _lastPid = -1;
        Mouse.OverrideCursor = Cursors.Cross;
        _icon.Visibility = Visibility.Hidden; // "nhấc" tâm ngắm lên theo con trỏ như EVKey
        SetResourceReference(BorderBrushProperty, "Accent");
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var pid = WindowInfo.PidAtCursor();
        if (pid == _lastPid) return;
        _lastPid = pid;
        if (HoverCommand?.CanExecute(pid) == true) HoverCommand.Execute(pid);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        var pid = WindowInfo.PidAtCursor();
        End();
        if (PickedCommand?.CanExecute(pid) == true) PickedCommand.Execute(pid);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) End(); // bấm Esc / mất focus giữa chừng: huỷ
    }

    private void End()
    {
        _dragging = false;
        Mouse.OverrideCursor = null;
        _icon.Visibility = Visibility.Visible;
        SetResourceReference(BorderBrushProperty, "Hairline");
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Khung tô quanh cửa sổ đang được tâm ngắm trỏ vào (giống Spy++/EVKey), để biết chắc sẽ chọn đúng app.
/// Cửa sổ trong suốt, click xuyên, không lấy focus; toạ độ theo pixel thật nên đúng với mọi mức phóng to.
/// </summary>
public sealed class PickerHighlight : Window
{
    private const int WM_DPICHANGED = 0x02E0;
    private PixelRect _rect;

    public PickerHighlight()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        var accent = (TryFindResource("AccentFill") as SolidColorBrush)?.Color ?? Color.FromRgb(0x0F, 0x76, 0x6E);
        Content = new Border
        {
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(accent),
            Background = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)),
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            DesktopWindows.SetOverlayStyle(h, clickThrough: true);
            // Trải qua màn hình khác độ phóng to: không để WPF tự co/giãn theo kích thước gợi ý, đặt lại đúng khung.
            HwndSource.FromHwnd(h)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg == WM_DPICHANGED) { handled = true; Dispatcher.BeginInvoke(() => Place()); }
                return IntPtr.Zero;
            });
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Tô quanh khung cửa sổ <paramref name="target"/>; Zero = ẩn khung.</summary>
    public void Show(IntPtr target)
    {
        var r = target == IntPtr.Zero ? default : DesktopWindows.Bounds(target);
        if (r.IsEmpty)
        {
            Hide();
            return;
        }
        _rect = r;
        if (!IsVisible) Show();
        Place();
    }

    private void Place()
    {
        var h = Handle;
        if (h != IntPtr.Zero && !_rect.IsEmpty) DesktopWindows.PlaceTopMost(h, _rect);
    }
}

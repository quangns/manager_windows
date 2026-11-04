using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Views;

/// <summary>Thông báo ngắn giữa cạnh dưới màn hình (vd. "Đã ghim …"), tự tắt, không lấy focus của app đang dùng.</summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    private ToastWindow(string message)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var text = new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 10, 18, 10), BorderThickness = new Thickness(1), Child = text };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        Content = border;
        SourceInitialized += (_, _) => DesktopWindows.SetOverlayStyle(new WindowInteropHelper(this).Handle, clickThrough: true);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 48;
        };
    }

    public static void Show(string message, double seconds = 1.8)
    {
        _current?.Close();
        var t = new ToastWindow(message);
        _current = t;
        t.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { t.Close(); if (_current == t) _current = null; };
            t.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }
}

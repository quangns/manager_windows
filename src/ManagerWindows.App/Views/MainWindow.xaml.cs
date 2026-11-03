using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ManagerWindows.App.Views;

public partial class MainWindow : Window
{
    private bool _reallyClose;

    public MainWindow() => InitializeComponent();

    /// <summary>Đóng hẳn (khi thoát app hoặc dựng lại cửa sổ).</summary>
    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    /// <summary>Bấm X chỉ ẩn xuống khay, app vẫn chạy nền.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>Đổi trang: nội dung mờ dần 150ms (chỉ đổi opacity, theo DESIGN.md mục 6).</summary>
    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Nav) return;
        if (Nav.Template?.FindName("PageHost", Nav) is UIElement host)
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }
}

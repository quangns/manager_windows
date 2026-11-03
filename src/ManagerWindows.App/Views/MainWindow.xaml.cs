using System.ComponentModel;
using System.Windows;

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
}

using Avalonia.Controls;

namespace ManagerMac.Views;

public partial class MainWindow : Window
{
    private bool _reallyClose;

    public MainWindow() => InitializeComponent();

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    /// <summary>Bấm nút đóng (đỏ) chỉ ẩn cửa sổ; app vẫn chạy trên thanh menu.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

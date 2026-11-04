using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using ManagerWindows.App.Services;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Views;

/// <summary>
/// Chọn vùng chụp trên ảnh màn hình đã "đóng băng" (giống Snipaste): rê chuột thấy khung cửa sổ dưới con trỏ,
/// bấm một cái để chụp cả cửa sổ đó, hoặc kéo để khoanh vùng tự do. Esc / chuột phải để huỷ.
/// Cửa sổ phủ toàn bộ màn hình ảo theo pixel; toạ độ chuột được quy đổi theo tỉ lệ ảnh nên đúng với mọi mức DPI.
/// </summary>
public sealed class RegionSelectWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly PixelRect _screen;
    private readonly List<DesktopWindow> _windows;
    private readonly Canvas _canvas = new();
    private readonly Path _dim;
    private readonly Rectangle _frame;
    private readonly Border _label;
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12 };
    private Point? _dragStart;
    private Rect _selection = Rect.Empty;

    /// <summary>Vùng đã chọn, pixel tính từ góc trên trái ảnh chụp; null = huỷ.</summary>
    public Int32Rect? Result { get; private set; }

    public RegionSelectWindow(BitmapSource shot, PixelRect screen, List<DesktopWindow> windows)
    {
        _shot = shot;
        _screen = screen;
        _windows = windows;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "Manager Windows – capture";

        var image = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
        var accent = (Brush)(TryFindResource("AccentFill") ?? Brushes.DeepSkyBlue);
        _frame = new Rectangle { Stroke = accent, StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x1B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Child = _labelText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x1B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            Child = new TextBlock { Text = Loc.Instance["Cap_SelectHint"], Foreground = Brushes.White, FontSize = 13 },
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(hint);
        Content = new Grid { Children = { image, _canvas } };
        _canvas.Background = Brushes.Transparent; // nhận sự kiện chuột trên toàn vùng

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Place(); // cửa sổ trải nhiều màn hình khác DPI: Windows có thể đổi kích thước, đặt lại
        Loaded += (_, _) => { Place(); Activate(); Focus(); UpdateDim(); };
        SizeChanged += (_, _) => UpdateDim();
    }

    private void Place() => DesktopWindows.PlaceTopMost(new WindowInteropHelper(this).Handle, _screen);

    /// <summary>DIP trong cửa sổ → pixel trên ảnh chụp.</summary>
    private (double X, double Y) ToPixels(Point p) =>
        (p.X * _shot.PixelWidth / Math.Max(1, ActualWidth), p.Y * _shot.PixelHeight / Math.Max(1, ActualHeight));

    private Rect ToDip(PixelRect r)
    {
        double sx = ActualWidth / _shot.PixelWidth, sy = ActualHeight / _shot.PixelHeight;
        return new Rect((r.X - _screen.X) * sx, (r.Y - _screen.Y) * sy, r.Width * sx, r.Height * sy);
    }

    private DesktopWindow? WindowAt(Point p)
    {
        var (x, y) = ToPixels(p);
        int ax = _screen.X + (int)x, ay = _screen.Y + (int)y;
        return _windows.FirstOrDefault(w => w.Bounds.Contains(ax, ay));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragStart is { } s && e.LeftButton == MouseButtonState.Pressed)
            _selection = new Rect(s, p);
        else
        {
            var w = WindowAt(p);
            _selection = w != null ? Rect.Intersect(ToDip(w.Bounds), new Rect(0, 0, ActualWidth, ActualHeight)) : Rect.Empty;
        }
        UpdateDim();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        var start = _dragStart;
        _dragStart = null;
        if (start == null) return;
        var p = e.GetPosition(this);
        // Bấm không kéo: lấy cả cửa sổ đang được tô khung.
        var sel = (p - start.Value).Length < 4 ? _selection : new Rect(start.Value, p);
        if (sel.IsEmpty || sel.Width < 2 || sel.Height < 2) return;
        var (x1, y1) = ToPixels(sel.TopLeft);
        var (x2, y2) = ToPixels(sel.BottomRight);
        int left = Math.Clamp((int)Math.Round(x1), 0, _shot.PixelWidth - 1), top = Math.Clamp((int)Math.Round(y1), 0, _shot.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Round(x2), left + 1, _shot.PixelWidth), bottom = Math.Clamp((int)Math.Round(y2), top + 1, _shot.PixelHeight);
        Result = new Int32Rect(left, top, right - left, bottom - top);
        Close();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (_selection.IsEmpty)
        {
            _dim.Data = full;
            _frame.Visibility = _label.Visibility = Visibility.Collapsed;
            return;
        }
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
        _frame.Visibility = _label.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, _selection.X);
        Canvas.SetTop(_frame, _selection.Y);
        _frame.Width = _selection.Width;
        _frame.Height = _selection.Height;
        var (w1, h1) = ToPixels(new Point(_selection.Width, _selection.Height));
        _labelText.Text = $"{(int)Math.Round(w1)} × {(int)Math.Round(h1)}";
        Canvas.SetLeft(_label, _selection.X);
        Canvas.SetTop(_label, Math.Max(0, _selection.Y - 26));
    }
}

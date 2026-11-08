using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using ManagerWindows.App.Controls;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Tools;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Views;

/// <summary>
/// Chụp màn hình kiểu Lightshot: ảnh màn hình được "đóng băng" đúng kích thước thật (1 pixel ảnh = 1 pixel màn hình, không thu nhỏ).
/// <list type="bullet">
/// <item>Chưa có vùng chọn: rê chuột thấy khung cửa sổ, bấm để lấy cả cửa sổ, kéo để khoanh vùng tự do.</item>
/// <item>Đã có vùng chọn: kéo 8 cạnh/góc để đổi kích thước, kéo bên trong để di chuyển, kéo bên ngoài để khoanh lại;
/// thanh công cụ dọc (vẽ) và ngang (sao chép, lưu…) bám theo vùng chọn; vẽ thẳng lên màn hình.</item>
/// </list>
/// Toạ độ vùng chọn và nét vẽ lưu theo pixel ảnh; quy đổi sang DIP chỉ để hiển thị nên đúng với mọi mức DPI.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    /// <summary>Bảng màu: đỏ, cam, vàng, xanh lá, xanh dương, tím, đen, trắng.</summary>
    private static readonly string[] Palette = ["#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5", "#8E24AA", "#000000", "#FFFFFF"];
    private static readonly Brush BarBrush = Frozen(Color.FromArgb(0xF0, 0x1F, 0x1F, 0x23));
    private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveBrush = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private enum Drag { None, Create, Move, Resize, Stroke }

    private readonly BitmapSource _shot;
    private readonly PixelRect _screen;
    private readonly List<DesktopWindow> _windows;
    private readonly bool _quick;
    private readonly Grid _root = new();
    private readonly Canvas _ui = new() { Background = Brushes.Transparent }; // nhận chuột trên toàn màn hình
    private readonly AnnotationCanvas _board = new();
    private readonly Path _dim = new() { Fill = Frozen(Color.FromArgb(0x80, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Border _sizeLabel, _hint;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Border _sideBar, _bottomBar;
    private readonly Dictionary<AnnotationTool, Chip> _toolChips = new();
    private readonly List<Chip> _colorChips = new(), _thickChips = new();

    private Box _sel;           // vùng chọn, pixel ảnh; rỗng = chưa chọn
    private Box _hover;         // khung cửa sổ dưới con trỏ (khi chưa chọn)
    private bool _editing;      // đã có vùng chọn, đang chỉnh
    private AnnotationTool? _tool;
    private Drag _drag;
    private Point _dragFrom;    // pixel ảnh
    private Box _dragOrig;
    private SelectionZone _zone;
    private string _status = "";

    /// <summary>Chế độ nhanh: vùng đã chọn (pixel ảnh). Chế độ chỉnh sửa tự sao chép/lưu nên luôn null.</summary>
    public Int32Rect? Result { get; private set; }

    /// <param name="initial">Vùng chọn sẵn (pixel ảnh), ví dụ cửa sổ đang dùng hoặc cả màn hình; null = để người dùng khoanh.</param>
    /// <param name="quick">true = khoanh xong là đóng, trả <see cref="Result"/> (tắt "chỉnh sửa sau khi chụp").</param>
    public CaptureOverlayWindow(BitmapSource shot, PixelRect screen, List<DesktopWindow> windows, Box? initial, bool quick)
    {
        _shot = shot;
        _screen = screen;
        _windows = windows;
        _quick = quick;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "Manager Windows – capture";
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        var opt = AppHost.Current.Config.Tools.Capture;
        _board.SetSource(shot);
        _board.Color = ParseColor(opt.Color) ?? Colors.Red;
        _board.ThicknessLevel = Math.Clamp(opt.Thickness, 1, 3);

        // Ảnh nền và lớp nét vẽ cùng được kéo giãn đúng bằng cửa sổ (cửa sổ = cả màn hình ảo theo pixel) → hiển thị 1:1.
        var image = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var accent = (Brush)(TryFindResource("AccentFill") ?? Brushes.DeepSkyBlue);
        _frame.Stroke = accent;
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _ui.Children.Add(_handles[i]);
        }
        _sizeLabel = Pill(_sizeText, 4);
        _hint = Pill(new TextBlock { Text = Loc.Instance["Cap_SelectHint"], Foreground = Brushes.White, FontSize = 13 }, 6);
        _hint.Padding = new Thickness(14, 8, 14, 8);
        Canvas.SetLeft(_hint, 24);
        Canvas.SetTop(_hint, 24);
        _sideBar = BuildSideBar();
        _bottomBar = BuildBottomBar();
        _ui.Children.Insert(0, _frame);
        _ui.Children.Insert(0, _dim);
        _ui.Children.Add(_sizeLabel);
        _ui.Children.Add(_hint);
        _ui.Children.Add(_sideBar);
        _ui.Children.Add(_bottomBar);
        _root.Children.Add(image);
        _root.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = _board, IsHitTestVisible = false });
        _root.Children.Add(_ui);
        Content = _root;

        if (initial is { IsEmpty: false } init && !quick)
        {
            _sel = init.Intersect(ShotBox);
            _editing = !_sel.IsEmpty;
        }

        SourceInitialized += (_, _) => Place();
        DpiChanged += (_, _) => Place(); // trải nhiều màn hình khác DPI: Windows có thể đổi kích thước, đặt lại
        Loaded += (_, _) => { Place(); Activate(); Focus(); Refresh(); };
        SizeChanged += (_, _) => Refresh();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color? ParseColor(string? hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? null : (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return null; }
    }

    private static Border Pill(UIElement child, double radius) => new()
    {
        Background = BarBrush,
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(8, 3, 8, 3),
        Child = child,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private void Place() => DesktopWindows.PlaceTopMost(new WindowInteropHelper(this).Handle, _screen);

    // ---------------- Quy đổi toạ độ ----------------

    private Box ShotBox => new(0, 0, _shot.PixelWidth, _shot.PixelHeight);
    private double Sx => _shot.PixelWidth / Math.Max(1, _root.ActualWidth);
    private double Sy => _shot.PixelHeight / Math.Max(1, _root.ActualHeight);
    private Point ToPx(Point dip) => new(dip.X * Sx, dip.Y * Sy);
    private Box ToDip(Box px) => new(px.X / Sx, px.Y / Sy, px.Width / Sx, px.Height / Sy);
    private Box ScreenDip => new(0, 0, _root.ActualWidth, _root.ActualHeight);

    private Box HoverWindow(Point px)
    {
        int ax = _screen.X + (int)px.X, ay = _screen.Y + (int)px.Y;
        var w = _windows.FirstOrDefault(w => w.Bounds.Contains(ax, ay));
        return w == null ? default : new Box(w.Bounds.X - _screen.X, w.Bounds.Y - _screen.Y, w.Bounds.Width, w.Bounds.Height).Intersect(ShotBox);
    }

    private Int32Rect SelectionPixels()
    {
        int l = Math.Clamp((int)Math.Round(_sel.X), 0, _shot.PixelWidth - 1), t = Math.Clamp((int)Math.Round(_sel.Y), 0, _shot.PixelHeight - 1);
        int r = Math.Clamp((int)Math.Round(_sel.Right), l + 1, _shot.PixelWidth), b = Math.Clamp((int)Math.Round(_sel.Bottom), t + 1, _shot.PixelHeight);
        return new Int32Rect(l, t, r - l, b - t);
    }

    // ---------------- Chuột ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var dip = e.GetPosition(_root);
        var px = ToPx(dip);
        _status = "";
        _dragFrom = px;
        _dragOrig = _sel;
        if (!_editing) _drag = Drag.Create;
        else
        {
            _zone = CaptureGeometry.HitTest(ToDip(_sel), dip.X, dip.Y);
            if (_zone == SelectionZone.Outside) _drag = Drag.Create; // kéo ra ngoài: khoanh lại (nét đã vẽ vẫn giữ)
            else if (_zone != SelectionZone.Inside) _drag = Drag.Resize;
            else if (_tool == null) _drag = Drag.Move;
            else
            {
                _board.Bounds = new Rect(_sel.X, _sel.Y, _sel.Width, _sel.Height);
                _drag = _board.BeginStroke(px) ? Drag.Stroke : Drag.None;
            }
        }
        if (_drag != Drag.None) CaptureMouse();
        e.Handled = true;
        Refresh();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var dip = e.GetPosition(_root);
        var px = ToPx(dip);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_drag)
        {
            case Drag.Create:
                _sel = Box.FromCorners(_dragFrom.X, _dragFrom.Y, Math.Clamp(px.X, 0, _shot.PixelWidth), Math.Clamp(px.Y, 0, _shot.PixelHeight));
                break;
            case Drag.Move:
                _sel = CaptureGeometry.Move(_dragOrig, px.X - _dragFrom.X, px.Y - _dragFrom.Y, ShotBox);
                break;
            case Drag.Resize:
                _sel = CaptureGeometry.Resize(_dragOrig, _zone, px.X, px.Y, ShotBox);
                break;
            case Drag.Stroke:
                _board.UpdateStroke(px, shift);
                return;
            default:
                if (!_editing) _hover = HoverWindow(px);
                else UpdateCursor(dip);
                break;
        }
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;
        var px = ToPx(e.GetPosition(_root));
        if (drag == Drag.Stroke) { _board.EndStroke(px); return; }
        if (drag == Drag.Create)
        {
            var tiny = (px - _dragFrom).Length < 4 * Sx || _sel.Width < 2 || _sel.Height < 2;
            // Bấm không kéo khi chưa chọn: lấy cả cửa sổ đang tô khung. Đang chỉnh mà bấm ra ngoài: giữ vùng cũ.
            if (tiny) _sel = _editing ? _dragOrig : _hover;
            if (_sel.IsEmpty || _sel.Width < 2 || _sel.Height < 2) { _sel = _dragOrig; Refresh(); return; }
            if (_quick)
            {
                Result = SelectionPixels();
                Close();
                return;
            }
            _editing = true;
        }
        if (_sel.Width < 2 || _sel.Height < 2) _sel = _dragOrig; // đổi kích thước về 0: trả lại vùng cũ
        Refresh();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    private void UpdateCursor(Point dip)
    {
        var z = CaptureGeometry.HitTest(ToDip(_sel), dip.X, dip.Y);
        Cursor = z switch
        {
            SelectionZone.Inside => _tool == null ? Cursors.SizeAll : _tool == AnnotationTool.Number ? Cursors.Hand : Cursors.Cross,
            SelectionZone.Left or SelectionZone.Right => Cursors.SizeWE,
            SelectionZone.Top or SelectionZone.Bottom => Cursors.SizeNS,
            SelectionZone.Left | SelectionZone.Top or SelectionZone.Right | SelectionZone.Bottom => Cursors.SizeNWSE,
            SelectionZone.Right | SelectionZone.Top or SelectionZone.Left | SelectionZone.Bottom => Cursors.SizeNESW,
            _ => Cursors.Cross,
        };
    }

    // ---------------- Bàn phím ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Escape) Close();
        else if (!_editing) return;
        else if (ctrl && e.Key == Key.Z) _board.Undo();
        else if ((ctrl && e.Key == Key.C) || e.Key == Key.Enter) CopyAndClose();
        else if (ctrl && e.Key == Key.S) { if (shift) SaveAs(); else Save(); }
        else if (!ctrl && e.Key is >= Key.D1 and <= Key.D7) SetTool(ToolOrder[e.Key - Key.D1]);
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // Mũi tên: dời vùng chọn 1 pixel; Shift: nới rộng/thu hẹp cạnh phải/dưới.
            double dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            if (shift) _sel = CaptureGeometry.Resize(_sel, SelectionZone.Right | SelectionZone.Bottom, Math.Max(_sel.X + 2, _sel.Right + dx), Math.Max(_sel.Y + 2, _sel.Bottom + dy), ShotBox);
            else _sel = CaptureGeometry.Move(_sel, dx, dy, ShotBox);
            Refresh();
        }
        else return;
        e.Handled = true;
    }

    // ---------------- Vẽ lại lớp giao diện ----------------

    private void Refresh()
    {
        if (_root.ActualWidth <= 0) return;
        _board.Scale = Sx; // nét vẽ và số thứ tự to theo mức phóng to của màn hình
        var full = new RectangleGeometry(new Rect(0, 0, _root.ActualWidth, _root.ActualHeight));
        var shown = _editing || _drag == Drag.Create ? _sel : _hover;
        _hint.Visibility = _editing || _drag == Drag.Create ? Visibility.Collapsed : Visibility.Visible;
        var bars = _editing && _drag is not (Drag.Create or Drag.Move or Drag.Resize) ? Visibility.Visible : Visibility.Collapsed;
        _sideBar.Visibility = _bottomBar.Visibility = bars;
        foreach (var h in _handles) h.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        if (shown.IsEmpty)
        {
            _dim.Data = full;
            _frame.Visibility = _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var d = ToDip(shown);
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(new Rect(d.X, d.Y, d.Width, d.Height)));
        _frame.Visibility = _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, d.X);
        Canvas.SetTop(_frame, d.Y);
        _frame.Width = d.Width;
        _frame.Height = d.Height;

        double[] xs = [d.X, d.X + d.Width / 2, d.Right], ys = [d.Y, d.Y + d.Height / 2, d.Bottom];
        var i = 0;
        foreach (var (hx, hy) in new[] { (0, 0), (1, 0), (2, 0), (0, 1), (2, 1), (0, 2), (1, 2), (2, 2) })
        {
            Canvas.SetLeft(_handles[i], xs[hx] - 3.5);
            Canvas.SetTop(_handles[i++], ys[hy] - 3.5);
        }

        _sizeText.Text = string.IsNullOrEmpty(_status)
            ? $"{(int)Math.Round(shown.Width)} × {(int)Math.Round(shown.Height)}"
            : _status;
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_sizeLabel, Math.Clamp(d.X, 0, Math.Max(0, _root.ActualWidth - _sizeLabel.DesiredSize.Width)));
        Canvas.SetTop(_sizeLabel, d.Y >= 26 ? d.Y - 26 : d.Y + 4);

        if (bars == Visibility.Visible)
        {
            var screen = ScreenDip;
            _sideBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var (sx, sy) = CaptureGeometry.PlaceSideBar(d, _sideBar.DesiredSize.Width, _sideBar.DesiredSize.Height, screen);
            Canvas.SetLeft(_sideBar, sx);
            Canvas.SetTop(_sideBar, sy);
            _bottomBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var (bx, by) = CaptureGeometry.PlaceBottomBar(d, _bottomBar.DesiredSize.Width, _bottomBar.DesiredSize.Height, screen);
            // Thanh dọc nằm trong mép phải thì thanh ngang lùi sang trái để không đè lên nhau.
            if (sx < d.Right && by < sy + _sideBar.DesiredSize.Height && by + _bottomBar.DesiredSize.Height > sy && bx + _bottomBar.DesiredSize.Width > sx)
                bx = Math.Max(0, sx - 6 - _bottomBar.DesiredSize.Width);
            Canvas.SetLeft(_bottomBar, bx);
            Canvas.SetTop(_bottomBar, by);
        }
    }

    // ---------------- Thanh công cụ ----------------

    private static readonly AnnotationTool[] ToolOrder =
        [AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Arrow, AnnotationTool.Pen, AnnotationTool.Highlight, AnnotationTool.Redact, AnnotationTool.Number];

    private Border BuildSideBar()
    {
        var panel = new StackPanel();
        (AnnotationTool Tool, string Glyph, string Key)[] tools =
        [
            (AnnotationTool.Rectangle, "", "Cap_ToolRect"),
            (AnnotationTool.Ellipse, "", "Cap_ToolEllipse"),
            (AnnotationTool.Arrow, "", "Cap_ToolArrow"),
            (AnnotationTool.Pen, "", "Cap_ToolPen"),
            (AnnotationTool.Highlight, "", "Cap_ToolHighlight"),
            (AnnotationTool.Redact, "", "Cap_ToolRedact"),
            (AnnotationTool.Number, "", "Cap_ToolNumber"),
        ];
        var n = 1;
        foreach (var (tool, glyph, key) in tools)
        {
            var chip = new Chip(Glyph(glyph), $"{Loc.Instance[key]} ({n++})", () => SetTool(_tool == tool ? null : tool));
            _toolChips[tool] = chip;
            panel.Children.Add(chip);
        }
        panel.Children.Add(Separator(horizontal: true));
        // Bảng màu 2 cột
        var colors = new UniformGrid { Columns = 2, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var hex in Palette)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(c), Stroke = Brushes.Gray, StrokeThickness = 1 };
            var chip = new Chip(dot, hex, () => SetColor(hex), size: 22) { Tag = c };
            _colorChips.Add(chip);
            colors.Children.Add(chip);
        }
        panel.Children.Add(colors);
        panel.Children.Add(Separator(horizontal: true));
        string[] thickKeys = ["Cap_Thin", "Cap_Medium", "Cap_Thick"];
        for (var level = 1; level <= 3; level++)
        {
            var l = level;
            var dot = new Ellipse { Width = 2 + level * 3, Height = 2 + level * 3, Fill = Brushes.White };
            var chip = new Chip(dot, Loc.Instance[thickKeys[level - 1]], () => SetThickness(l), size: 28);
            _thickChips.Add(chip);
            panel.Children.Add(chip);
        }
        SyncChips();
        return Bar(panel);
    }

    private Border BuildBottomBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Chip(Glyph(""), $"{Loc.Instance["Cap_Undo"]} (Ctrl+Z)", () => _board.Undo()));
        panel.Children.Add(Separator(horizontal: false));
        panel.Children.Add(new Chip(Glyph(""), $"{Loc.Instance["Cap_Save"]} (Ctrl+S)", Save));
        panel.Children.Add(new Chip(Glyph(""), $"{Loc.Instance["Cap_SaveAs"]} (Ctrl+Shift+S)", SaveAs));
        panel.Children.Add(new Chip(Glyph(""), $"{Loc.Instance["Cap_Copy"]} (Ctrl+C / Enter)", CopyAndClose));
        panel.Children.Add(Separator(horizontal: false));
        panel.Children.Add(new Chip(Glyph(""), $"{Loc.Instance["Cap_Close"]} (Esc)", Close));
        return Bar(panel);
    }

    private static Border Bar(UIElement child)
    {
        var b = new Border
        {
            Background = BarBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Child = child,
            Cursor = Cursors.Arrow,
            Visibility = Visibility.Collapsed,
        };
        // Bấm vào nền thanh không được tính là bấm lên màn hình (khoanh lại vùng chọn).
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => e.Handled = true;
        b.MouseRightButtonUp += (_, e) => e.Handled = true;
        return b;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = 16,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Rectangle Separator(bool horizontal) => horizontal
        ? new Rectangle { Height = 1, Margin = new Thickness(4, 4, 4, 4), Fill = HoverBrush }
        : new Rectangle { Width = 1, Margin = new Thickness(4, 6, 4, 6), Fill = HoverBrush };

    private void SetTool(AnnotationTool? tool)
    {
        _tool = tool;
        if (tool is { } t) _board.Tool = t;
        SyncChips();
        UpdateCursor(Mouse.GetPosition(_root));
    }

    private void SetColor(string hex)
    {
        _board.Color = (Color)ColorConverter.ConvertFromString(hex);
        AppHost.Current.Config.Tools.Capture.Color = hex;
        SyncChips();
    }

    private void SetThickness(int level)
    {
        _board.ThicknessLevel = level;
        AppHost.Current.Config.Tools.Capture.Thickness = level;
        SyncChips();
    }

    private void SyncChips()
    {
        foreach (var (tool, chip) in _toolChips) chip.Active = _tool == tool;
        foreach (var chip in _colorChips) chip.Active = chip.Tag is Color c && c == _board.Color;
        for (var i = 0; i < _thickChips.Count; i++) _thickChips[i].Active = _board.ThicknessLevel == i + 1;
    }

    // ---------------- Xuất ảnh ----------------

    private BitmapSource? Export() => _sel.IsEmpty ? null : _board.Export(SelectionPixels());

    private void Done(string message)
    {
        AppHost.Current.SaveConfig();
        ToastWindow.Show(message);
        Close();
    }

    private void Fail(string message)
    {
        _status = message;
        Refresh();
    }

    private void CopyAndClose()
    {
        if (Export() is not { } img) return;
        if (CaptureOutput.Copy(img)) Done(Loc.Instance["Cap_Copied"]);
        else Fail(Loc.Instance["Cap_CopyFailed"]);
    }

    private void Save() => SaveTo(System.IO.Path.Combine(CaptureOutput.Folder(), CaptureOutput.FileName()));

    private void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = CaptureOutput.FileName(),
            DefaultExt = ".png",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            InitialDirectory = CaptureOutput.Folder(),
        };
        if (dlg.ShowDialog(this) == true) SaveTo(dlg.FileName);
    }

    private void SaveTo(string path)
    {
        if (Export() is not { } img) return;
        try
        {
            var saved = CaptureOutput.Save(img, path);
            // Lưu xong vẫn sao chép theo tuỳ chọn, giống Lightshot: dán được ngay.
            var copied = AppHost.Current.Config.Tools.Capture.CopyToClipboard && CaptureOutput.Copy(img);
            Done(Loc.F("Cap_Saved", ("path", saved)) + (copied ? " " + Loc.Instance["Cap_Copied"] : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(Loc.F("Error_Generic", ("error", ex.Message)));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AppHost.Current.SaveConfig();
    }

    /// <summary>Nút vuông trên thanh công cụ (không dùng Button để không ăn theo theme sáng/tối của app: thanh luôn nền tối).</summary>
    private sealed class Chip : Border
    {
        private bool _active;

        public Chip(UIElement content, string tip, Action click, double size = 32)
        {
            Child = content;
            ToolTip = tip;
            Width = size;
            Height = size;
            Margin = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => { if (!_active) Background = HoverBrush; };
            MouseLeave += (_, _) => { if (!_active) Background = Brushes.Transparent; };
            MouseLeftButtonDown += (_, e) => e.Handled = true;
            MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }

        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                Background = value ? ActiveBrush : IsMouseOver ? HoverBrush : Brushes.Transparent;
            }
        }
    }
}

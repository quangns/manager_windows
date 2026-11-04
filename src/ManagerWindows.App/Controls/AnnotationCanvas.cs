using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManagerWindows.App.Controls;

public enum AnnotationTool { Rectangle, Ellipse, Arrow, Highlight, Redact, Number }

/// <summary>Một nét chú thích trên ảnh chụp; toạ độ là pixel của ảnh.</summary>
public abstract record Annotation(Color Color, double Thickness)
{
    public abstract void Draw(DrawingContext dc, double pixelsPerDip);

    protected Pen Stroke() => new(new SolidColorBrush(Color), Thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
}

public sealed record RectMark(Rect Box, Color Color, double Thickness) : Annotation(Color, Thickness)
{
    public override void Draw(DrawingContext dc, double ppd) => dc.DrawRoundedRectangle(null, Stroke(), Box, Thickness, Thickness);
}

public sealed record EllipseMark(Rect Box, Color Color, double Thickness) : Annotation(Color, Thickness)
{
    public override void Draw(DrawingContext dc, double ppd) =>
        dc.DrawEllipse(null, Stroke(), new Point(Box.X + Box.Width / 2, Box.Y + Box.Height / 2), Box.Width / 2, Box.Height / 2);
}

public sealed record ArrowMark(Point From, Point To, Color Color, double Thickness) : Annotation(Color, Thickness)
{
    public override void Draw(DrawingContext dc, double ppd)
    {
        var v = To - From;
        if (v.Length < 1) return;
        v.Normalize();
        var head = Math.Max(12, Thickness * 4);
        var n = new Vector(-v.Y, v.X);
        var b = To - v * head;
        var tip = new StreamGeometry();
        using (var g = tip.Open())
        {
            g.BeginFigure(To, true, true);
            g.LineTo(b + n * head * 0.5, true, false);
            g.LineTo(b - n * head * 0.5, true, false);
        }
        dc.DrawLine(Stroke(), From, b + v * 1);
        dc.DrawGeometry(new SolidColorBrush(Color), null, tip);
    }
}

/// <summary>Tô sáng kiểu bút dạ: màu trong suốt 40%, vẫn đọc được chữ bên dưới.</summary>
public sealed record HighlightMark(Rect Box, Color Color) : Annotation(Color, 0)
{
    public override void Draw(DrawingContext dc, double ppd) =>
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x66, Color.R, Color.G, Color.B)), null, Box);
}

/// <summary>Che kín (bôi đen thông tin nhạy cảm) bằng màu đặc.</summary>
public sealed record RedactMark(Rect Box, Color Color) : Annotation(Color, 0)
{
    public override void Draw(DrawingContext dc, double ppd) => dc.DrawRectangle(new SolidColorBrush(Color), null, Box);
}

/// <summary>Số thứ tự trong vòng tròn (đánh dấu các bước 1, 2, 3…).</summary>
public sealed record NumberMark(Point Center, int Number, Color Color, double Thickness) : Annotation(Color, Thickness)
{
    public double Radius => 9 + Thickness * 3;

    public override void Draw(DrawingContext dc, double ppd)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 2), Center, Radius, Radius);
        // Chữ đen trên màu sáng (vàng, trắng), chữ trắng trên màu đậm.
        var light = 0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B > 160;
        var text = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.15, light ? Brushes.Black : Brushes.White, ppd);
        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }
}

/// <summary>
/// Ảnh chụp + các nét chú thích. Kích thước = đúng số pixel của ảnh (1 DIP = 1 pixel), đặt trong Viewbox để thu nhỏ khi hiển thị;
/// xuất ảnh bằng cách vẽ lại vào DrawingVisual nên kết quả đúng độ phân giải gốc.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private readonly List<Annotation> _items = new();
    private Annotation? _preview;
    private Point _start;

    public BitmapSource? Source { get; private set; }
    public AnnotationTool Tool { get; set; } = AnnotationTool.Rectangle;
    public Color Color { get; set; } = Colors.Red;
    /// <summary>Độ dày nét 1–3 (mảnh/vừa/đậm), quy ra pixel theo kích thước ảnh.</summary>
    public int ThicknessLevel { get; set; } = 2;
    public bool HasAnnotations => _items.Count > 0;
    public event Action? Changed;

    public AnnotationCanvas()
    {
        Cursor = Cursors.Cross;
        Focusable = true;
    }

    public void SetSource(BitmapSource source)
    {
        Source = source;
        Width = source.PixelWidth;
        Height = source.PixelHeight;
        InvalidateVisual();
    }

    private double StrokeWidth => ThicknessLevel switch { 1 => 2, 3 => 6, _ => 4 };
    private int NextNumber => _items.OfType<NumberMark>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;

    public void Undo()
    {
        if (_items.Count == 0) return;
        _items.RemoveAt(_items.Count - 1);
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        _items.Clear();
        InvalidateVisual();
        Changed?.Invoke();
    }

    private void Render(DrawingContext dc, double ppd, bool withPreview)
    {
        if (Source == null) return;
        dc.DrawImage(Source, new Rect(0, 0, Source.PixelWidth, Source.PixelHeight));
        foreach (var a in _items) a.Draw(dc, ppd);
        if (withPreview) _preview?.Draw(dc, ppd);
    }

    protected override void OnRender(DrawingContext dc) => Render(dc, VisualTreeHelper.GetDpi(this).PixelsPerDip, true);

    /// <summary>Ảnh kết quả (ảnh gốc + chú thích) đúng độ phân giải gốc.</summary>
    public BitmapSource? Export()
    {
        if (Source == null) return null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Render(dc, 1.0, false);
        var bmp = new RenderTargetBitmap(Source.PixelWidth, Source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private Point Clamp(Point p) => Source == null ? p : new Point(Math.Clamp(p.X, 0, Source.PixelWidth), Math.Clamp(p.Y, 0, Source.PixelHeight));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _start = Clamp(e.GetPosition(this));
        if (Tool == AnnotationTool.Number)
        {
            Add(new NumberMark(_start, NextNumber, Color, ThicknessLevel));
            return;
        }
        CaptureMouse();
        _preview = Make(_start, _start);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_preview == null || !IsMouseCaptured) return;
        var p = Clamp(e.GetPosition(this));
        // Giữ Shift: hình vuông / tròn đều.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse)
        {
            var d = Math.Max(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
            p = new Point(_start.X + Math.Sign(p.X - _start.X) * d, _start.Y + Math.Sign(p.Y - _start.Y) * d);
        }
        _preview = Make(_start, p);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        var done = _preview;
        _preview = null;
        var p = Clamp(e.GetPosition(this));
        if (done != null && (p - _start).Length >= 3) Add(done);
        else InvalidateVisual();
    }

    private void Add(Annotation a)
    {
        _items.Add(a);
        InvalidateVisual();
        Changed?.Invoke();
    }

    private Annotation Make(Point a, Point b)
    {
        var box = new Rect(a, b);
        return Tool switch
        {
            AnnotationTool.Ellipse => new EllipseMark(box, Color, StrokeWidth),
            AnnotationTool.Arrow => new ArrowMark(a, b, Color, StrokeWidth),
            AnnotationTool.Highlight => new HighlightMark(box, Color),
            AnnotationTool.Redact => new RedactMark(box, Color),
            _ => new RectMark(box, Color, StrokeWidth),
        };
    }
}

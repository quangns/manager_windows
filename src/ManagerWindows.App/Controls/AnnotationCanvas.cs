using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManagerWindows.App.Controls;

public enum AnnotationTool { Rectangle, Ellipse, Arrow, Pen, Highlight, Redact, Number }

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

/// <summary>Nét bút vẽ tự do.</summary>
public sealed record PenMark(Point[] Points, Color Color, double Thickness) : Annotation(Color, Thickness)
{
    public override void Draw(DrawingContext dc, double ppd)
    {
        if (Points.Length < 2) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Points[0], false, false);
            c.PolyLineTo(Points.Skip(1).ToArray(), true, true);
        }
        dc.DrawGeometry(null, Stroke(), g);
    }
}

/// <summary>Số thứ tự trong vòng tròn (đánh dấu các bước 1, 2, 3…). <paramref name="Scale"/> = pixel mỗi DIP để số không bị nhỏ trên màn hình DPI cao.</summary>
public sealed record NumberMark(Point Center, int Number, Color Color, double Thickness, double Scale = 1) : Annotation(Color, Thickness)
{
    public double Radius => (9 + Thickness * 3) * Scale;

    public override void Draw(DrawingContext dc, double ppd)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, 2 * Scale), Center, Radius, Radius);
        // Chữ đen trên màu sáng (vàng, trắng), chữ trắng trên màu đậm.
        var light = 0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B > 160;
        var text = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            Radius * 1.15, light ? Brushes.Black : Brushes.White, ppd);
        dc.DrawText(text, new Point(Center.X - text.Width / 2, Center.Y - text.Height / 2));
    }
}

/// <summary>
/// Lớp chú thích phủ lên ảnh chụp toàn màn hình. Kích thước = đúng số pixel của ảnh (1 đơn vị = 1 pixel), đặt trong Viewbox
/// phủ kín cửa sổ chụp nên hiển thị đúng kích thước thật. Ảnh nền do cửa sổ chụp vẽ; lớp này chỉ vẽ nét.
/// Cửa sổ chụp tự xử lý chuột rồi gọi <see cref="BeginStroke"/> / <see cref="UpdateStroke"/> / <see cref="EndStroke"/> theo toạ độ pixel.
/// Xuất ảnh bằng cách vẽ lại vào DrawingVisual nên kết quả đúng độ phân giải gốc.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private readonly List<Annotation> _items = new();
    private readonly List<Point> _penPoints = new();
    private Annotation? _preview;
    private Point _start;

    public BitmapSource? Source { get; private set; }
    public AnnotationTool Tool { get; set; } = AnnotationTool.Rectangle;
    public Color Color { get; set; } = Colors.Red;
    /// <summary>Độ dày nét 1–3 (mảnh/vừa/đậm).</summary>
    public int ThicknessLevel { get; set; } = 2;
    /// <summary>Pixel mỗi DIP của màn hình: nét và số thứ tự to theo, nhìn như nhau ở mọi mức phóng to.</summary>
    public double Scale { get; set; } = 1;
    /// <summary>Nét vẽ bị giới hạn trong vùng này (vùng chọn, pixel ảnh).</summary>
    public Rect Bounds { get; set; } = Rect.Empty;
    public bool HasAnnotations => _items.Count > 0;
    public bool IsStroking => _preview != null;
    public event Action? Changed;

    public AnnotationCanvas()
    {
        IsHitTestVisible = false;
    }

    public void SetSource(BitmapSource source)
    {
        Source = source;
        Width = source.PixelWidth;
        Height = source.PixelHeight;
        InvalidateVisual();
    }

    private double StrokeWidth => (ThicknessLevel switch { 1 => 2, 3 => 6, _ => 4 }) * Scale;
    private int NextNumber => _items.OfType<NumberMark>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;

    public void Undo()
    {
        if (_items.Count == 0) return;
        _items.RemoveAt(_items.Count - 1);
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var a in _items) a.Draw(dc, ppd);
        _preview?.Draw(dc, ppd);
    }

    /// <summary>Ảnh kết quả (ảnh gốc + chú thích) của vùng <paramref name="crop"/>, đúng độ phân giải gốc.</summary>
    public BitmapSource? Export(Int32Rect crop)
    {
        if (Source == null || crop.Width <= 0 || crop.Height <= 0) return null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            dc.DrawImage(Source, new Rect(0, 0, Source.PixelWidth, Source.PixelHeight));
            foreach (var a in _items) a.Draw(dc, 1.0);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private Point Clamp(Point p)
    {
        var b = Bounds.IsEmpty ? new Rect(0, 0, Width, Height) : Bounds;
        return new Point(Math.Clamp(p.X, b.Left, b.Right), Math.Clamp(p.Y, b.Top, b.Bottom));
    }

    /// <summary>Bắt đầu một nét tại pixel <paramref name="p"/>. Trả false nếu nét đã xong ngay (đánh số: một cú bấm).</summary>
    public bool BeginStroke(Point p)
    {
        _start = Clamp(p);
        if (Tool == AnnotationTool.Number)
        {
            Add(new NumberMark(_start, NextNumber, Color, ThicknessLevel, Scale));
            return false;
        }
        _penPoints.Clear();
        _penPoints.Add(_start);
        _preview = Make(_start, _start);
        InvalidateVisual();
        return true;
    }

    /// <summary>Kéo nét tới <paramref name="p"/>; <paramref name="square"/> (giữ Shift) = hình vuông/tròn đều, mũi tên thẳng theo 45°.</summary>
    public void UpdateStroke(Point p, bool square)
    {
        if (_preview == null) return;
        p = Clamp(p);
        if (square && Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Highlight or AnnotationTool.Redact)
        {
            var d = Math.Max(Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
            p = Clamp(new Point(_start.X + Math.Sign(p.X - _start.X) * d, _start.Y + Math.Sign(p.Y - _start.Y) * d));
        }
        else if (square && Tool == AnnotationTool.Arrow)
        {
            var v = p - _start;
            var angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
            p = Clamp(_start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length);
        }
        if (Tool == AnnotationTool.Pen && (p - _penPoints[^1]).Length >= Scale) _penPoints.Add(p);
        _preview = Make(_start, p);
        InvalidateVisual();
    }

    public void EndStroke(Point p)
    {
        var done = _preview;
        _preview = null;
        var moved = Tool == AnnotationTool.Pen ? _penPoints.Count >= 2 : (Clamp(p) - _start).Length >= 3;
        if (done != null && moved) Add(done);
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
            AnnotationTool.Pen => new PenMark(_penPoints.ToArray(), Color, StrokeWidth),
            AnnotationTool.Highlight => new HighlightMark(box, Color),
            AnnotationTool.Redact => new RedactMark(box, Color),
            _ => new RectMark(box, Color, StrokeWidth),
        };
    }
}

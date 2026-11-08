namespace ManagerWindows.Core.Tools;

/// <summary>Hình chữ nhật số thực (pixel hoặc DIP) dùng cho vùng chọn khi chụp màn hình.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Hình chữ nhật từ hai góc bất kỳ (kéo ngược chiều vẫn ra hình dương).</summary>
    public static Box FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public bool Contains(double x, double y) => x >= X && y >= Y && x <= Right && y <= Bottom;

    public Box Intersect(Box o)
    {
        double l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : new Box(l, t, r - l, b - t);
    }
}

/// <summary>Vị trí con trỏ so với vùng chọn: trong vùng, trên cạnh/góc (kéo để đổi kích thước) hay ở ngoài.</summary>
[Flags]
public enum SelectionZone { Outside = 0, Inside = 1, Left = 2, Top = 4, Right = 8, Bottom = 16 }

/// <summary>
/// Hình học của vùng chọn kiểu Lightshot: nhận biết cạnh/góc dưới con trỏ, đổi kích thước, di chuyển,
/// và đặt thanh công cụ sát vùng chọn mà không tràn khỏi màn hình.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>Con trỏ ở đâu so với vùng chọn; <paramref name="grip"/> = bán kính bắt cạnh/góc.</summary>
    public static SelectionZone HitTest(Box sel, double x, double y, double grip = 6)
    {
        if (sel.IsEmpty) return SelectionZone.Outside;
        if (x < sel.X - grip || x > sel.Right + grip || y < sel.Y - grip || y > sel.Bottom + grip) return SelectionZone.Outside;
        var z = SelectionZone.Outside;
        if (Math.Abs(x - sel.X) <= grip) z |= SelectionZone.Left;
        else if (Math.Abs(x - sel.Right) <= grip) z |= SelectionZone.Right;
        if (Math.Abs(y - sel.Y) <= grip) z |= SelectionZone.Top;
        else if (Math.Abs(y - sel.Bottom) <= grip) z |= SelectionZone.Bottom;
        if (z != SelectionZone.Outside) return z;
        return sel.Contains(x, y) ? SelectionZone.Inside : SelectionZone.Outside;
    }

    /// <summary>Kéo cạnh/góc <paramref name="zone"/> của vùng gốc tới (x, y); kéo vượt cạnh đối diện thì vùng tự lật. Giới hạn trong <paramref name="bounds"/>.</summary>
    public static Box Resize(Box orig, SelectionZone zone, double x, double y, Box bounds)
    {
        x = Math.Clamp(x, bounds.X, bounds.Right);
        y = Math.Clamp(y, bounds.Y, bounds.Bottom);
        double l = orig.X, t = orig.Y, r = orig.Right, b = orig.Bottom;
        if (zone.HasFlag(SelectionZone.Left)) l = x;
        if (zone.HasFlag(SelectionZone.Right)) r = x;
        if (zone.HasFlag(SelectionZone.Top)) t = y;
        if (zone.HasFlag(SelectionZone.Bottom)) b = y;
        return Box.FromCorners(l, t, r, b);
    }

    /// <summary>Dời vùng chọn thêm (dx, dy), giữ nguyên kích thước và không ra ngoài <paramref name="bounds"/>.</summary>
    public static Box Move(Box orig, double dx, double dy, Box bounds) => orig with
    {
        X = Math.Clamp(orig.X + dx, bounds.X, Math.Max(bounds.X, bounds.Right - orig.Width)),
        Y = Math.Clamp(orig.Y + dy, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - orig.Height)),
    };

    /// <summary>
    /// Thanh công cụ dọc (công cụ vẽ): bên phải vùng chọn, hết chỗ thì bên trái, vẫn không đủ thì nằm trong mép phải.
    /// Trả góc trên trái của thanh.
    /// </summary>
    public static (double X, double Y) PlaceSideBar(Box sel, double w, double h, Box screen, double gap = 6)
    {
        double x = sel.Right + gap;
        if (x + w > screen.Right) x = sel.X - gap - w;
        if (x < screen.X) x = sel.Right - gap - w;
        double y = Math.Clamp(sel.Y, screen.Y, Math.Max(screen.Y, screen.Bottom - h));
        return (Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - w)), y);
    }

    /// <summary>
    /// Thanh thao tác ngang (sao chép, lưu…): dưới vùng chọn, canh theo mép phải; hết chỗ thì phía trên, vẫn không đủ thì nằm trong mép dưới.
    /// </summary>
    public static (double X, double Y) PlaceBottomBar(Box sel, double w, double h, Box screen, double gap = 6)
    {
        double y = sel.Bottom + gap;
        if (y + h > screen.Bottom) y = sel.Y - gap - h;
        if (y < screen.Y) y = sel.Bottom - gap - h;
        double x = sel.Right - w;
        return (Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - w)), Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - h)));
    }
}

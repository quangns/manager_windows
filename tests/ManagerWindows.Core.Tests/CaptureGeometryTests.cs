using ManagerWindows.Core.Tools;

namespace ManagerWindows.Core.Tests;

public class CaptureGeometryTests
{
    private static readonly Box Screen = new(0, 0, 1920, 1080);
    private static readonly Box Sel = new(100, 100, 400, 300);

    [Theory]
    [InlineData(300, 250, SelectionZone.Inside)]
    [InlineData(50, 50, SelectionZone.Outside)]
    [InlineData(101, 250, SelectionZone.Left)]
    [InlineData(498, 250, SelectionZone.Right)]
    [InlineData(300, 103, SelectionZone.Top)]
    [InlineData(300, 404, SelectionZone.Bottom)]
    [InlineData(97, 97, SelectionZone.Left | SelectionZone.Top)]
    [InlineData(502, 402, SelectionZone.Right | SelectionZone.Bottom)]
    [InlineData(300, 410, SelectionZone.Outside)]
    public void HitTest_finds_edges_and_corners(double x, double y, SelectionZone expected) =>
        Assert.Equal(expected, CaptureGeometry.HitTest(Sel, x, y));

    [Fact]
    public void Resize_moves_only_grabbed_edges_and_flips_past_opposite_edge()
    {
        Assert.Equal(new Box(100, 100, 500, 300), CaptureGeometry.Resize(Sel, SelectionZone.Right, 600, 999, Screen));
        Assert.Equal(new Box(50, 100, 450, 350), CaptureGeometry.Resize(Sel, SelectionZone.Left | SelectionZone.Bottom, 50, 450, Screen));
        // Kéo cạnh trái vượt qua cạnh phải: vùng lật, không ra kích thước âm.
        Assert.Equal(new Box(500, 100, 100, 300), CaptureGeometry.Resize(Sel, SelectionZone.Left, 600, 0, Screen));
        // Không kéo ra ngoài màn hình.
        Assert.Equal(new Box(0, 0, 500, 400), CaptureGeometry.Resize(Sel, SelectionZone.Left | SelectionZone.Top, -80, -80, Screen));
    }

    [Fact]
    public void Move_keeps_size_and_stays_on_screen()
    {
        Assert.Equal(new Box(150, 80, 400, 300), CaptureGeometry.Move(Sel, 50, -20, Screen));
        Assert.Equal(new Box(1520, 780, 400, 300), CaptureGeometry.Move(Sel, 5000, 5000, Screen));
        Assert.Equal(new Box(0, 0, 400, 300), CaptureGeometry.Move(Sel, -5000, -5000, Screen));
    }

    [Fact]
    public void Toolbars_go_beside_selection_then_fall_back_inside()
    {
        Assert.Equal((506.0, 100.0), CaptureGeometry.PlaceSideBar(Sel, 40, 300, Screen));
        Assert.Equal((260.0, 406.0), CaptureGeometry.PlaceBottomBar(Sel, 240, 40, Screen));
        // Vùng chọn sát mép phải/dưới: thanh dọc sang trái, thanh ngang lên trên.
        var edge = new Box(1500, 700, 420, 380);
        Assert.Equal((1454.0, 700.0), CaptureGeometry.PlaceSideBar(edge, 40, 300, Screen));
        Assert.Equal((1680.0, 654.0), CaptureGeometry.PlaceBottomBar(edge, 240, 40, Screen));
        // Chụp cả màn hình: cả hai thanh nằm bên trong.
        Assert.Equal((1874.0, 0.0), CaptureGeometry.PlaceSideBar(Screen, 40, 300, Screen));
        Assert.Equal((1680.0, 1034.0), CaptureGeometry.PlaceBottomBar(Screen, 240, 40, Screen));
    }

    [Fact]
    public void Box_from_corners_and_intersect()
    {
        Assert.Equal(new Box(10, 20, 30, 40), Box.FromCorners(40, 60, 10, 20));
        Assert.Equal(new Box(400, 300, 100, 100), Sel.Intersect(new Box(400, 300, 900, 900)));
        Assert.True(Sel.Intersect(new Box(900, 900, 10, 10)).IsEmpty);
    }
}

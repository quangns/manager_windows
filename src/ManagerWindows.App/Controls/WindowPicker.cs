using System.Windows;
using ComponentModelException = System.ComponentModel.Win32Exception;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Biểu tượng tâm ngắm "kéo để chọn app" (giống EVKey): giữ chuột trái vào biểu tượng, kéo tới cửa sổ app bất kỳ rồi thả.
/// Trong lúc kéo gọi <see cref="HoverCommand"/> với PID (hoặc handle cửa sổ nếu <see cref="PickWindow"/>) dưới con trỏ;
/// khi thả gọi <see cref="PickedCommand"/>. Cửa sổ đang trỏ vào được tô khung để thấy rõ sẽ chọn app nào.
/// PID là của app thật: cửa sổ "ma" của app treo/ngủ đông và khung app Store/UWP được quy về đúng tiến trình app.
/// Chuột được "bắt" (CaptureMouse) nên vẫn nhận sự kiện khi con trỏ ra ngoài cửa sổ app; ngoài ra một bộ đếm giờ đọc trực tiếp
/// trạng thái nút chuột, nên dù Windows huỷ việc bắt chuột giữa chừng (cửa sổ khác được kích hoạt…), thả chuột vẫn được tính.
/// </summary>
public sealed class WindowPicker : Border
{
    public static readonly DependencyProperty PickedCommandProperty =
        DependencyProperty.Register(nameof(PickedCommand), typeof(ICommand), typeof(WindowPicker));
    public static readonly DependencyProperty HoverCommandProperty =
        DependencyProperty.Register(nameof(HoverCommand), typeof(ICommand), typeof(WindowPicker));

    public ICommand? PickedCommand { get => (ICommand?)GetValue(PickedCommandProperty); set => SetValue(PickedCommandProperty, value); }
    public ICommand? HoverCommand { get => (ICommand?)GetValue(HoverCommandProperty); set => SetValue(HoverCommandProperty, value); }

    /// <summary>true: tham số lệnh là handle cửa sổ (long) thay vì PID — dùng cho "ghim cửa sổ".</summary>
    public bool PickWindow { get; set; }

    private PickerHighlight? _highlight;
    private readonly System.Windows.Threading.DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private long Target(IntPtr window) => PickWindow ? window.ToInt64() : window == IntPtr.Zero ? 0 : DesktopWindows.AppPidOf(window);

    /// <summary>Cửa sổ app dưới con trỏ (bỏ qua chính khung tô) và tô khung quanh nó.</summary>
    private IntPtr WindowUnderCursor()
    {
        var h = DesktopWindows.AtCursor(_highlight?.Handle ?? IntPtr.Zero);
        _highlight?.Show(h);
        return h;
    }

    private readonly System.Windows.Shapes.Path _icon;
    private bool _dragging;
    private long _last = -1;

    public WindowPicker()
    {
        Width = 48;
        Height = 48;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        Focusable = true; // nhận phím Esc để huỷ khi đang kéo
        FocusVisualStyle = null;
        SetResourceReference(BackgroundProperty, "Raised");
        SetResourceReference(BorderBrushProperty, "Hairline");
        _icon = new System.Windows.Shapes.Path
        {
            // Tâm ngắm: vòng tròn + 4 vạch
            Data = Geometry.Parse("M12,4 A8,8 0 1 1 11.99,4 Z M12,0 L12,7 M12,17 L12,24 M0,12 L7,12 M17,12 L24,12"),
            StrokeThickness = 2,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _icon.SetResourceReference(Shape.StrokeProperty, "Accent");
        Child = _icon;
        ToolTip = Services.Loc.Instance["Hib_PickerTip"];
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>Mỗi 30 ms khi đang kéo: cập nhật cửa sổ dưới con trỏ; nút chuột đã nhả thì coi là thả.</summary>
    private void Poll()
    {
        if (!_dragging) { _poll.Stop(); return; }
        try
        {
            if (DesktopWindows.PrimaryButtonDown()) Hover();
            else FinishDrag();
        }
        catch (Exception ex)
        {
            // Lỗi đọc một cửa sổ/tiến trình (vừa đóng…) không được làm kẹt thao tác kéo.
            Services.Log.Error("picker", ex);
        }
    }

    private void Hover()
    {
        long target;
        try { target = Target(WindowUnderCursor()); }
        catch (Exception ex) when (ex is InvalidOperationException or ComponentModelException) { return; }
        if (target == _last) return;
        _last = target;
        Fire(HoverCommand, target);
    }

    private void FinishDrag()
    {
        if (!_dragging) return; // đã xử lý (sự kiện nhả chuột và bộ đếm giờ có thể cùng tới)
        long target;
        try { target = Target(WindowUnderCursor()); }
        catch (Exception ex) when (ex is InvalidOperationException or ComponentModelException) { target = _last > 0 ? _last : 0; }
        if (target <= 0 && _last > 0) target = _last; // con trỏ vừa rời cửa sổ đúng lúc nhả: lấy app trỏ gần nhất
        End();
        Fire(PickedCommand, target);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        // Không phụ thuộc kết quả bắt chuột: dù Windows huỷ bắt chuột giữa chừng, bộ đếm giờ vẫn theo dõi nút chuột. Esc để huỷ.
        _dragging = true;
        _poll.Start();
        _last = -1;
        _highlight ??= new PickerHighlight();
        Mouse.OverrideCursor = Cursors.Cross;
        _icon.Visibility = Visibility.Hidden; // "nhấc" tâm ngắm lên theo con trỏ như EVKey
        SetResourceReference(BorderBrushProperty, "Accent");
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Hover();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        FinishDrag();
        e.Handled = true;
    }

    /// <summary>Tham số gửi cho lệnh: handle cửa sổ dạng long (ghim cửa sổ), PID dạng int (ngủ đông).</summary>
    /// <remarks>
    /// Phải ép kiểu từng nhánh về object: viết <c>PickWindow ? target : (int)target</c> thì C# đưa cả biểu thức về long,
    /// lệnh nhận int (RelayCommand&lt;int&gt;) ném ArgumentException và thao tác chọn app ở tab Ngủ đông không chạy.
    /// </remarks>
    internal static object CommandArgument(bool pickWindow, long target) => pickWindow ? (object)target : (object)(int)target;

    private void Fire(ICommand? cmd, long target)
    {
        var arg = CommandArgument(PickWindow, target);
        try
        {
            if (cmd?.CanExecute(arg) == true) cmd.Execute(arg);
        }
        catch (ArgumentException ex)
        {
            // Sai kiểu tham số không được âm thầm làm hỏng thao tác: ghi log để thấy ngay.
            Services.Log.Error("picker", ex);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_dragging && e.Key == Key.Escape)
        {
            End(); // huỷ, không chọn gì
            e.Handled = true;
        }
    }

    private void End()
    {
        _dragging = false;
        _poll.Stop();
        Mouse.OverrideCursor = null;
        _icon.Visibility = Visibility.Visible;
        _highlight?.Close();
        _highlight = null;
        SetResourceReference(BorderBrushProperty, "Hairline");
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}

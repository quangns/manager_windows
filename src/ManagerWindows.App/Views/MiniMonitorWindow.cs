using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Tools;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Views;

/// <summary>
/// Mini monitor luôn nằm trên cùng, chỉ hiện CPU / GPU / RAM / ổ đĩa. Nền trong suốt hoặc màu tuỳ chọn;
/// màu chữ chọn theo <see cref="OverlayPalette"/> để đọc được ở cả chế độ sáng lẫn tối.
/// Kéo để di chuyển (khi chưa khoá); "click xuyên" thì chuột đi qua, không che app bên dưới. Chuột phải: menu tuỳ chọn.
/// </summary>
public sealed class MiniMonitorWindow : Window
{
    private readonly OverlayOptions _opt;
    private readonly StackPanel _panel = new();
    private readonly Border _root;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CpuMeter _cpu = new();
    private readonly GpuMeter _gpu = new();
    private readonly DiskMeter _disk = new();
    private readonly Dictionary<string, (TextBlock Label, TextBlock Value)> _cells = new();
    private int _tick;
    private OverlayColors _colors = null!;

    public event Action? OptionsChanged;

    public MiniMonitorWindow(OverlayOptions opt)
    {
        _opt = opt;
        Title = "Manager Windows – mini monitor";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _root = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6), Child = _panel };
        ThemeManager.Changed += OnThemeChanged;
        Content = _root;
        Build();

        SourceInitialized += (_, _) => ApplyStyle();
        Loaded += (_, _) => PlaceInitially();
        _timer.Tick += (_, _) => Update();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        ContextMenu = BuildMenu();
    }

    /// <summary>Dựng lại các ô theo tuỳ chọn (bật/tắt chỉ số, ngang/dọc, cỡ chữ, nền, màu chữ).</summary>
    public void Build()
    {
        _colors = OverlayPalette.Resolve(_opt, ThemeManager.IsDark);
        _panel.Children.Clear();
        _cells.Clear();
        _panel.Orientation = _opt.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
        var size = _opt.Size switch { "small" => 11.0, "large" => 16.0, _ => 13.0 };
        var mono = TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");
        // Nền trong suốt: viền chữ (đen cho chữ sáng, trắng cho chữ tối) để đọc được trên mọi thứ phía sau.
        // Nền màu: không cần viền, chữ đã được chọn tương phản với nền.
        var outline = _opt.Background == "solid" ? null
            : new DropShadowEffect { Color = ToColor(_colors.Outline), BlurRadius = 4, ShadowDepth = 0, Opacity = 1 };
        foreach (var (key, on) in new[] { ("CPU", _opt.Cpu), ("GPU", _opt.Gpu), ("RAM", _opt.Ram), ("Disk", _opt.Disk) })
        {
            if (!on) continue;
            var label = new TextBlock { Text = key, FontSize = size * 0.85, FontWeight = FontWeights.SemiBold, Foreground = Brush(_colors.Label), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = "…", FontSize = size, FontWeight = FontWeights.Bold, FontFamily = mono, Foreground = Brush(_colors.Value), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = _opt.Horizontal ? new Thickness(0, 0, 12, 0) : new Thickness(0, 1, 0, 1),
                Effect = outline,
                Children = { label, value },
            };
            _panel.Children.Add(cell);
            _cells[key] = (label, value);
        }
        if (_panel.Children.Count == 0)
            _panel.Children.Add(new TextBlock { Text = "Manager Windows", Foreground = Brush(_colors.Value), FontSize = size, Effect = outline });
        // Ô cuối hàng ngang không cần lề phải: nền màu cân hai bên.
        if (_opt.Horizontal && _panel.Children.Count > 0 && _panel.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        ApplyStyle();
        Update();
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static SolidColorBrush Brush(uint argb)
    {
        var b = new SolidColorBrush(ToColor(argb));
        b.Freeze();
        return b;
    }

    private void OnThemeChanged()
    {
        // Chỉ "tự động" mới phụ thuộc chế độ sáng/tối của app.
        if (_opt.TextColor == "auto") Dispatcher.BeginInvoke(Build);
    }

    private void ApplyStyle()
    {
        _root.Background = Brush(_colors.Background);
        Cursor = _opt.ClickThrough ? null : Cursors.SizeAll;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) DesktopWindows.SetOverlayStyle(hwnd, _opt.ClickThrough);
    }

    private void PlaceInitially()
    {
        var area = SystemParameters.WorkArea;
        // Vị trí lưu theo DIP của màn hình chính; ra ngoài màn hình (rút màn phụ) thì về góc trên phải.
        var left = _opt.Left ?? area.Right - ActualWidth - 16;
        var top = _opt.Top ?? area.Top + 8;
        if (left < SystemParameters.VirtualScreenLeft || left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 20 ||
            top < SystemParameters.VirtualScreenTop || top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20)
            (left, top) = (area.Right - ActualWidth - 16, area.Top + 8);
        Left = left;
        Top = top;
    }

    private void Update()
    {
        if (!IsVisible) return;
        if (_cells.TryGetValue("CPU", out var cpu)) SetValue(cpu.Value, _cpu.Sample());
        else _cpu.Sample();
        if (_cells.TryGetValue("GPU", out var gpu))
        {
            var g = _gpu.Sample();
            if (g is { } v) SetValue(gpu.Value, v); else gpu.Value.Text = "–";
        }
        if (_cells.TryGetValue("RAM", out var ram))
        {
            var (free, total, avail) = SystemInfo.Memory();
            SetValue(ram.Value, 100 - free, total > 0 ? $" {(total - avail) / 1073741824.0:0.0}G" : "");
        }
        if (_cells.TryGetValue("Disk", out var disk)) SetValue(disk.Value, _disk.Sample().BusyPercent);
        // App toàn màn hình hoặc cửa sổ khác cũng ghim có thể đè lên: thỉnh thoảng đẩy lại lên trên cùng.
        if (++_tick % 5 == 0) DesktopWindows.BringToTop(new WindowInteropHelper(this).Handle);
    }

    private void SetValue(TextBlock t, double percent, string extra = "")
    {
        t.Text = $"{percent,3:0}%{extra}";
        // Màu thường < 60%, vàng/cam < 85%, đỏ khi gần đầy (bộ màu sáng hoặc tối tuỳ nền).
        t.Foreground = Brush(percent >= 85 ? _colors.Critical : percent >= 60 ? _colors.Warning : _colors.Value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_opt.ClickThrough) return;
        DragMove();
        _opt.Left = Left;
        _opt.Top = Top;
        OptionsChanged?.Invoke();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Add(string key, Action action, bool? check = null)
        {
            var mi = new MenuItem { Header = (check == true ? "✓ " : check == false ? "    " : "") + Loc.Instance[key] };
            mi.Click += (_, _) => { action(); ContextMenu = BuildMenu(); };
            menu.Items.Add(mi);
        }
        Add("Mini_Horizontal", () => { _opt.Horizontal = !_opt.Horizontal; Changed(); }, _opt.Horizontal);
        Add("Mini_SizeSmall", () => { _opt.Size = "small"; Changed(); }, _opt.Size == "small");
        Add("Mini_SizeMedium", () => { _opt.Size = "medium"; Changed(); }, _opt.Size == "medium");
        Add("Mini_SizeLarge", () => { _opt.Size = "large"; Changed(); }, _opt.Size == "large");
        menu.Items.Add(new Separator());
        Add("Mini_BgTransparent", () => { _opt.Background = "transparent"; Changed(); }, _opt.Background != "solid");
        Add("Mini_BgSolid", () => { _opt.Background = "solid"; Changed(); }, _opt.Background == "solid");
        menu.Items.Add(new Separator());
        Add("Mini_TextAuto", () => { _opt.TextColor = "auto"; Changed(); }, _opt.TextColor is not ("light" or "dark"));
        Add("Mini_TextLight", () => { _opt.TextColor = "light"; Changed(); }, _opt.TextColor == "light");
        Add("Mini_TextDark", () => { _opt.TextColor = "dark"; Changed(); }, _opt.TextColor == "dark");
        menu.Items.Add(new Separator());
        Add("Mini_ClickThrough", () => { _opt.ClickThrough = true; Changed(); });
        Add("Mini_Hide", () => ToolsService.Current?.SetOverlay(false));
        return menu;
    }

    private void Changed()
    {
        Build();
        OptionsChanged?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.Changed -= OnThemeChanged;
        _timer.Stop();
        _gpu.Dispose();
        _disk.Dispose();
        base.OnClosed(e);
    }
}

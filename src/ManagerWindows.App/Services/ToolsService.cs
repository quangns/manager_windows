using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ManagerWindows.App.Views;
using ManagerWindows.Core.Tools;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

public enum CaptureMode { Region, Window, FullScreen }

/// <summary>
/// Tab "Công cụ": phím tắt toàn cục → chụp màn hình (vùng / cửa sổ / toàn màn hình), ghim cửa sổ luôn trên cùng,
/// bật/tắt mini monitor. Tạo một lần trên luồng UI lúc khởi động.
/// </summary>
public sealed class ToolsService : IDisposable
{
    public static ToolsService? Current { get; private set; }

    private readonly AppHost _host;
    private MiniMonitorWindow? _overlay;
    private bool _capturing;

    public HotkeyService Hotkeys { get; } = new();
    public event Action? StateChanged;

    public ToolsService(AppHost host)
    {
        _host = host;
        Current = this;
        Hotkeys.Registered += () => StateChanged?.Invoke();
        ApplyHotkeys();
        if (host.Config.Tools.Overlay.Visible) SetOverlay(true);
    }

    /// <summary>Đăng ký lại phím tắt theo cấu hình (gọi sau khi lưu).</summary>
    public void ApplyHotkeys()
    {
        var t = _host.Config.Tools;
        var h = t.Hotkeys;
        Hotkeys.Apply(!t.HotkeysEnabled ? [] :
        [
            (nameof(h.RegionCapture), h.RegionCapture, () => Capture(CaptureMode.Region)),
            (nameof(h.WindowCapture), h.WindowCapture, () => Capture(CaptureMode.Window)),
            (nameof(h.FullCapture), h.FullCapture, () => Capture(CaptureMode.FullScreen)),
            (nameof(h.TogglePin), h.TogglePin, ToggleForegroundPin),
            (nameof(h.ToggleOverlay), h.ToggleOverlay, () => SetOverlay(!OverlayVisible)),
        ]);
    }

    // ===================== Chụp màn hình =====================

    /// <summary>
    /// Chụp: Window = cửa sổ đang dùng (lấy ngay lúc bấm phím tắt), Region = khoanh vùng/bấm chọn cửa sổ trên ảnh đóng băng,
    /// FullScreen = mọi màn hình. <paramref name="hideFirst"/>: bấm từ cửa sổ app thì ẩn nó trước để không lọt vào ảnh.
    /// Bật "chỉnh sửa sau khi chụp": mở màn hình đóng băng kiểu Lightshot (chọn sẵn cửa sổ / cả màn hình), vẽ và lưu ngay trên đó.
    /// </summary>
    public async void Capture(CaptureMode mode, Window? hideFirst = null, int delayMs = 0)
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            // Lấy cửa sổ đang dùng trước khi app làm gì khác (bấm từ app thì cửa sổ đang dùng là chính app → chuyển sang khoanh vùng).
            var target = mode == CaptureMode.Window ? DesktopWindows.Foreground() : IntPtr.Zero;
            if (mode == CaptureMode.Window && (target == IntPtr.Zero || WindowInfo.PidOf(target) == Environment.ProcessId))
                mode = CaptureMode.Region;
            if (hideFirst is { IsVisible: true })
            {
                hideFirst.WindowState = WindowState.Minimized;
                delayMs = Math.Max(delayMs, 350); // chờ hiệu ứng thu nhỏ xong
            }
            if (delayMs > 0) await Task.Delay(delayMs);
            var screen = ScreenCapture.VirtualScreen();
            var edit = _host.Config.Tools.Capture.OpenEditor;
            var windows = mode == CaptureMode.Region || edit ? DesktopWindows.List() : [];
            var shot = Grab(screen);
            if (shot == null) return;

            if (edit)
            {
                var win = mode == CaptureMode.Window ? DesktopWindows.Bounds(target) : default;
                Box? initial = mode switch
                {
                    CaptureMode.FullScreen => new Box(0, 0, shot.PixelWidth, shot.PixelHeight),
                    CaptureMode.Window => new Box(win.X - screen.X, win.Y - screen.Y, win.Width, win.Height),
                    _ => null,
                };
                new CaptureOverlayWindow(shot, screen, windows, initial, quick: false).ShowDialog();
                return;
            }
            BitmapSource? result = mode switch
            {
                CaptureMode.FullScreen => shot,
                CaptureMode.Window => Crop(shot, screen, DesktopWindows.Bounds(target)),
                _ => SelectRegion(shot, screen, windows),
            };
            if (result != null) Deliver(result);
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            ToastWindow.Show(Loc.F("Error_Generic", ("error", ex.Message)));
        }
        finally { _capturing = false; }
    }

    private static BitmapSource? Grab(PixelRect r)
    {
        if (r.IsEmpty) return null;
        var h = ScreenCapture.Capture(r);
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally { ScreenCapture.Free(h); }
    }

    private static BitmapSource? Crop(BitmapSource shot, PixelRect screen, PixelRect r)
    {
        int x = Math.Max(r.X - screen.X, 0), y = Math.Max(r.Y - screen.Y, 0);
        int right = Math.Min(r.X - screen.X + r.Width, shot.PixelWidth), bottom = Math.Min(r.Y - screen.Y + r.Height, shot.PixelHeight);
        if (right - x < 2 || bottom - y < 2) return null;
        var c = new CroppedBitmap(shot, new Int32Rect(x, y, right - x, bottom - y));
        c.Freeze();
        return c;
    }

    private static BitmapSource? SelectRegion(BitmapSource shot, PixelRect screen, List<DesktopWindow> windows)
    {
        var w = new CaptureOverlayWindow(shot, screen, windows, null, quick: true);
        w.ShowDialog();
        if (w.Result is not { } r) return null;
        var c = new CroppedBitmap(shot, r);
        c.Freeze();
        return c;
    }

    private void Deliver(BitmapSource img)
    {
        var opt = _host.Config.Tools.Capture;
        var parts = new List<string>();
        if (opt.CopyToClipboard && CaptureOutput.Copy(img)) parts.Add(Loc.Instance["Cap_Copied"]);
        if (opt.AutoSave || !opt.CopyToClipboard)
        {
            try { parts.Add(Loc.F("Cap_Saved", ("path", CaptureOutput.Save(img, Path.Combine(CaptureOutput.Folder(), CaptureOutput.FileName()))))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { parts.Add(Loc.F("Error_Generic", ("error", ex.Message))); }
        }
        ToastWindow.Show(string.Join(" ", parts));
    }

    // ===================== Ghim cửa sổ =====================

    /// <summary>Phím tắt: ghim/bỏ ghim cửa sổ đang dùng.</summary>
    public void ToggleForegroundPin()
    {
        var h = DesktopWindows.Foreground();
        if (h == IntPtr.Zero) return;
        TogglePin(h);
    }

    /// <summary>Đảo trạng thái "luôn trên cùng" của cửa sổ, báo bằng thông báo nhỏ. Trả trạng thái mới (null = Windows từ chối).</summary>
    public bool? TogglePin(IntPtr h)
    {
        var info = DesktopWindows.Describe(h);
        var want = !info.IsTopMost;
        var title = info.Title.Length > 60 ? info.Title[..57] + "…" : info.Title;
        if (!DesktopWindows.SetTopMost(h, want))
        {
            ToastWindow.Show(Loc.F("Pin_Failed", ("title", title)));
            return null;
        }
        ToastWindow.Show(Loc.F(want ? "Pin_Pinned" : "Pin_Unpinned", ("title", title)));
        StateChanged?.Invoke();
        return want;
    }

    // ===================== Mini monitor =====================

    public bool OverlayVisible => _overlay?.IsVisible == true;

    public void SetOverlay(bool visible)
    {
        var opt = _host.Config.Tools.Overlay;
        if (visible)
        {
            if (_overlay == null)
            {
                _overlay = new MiniMonitorWindow(opt);
                _overlay.OptionsChanged += () => { _host.SaveConfig(); StateChanged?.Invoke(); };
                _overlay.Closed += (_, _) => _overlay = null;
            }
            _overlay.Show();
        }
        else _overlay?.Close();
        if (opt.Visible != visible)
        {
            opt.Visible = visible;
            _host.SaveConfig();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Tuỳ chọn mini monitor vừa đổi trong tab Công cụ: vẽ lại.</summary>
    public void RefreshOverlay() => _overlay?.Build();

    public void Dispose()
    {
        Hotkeys.Dispose();
        _overlay?.Close();
    }
}

/// <summary>Lưu/sao chép ảnh chụp.</summary>
public static class CaptureOutput
{
    public static string Folder()
    {
        var f = AppHost.Current.Config.Tools.Capture.SaveFolder;
        return string.IsNullOrWhiteSpace(f)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Manager Windows")
            : Environment.ExpandEnvironmentVariables(f);
    }

    public static string FileName() => $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png";

    /// <summary>Lưu PNG (hoặc JPEG nếu đuôi .jpg), trả đường dẫn đã lưu (thêm (2), (3)… nếu trùng tên).</summary>
    public static string Save(BitmapSource img, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var unique = path;
        for (var i = 2; File.Exists(unique); i++)
            unique = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
        BitmapEncoder enc = Path.GetExtension(unique).ToLowerInvariant() is ".jpg" or ".jpeg" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(unique);
        enc.Save(fs);
        return unique;
    }

    /// <summary>Đưa ảnh vào clipboard; thử lại vài lần vì app khác có thể đang giữ clipboard.</summary>
    public static bool Copy(BitmapSource img)
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetImage(img); return true; }
            catch (COMException) { Thread.Sleep(60); }
        }
        return false;
    }
}

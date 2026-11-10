using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ManagerWindows.Core.Tools;

namespace ManagerWindows.Platform;

/// <summary>Hình chữ nhật theo pixel màn hình thật (app khai báo Per-Monitor DPI v2 nên không bị Windows quy đổi).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Chụp màn hình bằng GDI (BitBlt + CAPTUREBLT để lấy cả cửa sổ trong suốt/layered).</summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>Toàn bộ màn hình ảo (mọi màn hình gộp lại), toạ độ có thể âm nếu màn phụ nằm bên trái.</summary>
    public static PixelRect VirtualScreen() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    /// <summary>Chụp một vùng màn hình; trả HBITMAP (người gọi phải <see cref="Free"/>).</summary>
    public static IntPtr Capture(PixelRect r)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return bmp;
    }

    public static void Free(IntPtr hbitmap)
    {
        if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
    }
}

/// <summary>Một cửa sổ ứng dụng trên màn hình.</summary>
public sealed record DesktopWindow(IntPtr Handle, string Title, int Pid, PixelRect Bounds, bool IsTopMost, bool IsMinimized);

/// <summary>Liệt kê cửa sổ, lấy khung cửa sổ (không gồm bóng đổ), ghim/bỏ ghim "luôn trên cùng".</summary>
[SupportedOSPlatform("windows")]
public static class DesktopWindows
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    /// <summary>Cửa sổ của Windows không nên ghim/chụp như một app (desktop, taskbar).</summary>
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Cửa sổ ứng dụng đang hiện, theo thứ tự trên → dưới (z-order).</summary>
    public static List<DesktopWindow> List(bool includeMinimized = false)
    {
        var list = new List<DesktopWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h)) return true;
            var min = IsIconic(h);
            if (min && !includeMinimized) return true;
            list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static DesktopWindow Describe(IntPtr h)
    {
        var sb = new StringBuilder(GetWindowTextLength(h) + 1);
        GetWindowText(h, sb, sb.Capacity);
        return new DesktopWindow(h, sb.ToString(), WindowInfo.PidOf(h), Bounds(h), IsTopMost(h), IsIconic(h));
    }

    private static bool IsAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return false;
        if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        // Cửa sổ app UWP đã đóng/ẩn vẫn "visible" nhưng bị DWM che (cloaked).
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !IsShell(h);
    }

    private static bool IsShell(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return ShellClasses.Contains(sb.ToString());
    }

    /// <summary>Khung cửa sổ đúng như nhìn thấy (không gồm vùng bóng đổ trong suốt của Windows 10/11).</summary>
    public static PixelRect Bounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r))
            return default;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static bool IsTopMost(IntPtr h) => (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>Ghim/bỏ ghim luôn trên cùng. Trả false nếu Windows từ chối (vd. app chạy quyền Administrator).</summary>
    public static bool SetTopMost(IntPtr h, bool topMost)
    {
        SetWindowPos(h, topMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return IsTopMost(h) == topMost;
    }

    /// <summary>Cửa sổ app đang dùng (bỏ qua desktop/taskbar).</summary>
    public static IntPtr Foreground()
    {
        var h = GetAncestor(GetForegroundWindow(), 2 /* GA_ROOT */);
        return h != IntPtr.Zero && !IsShell(h) ? h : IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    private const uint LWA_ALPHA = 0x2;

    /// <summary>
    /// Cửa sổ app trên cùng (theo z-order) dưới con trỏ chuột — dùng cho tâm ngắm "kéo thả để chọn app".
    /// Không dùng WindowFromPoint vì nó hay trả về lớp phủ trong suốt (overlay của game/ghi hình, khung tô của chính tâm ngắm),
    /// cửa sổ con của app khác hoặc cửa sổ đã đóng nhưng còn "ẩn hình" (cloaked). <paramref name="ignore"/>: bỏ qua cửa sổ này.
    /// </summary>
    public static IntPtr AtCursor(IntPtr ignore = default)
    {
        if (!GetCursorPos(out var p)) return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (h == ignore || !IsWindowVisible(h) || IsIconic(h)) return true;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TRANSPARENT) != 0) return true; // click xuyên: chuột không "chạm" được cửa sổ này
            if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(h, out uint _key, out var alpha, out var flags)
                && (flags & LWA_ALPHA) != 0 && alpha == 0) return true; // trong suốt hoàn toàn
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if ((ex & WS_EX_TOOLWINDOW) != 0 && GetWindowTextLength(h) == 0) return true; // lớp phủ không tên (thanh công cụ ẩn, bóng đổ…)
            var b = Bounds(h);
            if (b.IsEmpty || !b.Contains(p.X, p.Y)) return true;
            found = IsShell(h) ? IntPtr.Zero : h; // trỏ vào desktop/taskbar: không chọn gì
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Tiến trình thật của app sở hữu cửa sổ: cửa sổ "ma" của app treo/ngủ đông (thuộc dwm) → app thật;
    /// khung cửa sổ của app Store/UWP (thuộc ApplicationFrameHost) → tiến trình app bên trong.
    /// </summary>
    public static int AppPidOf(IntPtr h)
    {
        var pid = WindowInfo.OwnerPid(h);
        if (pid <= 0) return pid;
        string name;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            name = proc.ProcessName;
        }
        catch (ArgumentException) { return pid; }
        if (!name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return pid;
        var inner = 0;
        EnumChildWindows(h, (c, _) =>
        {
            var cp = WindowInfo.PidOf(c);
            if (cp > 0 && cp != pid) { inner = cp; return false; }
            return true;
        }, IntPtr.Zero);
        return inner > 0 ? inner : pid;
    }

    public static void Activate(IntPtr h) => SetForegroundWindow(h);

    /// <summary>Đặt cửa sổ của app (overlay chụp màn hình) phủ đúng vùng pixel và nằm trên cùng.</summary>
    public static void PlaceTopMost(IntPtr h, PixelRect r) =>
        SetWindowPos(h, HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, SWP_SHOWWINDOW);

    /// <summary>Overlay theo dõi: không hiện trong Alt+Tab, không lấy focus; bật "click xuyên" thì chuột đi qua cửa sổ.</summary>
    public static void SetOverlayStyle(IntPtr h, bool clickThrough)
    {
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Đẩy lại lên trên cùng (app toàn màn hình hoặc app khác cũng ghim có thể che mất).</summary>
    public static void BringToTop(IntPtr h) =>
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

/// <summary>% GPU toàn máy qua PDH "\GPU Engine(*)\Utilization Percentage" (Windows 10 1709+). Không có bộ đếm thì trả null.</summary>
[SupportedOSPlatform("windows")]
public sealed class GpuMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double DoubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr Name; public PDH_FMT_COUNTERVALUE Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    private readonly IntPtr _query, _counter;
    private readonly bool _ok;

    public GpuMeter()
    {
        try
        {
            _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                  && PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) == 0;
            if (_ok) PdhCollectQueryData(_query);
        }
        catch (DllNotFoundException) { _ok = false; }
    }

    public double? Sample()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) return null;
        var items = new List<(string, double)>();
        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
            return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0) return 0;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus is 0 or 1)
                    items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return GpuUsage.Aggregate(items);
    }

    public void Dispose()
    {
        if (_ok) PdhCloseQuery(_query);
    }
}

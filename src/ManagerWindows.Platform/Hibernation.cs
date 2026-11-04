using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ManagerWindows.Platform;

/// <summary>Tạm dừng / chạy tiếp toàn bộ luồng của một tiến trình (giống cách Windows tạm dừng app Store).</summary>
[SupportedOSPlatform("windows")]
public static class ProcessSuspender
{
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(SafeProcessHandle process);

    public static bool Suspend(int pid) => Call(pid, NtSuspendProcess);

    public static bool Resume(int pid) => Call(pid, NtResumeProcess);

    private static bool Call(int pid, Func<SafeProcessHandle, int> op)
    {
        using var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        return !h.IsInvalid && op(h) == 0;
    }
}

/// <summary>Thông tin cửa sổ phục vụ ngủ đông: cửa sổ đang thu nhỏ, tiến trình giữ clipboard, cửa sổ trước.</summary>
[SupportedOSPlatform("windows")]
public static class WindowInfo
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>PID → mọi cửa sổ chính của tiến trình đó đều đang thu nhỏ hay không.</summary>
    public static Dictionary<int, bool> AllMinimizedByPid()
    {
        var map = new Dictionary<int, bool>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out var pid);
                var min = IsIconic(h);
                map[(int)pid] = map.TryGetValue((int)pid, out var prev) ? prev && min : min;
            }
            return true;
        }, IntPtr.Zero);
        return map;
    }

    public static int PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Tiến trình đang giữ clipboard: tạm dừng nó thì app khác dán (Ctrl+V) sẽ bị treo.</summary>
    public static HashSet<int> ClipboardPids()
    {
        var set = new HashSet<int>();
        foreach (var h in new[] { GetClipboardOwner(), GetOpenClipboardWindow() })
            if (PidOf(h) is > 0 and var pid) set.Add(pid);
        return set;
    }

    public static int ForegroundPid() => OwnerPid(GetForegroundWindow());

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr HungWindowFromGhostWindow(IntPtr ghost);

    /// <summary>
    /// PID thật của app sở hữu cửa sổ. App bị tạm dừng lâu bị Windows coi là "Not Responding": khi người dùng bấm vào,
    /// Windows thay cửa sổ thật bằng cửa sổ "ma" (lớp "Ghost", thuộc dwm.exe) — phải lần ngược về cửa sổ treo thật,
    /// nếu không sẽ không bao giờ nhận ra app đang ngủ được gọi lại.
    /// </summary>
    public static int OwnerPid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        var cls = new System.Text.StringBuilder(32);
        if (GetClassName(hwnd, cls, cls.Capacity) > 0 && cls.ToString() == "Ghost")
        {
            try
            {
                var real = HungWindowFromGhostWindow(hwnd);
                if (real != IntPtr.Zero) return PidOf(real);
            }
            catch (EntryPointNotFoundException) { }
        }
        return PidOf(hwnd);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Tiến trình sở hữu cửa sổ (cấp cao nhất) đang nằm dưới con trỏ chuột — dùng cho "kéo để chọn app".</summary>
    public static int PidAtCursor()
    {
        if (!GetCursorPos(out var p)) return 0;
        var hwnd = WindowFromPoint(p);
        var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        return PidOf(root != IntPtr.Zero ? root : hwnd);
    }
}

/// <summary>
/// Báo ngay khi một cửa sổ được đưa lên trước hoặc khôi phục từ thu nhỏ (kể cả cửa sổ của app đang tạm dừng:
/// Windows vẫn đổi cửa sổ trước dù app chưa xử lý). Phải tạo trên luồng có vòng lặp thông điệp (luồng UI).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundHook : IDisposable
{
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZEEND = 0x0017, WINEVENT_OUTOFCONTEXT = 0;

    private readonly WinEventProc _proc; // giữ tham chiếu để GC không thu hồi callback
    private readonly IntPtr _fg, _restore;

    public ForegroundHook(Action<int> onActivated)
    {
        _proc = (_, _, hwnd, _, _, _, _) => { if (WindowInfo.OwnerPid(hwnd) is > 0 and var pid) onActivated(pid); };
        _fg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _restore = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void Dispose()
    {
        if (_fg != IntPtr.Zero) UnhookWinEvent(_fg);
        if (_restore != IntPtr.Zero) UnhookWinEvent(_restore);
    }
}

/// <summary>Tiến trình đang phát âm thanh (phiên âm thanh Active trên mọi thiết bị phát), qua Core Audio.</summary>
[SupportedOSPlatform("windows")]
public static class AudioSessions
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // IAudioSessionControl2 kế thừa IAudioSessionControl: khai báo lại đủ các hàm của lớp cha theo đúng thứ tự vtable.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    private const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 0x17, AudioSessionStateActive = 1;

    public static HashSet<int> ActivePids()
    {
        var pids = new HashSet<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices) != 0) return pids;
            devices.GetCount(out var n);
            var iid = typeof(IAudioSessionManager2).GUID;
            for (var i = 0; i < n; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) continue;
                if (((IAudioSessionManager2)obj).GetSessionEnumerator(out var sessions) != 0) continue;
                sessions.GetCount(out var count);
                for (var s = 0; s < count; s++)
                {
                    if (sessions.GetSession(s, out var ctl) != 0) continue;
                    if (ctl.GetState(out var state) == 0 && state == AudioSessionStateActive && ctl.GetProcessId(out var pid) == 0 && pid > 0)
                        pids.Add((int)pid);
                }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        return pids;
    }
}

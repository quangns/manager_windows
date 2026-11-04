using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ManagerWindows.Core.Tools;

namespace ManagerWindows.App.Services;

/// <summary>
/// Phím tắt toàn cục (dùng được khi đang ở app khác) qua RegisterHotKey trên một cửa sổ chỉ nhận thông điệp.
/// Phải tạo trên luồng UI. Tổ hợp đã bị app khác giữ thì đăng ký thất bại và được báo lại để người dùng đổi.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<(string Name, Hotkey Key, Action Action)> _wanted = new();
    private int _nextId = 1;
    private bool _suspended;

    /// <summary>Tên hành động → lỗi (null = đăng ký được) của lần áp dụng gần nhất.</summary>
    public Dictionary<string, string?> Status { get; } = new();

    /// <summary>Vừa đăng ký lại xong (Status đã cập nhật).</summary>
    public event Action? Registered;

    public HotkeyService()
    {
        // HWND_MESSAGE (-3): cửa sổ ẩn chỉ để nhận WM_HOTKEY
        _source = new HwndSource(new HwndSourceParameters("ManagerWindows.Hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); }
            catch (Exception ex) { Log.Error("hotkey", ex); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Đặt lại toàn bộ phím tắt (gọi lại mỗi khi đổi cấu hình).</summary>
    public void Apply(IEnumerable<(string Name, string? Text, Action Action)> bindings)
    {
        _wanted.Clear();
        Status.Clear();
        foreach (var (name, text, action) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var hk = Hotkey.Parse(text);
            if (hk == null) { Status[name] = Loc.Instance["Hotkey_Invalid"]; continue; }
            _wanted.Add((name, hk, action));
        }
        Register();
    }

    /// <summary>Đang gõ phím tắt mới trong ô nhập: tạm nhả mọi phím tắt để tổ hợp đi vào ô thay vì chạy lệnh.</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        Register();
    }

    private void Register()
    {
        UnregisterAll();
        if (_suspended) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hk, action) in _wanted)
        {
            if (!seen.Add(hk.ToString())) { Status[name] = Loc.Instance["Hotkey_Duplicate"]; continue; }
            var vk = VirtualKey(hk.Key);
            if (vk == 0) { Status[name] = Loc.Instance["Hotkey_Invalid"]; continue; }
            var mods = MOD_NOREPEAT | (hk.Ctrl ? MOD_CONTROL : 0) | (hk.Alt ? MOD_ALT : 0) | (hk.Shift ? MOD_SHIFT : 0) | (hk.Win ? MOD_WIN : 0);
            var id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods, vk))
            {
                _actions[id] = action;
                Status[name] = null;
            }
            else
            {
                Status[name] = Loc.Instance["Hotkey_Taken"];
                Log.Info("hotkey", $"không đăng ký được {hk} ({name}): lỗi {Marshal.GetLastWin32Error()}");
            }
        }
        Registered?.Invoke();
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    /// <summary>"A" → VK_A, "1" → VK_1, "F5", "PrintScreen"… theo tên phím của WPF.</summary>
    public static uint VirtualKey(string key)
    {
        if (key.Length == 1 && char.IsDigit(key[0])) key = "D" + key;
        if (key.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase)) key = nameof(Key.Snapshot);
        return Enum.TryParse<Key>(key, ignoreCase: true, out var k) && k != Key.None ? (uint)KeyInterop.VirtualKeyFromKey(k) : 0;
    }

    /// <summary>Tên phím để hiển thị/lưu từ phím WPF (D1 → "1", Snapshot → "PrintScreen").</summary>
    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Snapshot => "PrintScreen",
        _ => key.ToString(),
    };

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}

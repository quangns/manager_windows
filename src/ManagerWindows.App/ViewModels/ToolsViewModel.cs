using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Tools;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một phím tắt trong bảng: tên lệnh, tổ hợp phím, trạng thái đăng ký.</summary>
public sealed partial class HotkeyRow : ObservableObject
{
    private readonly Action<HotkeyRow> _changed;

    public HotkeyRow(string name, string label, string value, Action<HotkeyRow> changed)
    {
        Name = name;
        Label = label;
        _value = value;
        _changed = changed;
    }

    public string Name { get; }
    public string Label { get; }
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _hasError;

    partial void OnValueChanged(string value) => _changed(this);
}

/// <summary>Một cửa sổ đang mở trong danh sách "ghim luôn trên cùng".</summary>
public sealed partial class PinRow : ObservableObject
{
    public PinRow(DesktopWindow w, string app)
    {
        Handle = w.Handle;
        Title = w.Title;
        App = app;
        _isTopMost = w.IsTopMost;
    }

    public IntPtr Handle { get; }
    public string Title { get; }
    public string App { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActionText))] private bool _isTopMost;
    public string ActionText => Loc.Instance[IsTopMost ? "Pin_Unpin" : "Pin_Pin"];
}

/// <summary>Tab "Công cụ": phím tắt, chụp màn hình, ghim cửa sổ luôn trên cùng, mini monitor. Mọi thay đổi lưu ngay.</summary>
public sealed partial class ToolsViewModel : ViewModelBase
{
    private ToolsOptions Opt => Host.Config.Tools;
    private static ToolsService? Tools => ToolsService.Current;
    private bool _loading;

    public ObservableCollection<HotkeyRow> Hotkeys { get; } = new();
    public ObservableCollection<PinRow> Windows { get; } = new();
    public List<Option<string>> Sizes { get; } =
    [
        new("small", Loc.Instance["Mini_SizeSmall"]),
        new("medium", Loc.Instance["Mini_SizeMedium"]),
        new("large", Loc.Instance["Mini_SizeLarge"]),
    ];

    public List<Option<string>> Backgrounds { get; } =
    [
        new("transparent", Loc.Instance["Mini_BgTransparent"]),
        new("solid", Loc.Instance["Mini_BgSolid"]),
    ];
    public List<Option<string>> TextColors { get; } =
    [
        new("auto", Loc.Instance["Mini_TextAuto"]),
        new("light", Loc.Instance["Mini_TextLight"]),
        new("dark", Loc.Instance["Mini_TextDark"]),
    ];
    /// <summary>Ô màu gợi ý (bấm để chọn làm màu nền).</summary>
    public List<string> ColorPresets { get; } = [.. OverlayPalette.Presets];

    [ObservableProperty] private bool _hotkeysEnabled;
    [ObservableProperty] private bool _openEditor;
    [ObservableProperty] private bool _copyToClipboard;
    [ObservableProperty] private bool _autoSave;
    [ObservableProperty] private string _saveFolder = "";
    [ObservableProperty] private string _pickerText = "";

    [ObservableProperty] private bool _overlayVisible;
    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _showRam;
    [ObservableProperty] private bool _showDisk;
    [ObservableProperty] private bool _horizontal;
    [ObservableProperty] private string _overlaySize = "medium";
    [ObservableProperty] private bool _clickThrough;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSolidBackground))]
    private string _overlayBackground = "transparent";
    /// <summary>Màu nền dạng #RRGGBB; gõ sai định dạng thì giữ màu cũ.</summary>
    [ObservableProperty] private string _overlayColor = OverlayPalette.DefaultColor;
    [ObservableProperty] private int _overlayOpacity = 80;
    [ObservableProperty] private string _overlayTextColor = "auto";
    public bool IsSolidBackground => OverlayBackground == "solid";

    public ToolsViewModel()
    {
        _loading = true;
        var h = Opt.Hotkeys;
        Hotkeys.Add(new(nameof(h.RegionCapture), Loc.Instance["Hotkey_Region"], h.RegionCapture, OnHotkeyChanged));
        Hotkeys.Add(new(nameof(h.WindowCapture), Loc.Instance["Hotkey_Window"], h.WindowCapture, OnHotkeyChanged));
        Hotkeys.Add(new(nameof(h.FullCapture), Loc.Instance["Hotkey_Full"], h.FullCapture, OnHotkeyChanged));
        Hotkeys.Add(new(nameof(h.TogglePin), Loc.Instance["Hotkey_Pin"], h.TogglePin, OnHotkeyChanged));
        Hotkeys.Add(new(nameof(h.ToggleOverlay), Loc.Instance["Hotkey_Overlay"], h.ToggleOverlay, OnHotkeyChanged));
        _hotkeysEnabled = Opt.HotkeysEnabled;
        var c = Opt.Capture;
        (_openEditor, _copyToClipboard, _autoSave, _saveFolder) = (c.OpenEditor, c.CopyToClipboard, c.AutoSave, CaptureOutput.Folder());
        var o = Opt.Overlay;
        (_showCpu, _showGpu, _showRam, _showDisk, _horizontal, _overlaySize, _clickThrough) = (o.Cpu, o.Gpu, o.Ram, o.Disk, o.Horizontal, o.Size, o.ClickThrough);
        (_overlayBackground, _overlayColor, _overlayOpacity, _overlayTextColor) = (o.Background, o.BackgroundColor, o.BackgroundOpacity, o.TextColor);
        _pickerText = Loc.Instance["Pin_PickerHint"];
        _loading = false;
        if (Tools != null) Tools.StateChanged += OnToolsChanged;
    }

    public void Start()
    {
        OverlayVisible = Tools?.OverlayVisible == true;
        UpdateHotkeyStatus();
        RefreshWindows();
    }

    private void OnToolsChanged()
    {
        _loading = true;
        OverlayVisible = Tools?.OverlayVisible == true;
        var o = Opt.Overlay;
        (Horizontal, OverlaySize, ClickThrough) = (o.Horizontal, o.Size, o.ClickThrough);
        (OverlayBackground, OverlayTextColor) = (o.Background, o.TextColor); // có thể vừa đổi từ menu chuột phải của mini monitor
        _loading = false;
        UpdateHotkeyStatus();
    }

    // ---------- Phím tắt ----------

    private void OnHotkeyChanged(HotkeyRow row)
    {
        var h = Opt.Hotkeys;
        var v = row.Value.Trim();
        switch (row.Name)
        {
            case nameof(h.RegionCapture): h.RegionCapture = v; break;
            case nameof(h.WindowCapture): h.WindowCapture = v; break;
            case nameof(h.FullCapture): h.FullCapture = v; break;
            case nameof(h.TogglePin): h.TogglePin = v; break;
            case nameof(h.ToggleOverlay): h.ToggleOverlay = v; break;
        }
        SaveHotkeys();
    }

    partial void OnHotkeysEnabledChanged(bool value)
    {
        Opt.HotkeysEnabled = value;
        SaveHotkeys();
    }

    private void SaveHotkeys()
    {
        if (_loading) return;
        Host.SaveConfig();
        Tools?.ApplyHotkeys(); // trong lúc đang gõ ô phím tắt, dịch vụ đang tạm nhả: đăng ký lại khi rời ô
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        var status = Tools?.Hotkeys.Status ?? new();
        foreach (var r in Hotkeys)
        {
            if (!HotkeysEnabled || string.IsNullOrWhiteSpace(r.Value))
                (r.StatusText, r.HasError) = (Loc.Instance["Hotkey_Off"], false);
            else if (status.TryGetValue(r.Name, out var err) && err != null)
                (r.StatusText, r.HasError) = (err, true);
            else
                (r.StatusText, r.HasError) = (Loc.Instance["Hotkey_Ok"], false);
        }
    }

    [RelayCommand]
    private void ClearHotkey(HotkeyRow? row)
    {
        if (row != null) row.Value = "";
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        var d = new HotkeyOptions();
        string[] defaults = [d.RegionCapture, d.WindowCapture, d.FullCapture, d.TogglePin, d.ToggleOverlay];
        for (var i = 0; i < Hotkeys.Count; i++) Hotkeys[i].Value = defaults[i];
    }

    // ---------- Chụp màn hình ----------

    private static Window? MainWin => Application.Current.Windows.OfType<Views.MainWindow>().FirstOrDefault();

    [RelayCommand] private static void CaptureRegion() => Tools?.Capture(CaptureMode.Region, MainWin);
    [RelayCommand] private static void CaptureFull() => Tools?.Capture(CaptureMode.FullScreen, MainWin);

    partial void OnOpenEditorChanged(bool value) => SaveCapture();
    partial void OnCopyToClipboardChanged(bool value) => SaveCapture();
    partial void OnAutoSaveChanged(bool value) => SaveCapture();

    private void SaveCapture()
    {
        if (_loading) return;
        var c = Opt.Capture;
        (c.OpenEditor, c.CopyToClipboard, c.AutoSave) = (OpenEditor, CopyToClipboard, AutoSave);
        Host.SaveConfig();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(SaveFolder) ? SaveFolder : null };
        if (dlg.ShowDialog() != true) return;
        SaveFolder = dlg.FolderName;
        Opt.Capture.SaveFolder = dlg.FolderName;
        Host.SaveConfig();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveFolder}\"") { UseShellExecute = false });
    }

    // ---------- Ghim cửa sổ ----------

    [RelayCommand]
    private void RefreshWindows()
    {
        Windows.Clear();
        foreach (var w in DesktopWindows.List(includeMinimized: true))
        {
            if (w.Pid == Environment.ProcessId) continue;
            string app;
            try
            {
                using var p = Process.GetProcessById(w.Pid);
                app = RunningApps.Describe(p.ProcessName, SystemInfo.ProcessPath(w.Pid));
            }
            catch (Exception) { app = ""; }
            Windows.Add(new PinRow(w, app));
        }
        // Cửa sổ đang ghim lên đầu.
        var sorted = Windows.OrderByDescending(r => r.IsTopMost).ToList();
        Windows.Clear();
        foreach (var r in sorted) Windows.Add(r);
    }

    [RelayCommand]
    private void TogglePin(PinRow? row)
    {
        if (row == null || Tools?.TogglePin(row.Handle) is not { } now) return;
        row.IsTopMost = now;
    }

    [RelayCommand]
    private void PinHover(long handle)
    {
        var h = new IntPtr(handle);
        PickerText = h == IntPtr.Zero ? Loc.Instance["Pin_PickerNone"]
            : WindowInfo.PidOf(h) == Environment.ProcessId ? Loc.Instance["Hib_PickerSelf"]
            : Loc.F("Pin_PickerOver", ("title", DesktopWindows.Describe(h).Title));
    }

    [RelayCommand]
    private void PinPicked(long handle)
    {
        var h = new IntPtr(handle);
        PickerText = Loc.Instance["Pin_PickerHint"];
        if (h == IntPtr.Zero || WindowInfo.PidOf(h) == Environment.ProcessId) return;
        Tools?.TogglePin(h);
        RefreshWindows();
    }

    [RelayCommand]
    private void UnpinAll()
    {
        foreach (var r in Windows.Where(r => r.IsTopMost).ToList())
            if (DesktopWindows.SetTopMost(r.Handle, false)) r.IsTopMost = false;
        RefreshWindows();
    }

    // ---------- Mini monitor ----------

    partial void OnOverlayVisibleChanged(bool value)
    {
        if (!_loading) Tools?.SetOverlay(value);
    }

    partial void OnShowCpuChanged(bool value) => SaveOverlay();
    partial void OnShowGpuChanged(bool value) => SaveOverlay();
    partial void OnShowRamChanged(bool value) => SaveOverlay();
    partial void OnShowDiskChanged(bool value) => SaveOverlay();
    partial void OnHorizontalChanged(bool value) => SaveOverlay();
    partial void OnOverlaySizeChanged(string value) => SaveOverlay();
    partial void OnClickThroughChanged(bool value) => SaveOverlay();
    partial void OnOverlayBackgroundChanged(string value) => SaveOverlay();
    partial void OnOverlayOpacityChanged(int value) => SaveOverlay();
    partial void OnOverlayTextColorChanged(string value) => SaveOverlay();
    partial void OnOverlayColorChanged(string value)
    {
        if (OverlayPalette.ParseRgb(value) != null) SaveOverlay(); // đang gõ dở thì chưa lưu
    }

    /// <summary>Bấm ô màu gợi ý: dùng màu đó làm nền (tự chuyển sang nền màu).</summary>
    [RelayCommand]
    private void PickOverlayColor(string hex)
    {
        _loading = true;
        OverlayColor = hex;
        OverlayBackground = "solid";
        _loading = false;
        SaveOverlay();
    }

    private void SaveOverlay()
    {
        if (_loading) return;
        var o = Opt.Overlay;
        (o.Cpu, o.Gpu, o.Ram, o.Disk, o.Horizontal, o.Size, o.ClickThrough) = (ShowCpu, ShowGpu, ShowRam, ShowDisk, Horizontal, OverlaySize, ClickThrough);
        (o.Background, o.BackgroundOpacity, o.TextColor) = (OverlayBackground, Math.Clamp(OverlayOpacity, 10, 100), OverlayTextColor);
        if (OverlayPalette.ParseRgb(OverlayColor) is { } rgb) o.BackgroundColor = $"#{rgb:X6}";
        Host.SaveConfig();
        Tools?.RefreshOverlay();
    }

    [RelayCommand]
    private void ResetOverlayPosition()
    {
        Opt.Overlay.Left = Opt.Overlay.Top = null;
        Host.SaveConfig();
        if (Tools?.OverlayVisible == true)
        {
            Tools.SetOverlay(false);
            Tools.SetOverlay(true);
        }
    }
}

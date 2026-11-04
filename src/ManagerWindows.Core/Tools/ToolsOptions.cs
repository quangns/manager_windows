namespace ManagerWindows.Core.Tools;

/// <summary>Tab "Công cụ": phím tắt toàn cục, chụp màn hình, ghim cửa sổ, mini monitor.</summary>
public sealed class ToolsOptions
{
    public bool HotkeysEnabled { get; set; } = true;
    public HotkeyOptions Hotkeys { get; set; } = new();
    public CaptureOptions Capture { get; set; } = new();
    public OverlayOptions Overlay { get; set; } = new();
}

/// <summary>Phím tắt dạng "Ctrl+Alt+A"; để trống = không dùng.</summary>
public sealed class HotkeyOptions
{
    public string RegionCapture { get; set; } = "Ctrl+Alt+A";
    public string WindowCapture { get; set; } = "Ctrl+Alt+W";
    public string FullCapture { get; set; } = "Ctrl+Alt+F";
    public string TogglePin { get; set; } = "Ctrl+Alt+T";
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+M";
}

public sealed class CaptureOptions
{
    /// <summary>Mở cửa sổ chỉnh sửa (khoanh vùng, tô, đánh số) sau khi chụp; tắt thì chỉ sao chép/lưu ngay.</summary>
    public bool OpenEditor { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    /// <summary>Tự lưu file PNG vào <see cref="SaveFolder"/> (khi không mở trình chỉnh sửa, hoặc khi bấm Lưu).</summary>
    public bool AutoSave { get; set; }
    /// <summary>Trống = Pictures\Manager Windows.</summary>
    public string SaveFolder { get; set; } = "";
    /// <summary>Màu vẽ dùng lần trước (#RRGGBB).</summary>
    public string Color { get; set; } = "#E53935";
    /// <summary>Độ dày nét: 1 mảnh, 2 vừa, 3 đậm.</summary>
    public int Thickness { get; set; } = 2;
}

public sealed class OverlayOptions
{
    public bool Visible { get; set; }
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Disk { get; set; } = true;
    /// <summary>true = một hàng ngang, false = cột dọc.</summary>
    public bool Horizontal { get; set; } = true;
    /// <summary>Cỡ chữ: "small" | "medium" | "large".</summary>
    public string Size { get; set; } = "medium";
    /// <summary>Khoá vị trí và cho chuột bấm xuyên qua (không che app bên dưới).</summary>
    public bool ClickThrough { get; set; }
    /// <summary>Vị trí (DIP); null = góc trên bên phải màn hình chính. Không dùng NaN: JSON không ghi được NaN.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
}

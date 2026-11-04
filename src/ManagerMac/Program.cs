using Avalonia;
using ManagerMac.Services;

namespace ManagerMac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Mỗi user chỉ chạy một Manager Mac (khoá file trong thư mục dữ liệu).
        var dir = Path.Combine(MacPlatform.Home, "Library", "Application Support", "ManagerMac");
        Directory.CreateDirectory(dir);
        FileStream? lockFile;
        try { lockFile = new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; } // đã có phiên đang chạy: macOS tự đưa phiên đó lên khi bấm biểu tượng Dock

        using (lockFile)
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}

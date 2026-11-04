using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using ManagerMac;
using ManagerMac.Services;
using ManagerMac.ViewModels;
using ManagerMac.Views;

// Không dùng await: sau SetupWithoutStarting, luồng chính mang SynchronizationContext của Avalonia nhưng không có vòng lặp
// xử lý → continuation của await nằm chờ mãi. Thay bằng Pump(): tự chạy hàng đợi dispatcher.
static void Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(20);
    }
}

// Dùng: MacPreview <thư mục ảnh>. Dựng từng trang ở chế độ sáng và tối, lưu PNG; lỗi XAML/style lúc chạy sẽ ném ra ở đây.
var outDir = args.Length > 0 ? args[0] : "preview";
Directory.CreateDirectory(outDir);
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

_ = new MacHost();
Loc.Instance.SetLanguage("vi");
foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
{
    Application.Current!.RequestedThemeVariant = variant;
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1180, Height = 780 };
    window.Show();
    foreach (var nav in vm.Nav)
    {
        vm.Selected = nav;
        if (nav.Page is ProcessesViewModel p) { p.Start(); Pump(3000); }
        Pump(1500);
        var frame = window.CaptureRenderedFrame();
        var file = Path.Combine(outDir, $"{nav.Key}-{name}.png");
        frame?.Save(file);
        Console.WriteLine($"{file}: {(frame == null ? "KHÔNG dựng được" : "ok")}");
        if (nav.Page is ProcessesViewModel p2) p2.Stop();
    }
    window.CloseForReal();
}

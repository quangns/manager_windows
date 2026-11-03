using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ManagerWindows.App.Services;
using ManagerWindows.App.ViewModels;
using ManagerWindows.App.Views;

namespace ManagerWindows.App;

/// <summary>
/// Khởi động: một phiên duy nhất cho mỗi user, chạy nền ở khay hệ thống.
/// Tham số: --tray (không mở cửa sổ, dùng khi tự chạy lúc đăng nhập), --rollback-all (gỡ cài đặt: hoàn tác mọi junction).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\ManagerWindows.App";
    private const string ShowEventName = @"Local\ManagerWindows.App.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppHost? _host;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private string? _pendingTab;
    private string? _pendingExcelFile;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("ui", ex.Exception);
            ex.Handled = true;
        };

        if (e.Args.Contains("--rollback-all"))
        {
            RunRollbackAll();
            return;
        }

        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // Đã có phiên đang chạy: nhờ phiên đó mở cửa sổ rồi thoát.
            using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)) ev.Set();
            Shutdown();
            return;
        }

        _host = new AppHost();
        Loc.Instance.SetLanguage(_host.Config.Language);
        ThemeManager.Apply(_host.Config.Theme);
        _host.Notify += (title, message, tab) => Dispatcher.BeginInvoke(() => ShowBalloon(title, message, tab));
        _host.Priority.ExcelHeavy += file => Dispatcher.BeginInvoke(() => OnExcelHeavy(file));
        SettingsViewModel.LanguageChanged += RebuildWindow;

        CreateTray();
        _host.Start();
        ListenForShowRequests();

        if (!e.Args.Contains("--tray")) ShowMain();
    }

    private void RunRollbackAll()
    {
        var host = new AppHost();
        Loc.Instance.SetLanguage(host.Config.Language);
        var (ok, failed) = host.Relocation.RollbackAll();
        MessageBox.Show(Loc.F("Reloc_RollbackAllResult", ("ok", ok), ("failed", failed)), Loc.Instance["App_Title"]);
        Shutdown(failed > 0 ? 1 : 0);
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(() => ShowMain());
        }) { IsBackground = true, Name = "show-listener" };
        thread.Start();
    }

    private void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = Loc.Instance["App_Title"],
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = BuildMenu(),
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowMain();
        _tray.TrayBalloonTipClicked += (_, _) => OnBalloonClicked();
        // Không bật Efficiency Mode cho chính app: app cần phản hồi nhanh khi điều phối ưu tiên.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, Action action)
        {
            var mi = new MenuItem { Header = Loc.Instance[key] };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Tray_Open", () => ShowMain());
        Item("Tray_CleanNow", () =>
        {
            ShowMain("dashboard");
            _vm?.Dashboard.QuickCleanCommand.Execute(null);
        });
        Item("Tray_Relocation", () => ShowMain("relocation"));
        menu.Items.Add(new Separator());
        Item("Tray_Exit", ExitApp);
        return menu;
    }

    private void ShowMain(string? tab = null)
    {
        if (_window == null)
        {
            _vm = new MainViewModel();
            _window = new MainWindow { DataContext = _vm };
        }
        _vm!.Navigate(tab ?? "dashboard");
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Đổi ngôn ngữ: dựng lại cửa sổ và menu để mọi nhãn (kể cả ComboBox) đổi theo.</summary>
    private void RebuildWindow()
    {
        var wasVisible = _window?.IsVisible == true;
        var tab = _vm != null ? MainViewModel.Tabs[_vm.SelectedTab] : null;
        _window?.CloseForReal();
        _window = null;
        _vm = null;
        if (_tray != null)
        {
            _tray.ContextMenu = BuildMenu();
            _tray.ToolTipText = Loc.Instance["App_Title"];
        }
        if (wasVisible) ShowMain(tab);
    }

    private void ShowBalloon(string title, string message, string? tab)
    {
        _pendingTab = tab;
        _pendingExcelFile = null;
        _tray?.ShowNotification(title, message, NotificationIcon.Info);
    }

    private void OnExcelHeavy(string? file)
    {
        _pendingTab = "priority";
        _pendingExcelFile = file;
        var msg = file == null ? Loc.Instance["Excel_HeavyNotifyUnknown"] : Loc.F("Excel_HeavyNotify", ("file", Path.GetFileName(file)));
        _tray?.ShowNotification(Loc.Instance["Notify_Title"], msg, NotificationIcon.Warning);
    }

    private void OnBalloonClicked()
    {
        var file = _pendingExcelFile;
        ShowMain(_pendingTab);
        if (file != null && File.Exists(file)) _ = _vm!.Priority.AnalyzeAsync(file!);
    }

    private void ExitApp()
    {
        _window?.CloseForReal();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Stop(); // trả lại mức ưu tiên ban đầu, ghi nốt thống kê
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

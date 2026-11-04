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
    private const string ExitEventName = @"Local\ManagerWindows.App.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private bool _ownsMutex;
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
        // App sắp sập vì lỗi không bắt được: đánh thức mọi app đang ngủ đông trước khi thoát.
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { _host?.Hibernation.WakeAll("crash"); } catch { }
        };

        if (e.Args.Contains("--rollback-all"))
        {
            RunRollbackAll();
            return;
        }

        if (!AcquireSingleInstance()) return;
        InstanceInfo.Current().Write();

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

    /// <summary>
    /// Mỗi user chỉ chạy một phiên. Bản cài đặt và bản portable dùng chung khoá này: nếu phiên đang chạy là
    /// bản khác (khác file exe hoặc khác phiên bản) thì hỏi người dùng có đóng bản đó để mở bản này không,
    /// tránh trường hợp mở bản mới mà lại thấy cửa sổ của bản cũ.
    /// </summary>
    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        if (created) { _ownsMutex = true; return true; }

        var me = InstanceInfo.Current();
        var other = InstanceInfo.Read();
        var others = OtherAppProcesses();
        // Phiên kia vừa thoát: nhận khoá luôn.
        if (others.Count == 0 && WaitForMutex(TimeSpan.FromSeconds(3)))
        {
            _ownsMutex = true;
            return true;
        }
        // Phiên đang chạy chính là bản này: chỉ cần hiện cửa sổ của nó.
        if (others.Count == 0 || other != null && other.SameAs(me) && others.Any(p => p.Id == other.Pid))
        {
            DisposeAll(others);
            SignalShowAndQuit();
            return false;
        }

        // Bản cũ (trước 0.2.1) không ghi instance.json: vẫn hỏi, chỉ là không biết rõ phiên bản.
        var otherText = other != null && others.Any(p => p.Id == other.Pid) ? other.Describe() : Loc.Instance["Instance_Unknown"];
        var answer = MessageBox.Show(Loc.F("Instance_Other", ("other", otherText), ("me", me.Describe())),
            Loc.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            DisposeAll(others);
            SignalShowAndQuit();
            return false;
        }

        // Nhờ phiên kia tự thoát (trả lại mức ưu tiên, ghi nốt thống kê); bản cũ không hiểu lệnh này thì buộc dừng.
        using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName)) exit.Set();
        _ownsMutex = WaitForMutex(TimeSpan.FromSeconds(8));
        if (!_ownsMutex)
        {
            foreach (var p in others)
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { Log.Error("instance", ex); }
            _ownsMutex = WaitForMutex(TimeSpan.FromSeconds(5));
        }
        DisposeAll(others);
        if (_ownsMutex) return true;

        MessageBox.Show(Loc.Instance["Instance_CloseFailed"], Loc.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown();
        return false;
    }

    private bool WaitForMutex(TimeSpan timeout)
    {
        try { return _mutex!.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; } // phiên kia thoát mà không trả khoá: vẫn nhận được
    }

    /// <summary>Các tiến trình ManagerWindows.App khác của cùng user (cùng phiên đăng nhập).</summary>
    private static List<System.Diagnostics.Process> OtherAppProcesses()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        return System.Diagnostics.Process.GetProcessesByName(self.ProcessName)
            .Where(p => p.Id != self.Id && p.SessionId == self.SessionId).ToList();
    }

    private static void DisposeAll(List<System.Diagnostics.Process> list)
    {
        foreach (var p in list) p.Dispose();
    }

    private void SignalShowAndQuit()
    {
        using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)) ev.Set();
        Shutdown();
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

        // Bản khác (cài đặt/portable, phiên bản khác) yêu cầu phiên này nhường chỗ.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitThread = new Thread(() =>
        {
            if (_exitEvent.WaitOne()) Dispatcher.BeginInvoke(ExitApp);
        }) { IsBackground = true, Name = "exit-listener" };
        exitThread.Start();
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
        Item("Tray_WakeAll", () => _host?.Hibernation.WakeAll("tray"));
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
        ShowMain(_pendingTab == "leftovers" ? "processes" : _pendingTab);
        // Cảnh báo app chạy ngầm: mở thẳng bộ lọc "Chạy ngầm".
        if (_pendingTab == "processes") _vm?.Processes.Start(ProcessFilter.Background);
        if (_pendingTab == "leftovers") _ = _vm?.Processes.OpenLeftoversCommand.ExecuteAsync(null);
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
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

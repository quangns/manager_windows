using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ManagerMac.Services;
using ManagerMac.ViewModels;
using ManagerMac.Views;

namespace ManagerMac;

/// <summary>Khởi động: cửa sổ chính + biểu tượng trên thanh menu. Đóng cửa sổ chỉ ẩn đi, app vẫn chạy nền.</summary>
public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _vm;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var host = new MacHost();
            Loc.Instance.SetLanguage(host.Config.Language);
            SettingsViewModel.ApplyTheme(host.Config.Theme);
            SettingsViewModel.LanguageChanged += Rebuild;
            CreateTray();

            // Bấm biểu tượng Dock khi cửa sổ đang ẩn → mở lại cửa sổ.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) ShowMain(); };

            if (!desktop.Args?.Contains("--tray") ?? true) ShowMain();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowMain(string? page = null)
    {
        if (_window == null)
        {
            _vm = new MainViewModel();
            _window = new MainWindow { DataContext = _vm };
            if (_desktop != null) _desktop.MainWindow = _window;
        }
        if (page != null) _vm!.Navigate(page);
        _window.Show();
        _window.Activate();
    }

    private void Rebuild()
    {
        var visible = _window?.IsVisible == true;
        var page = _vm?.Selected.Key;
        _window?.CloseForReal();
        _window = null;
        _vm = null;
        CreateTray();
        if (visible) ShowMain(page);
    }

    private void CreateTray()
    {
        var menu = new NativeMenu();
        void Item(string key, Action action)
        {
            var mi = new NativeMenuItem(Loc.Instance[key]);
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Item("Tray_Open", () => ShowMain());
        Item("Tray_CleanNow", () => { ShowMain("dashboard"); _vm?.Dashboard.QuickCleanCommand.Execute(null); });
        Item("Mac_TrayProcesses", () => ShowMain("processes"));
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Tray_Exit", () => { _window?.CloseForReal(); _desktop?.Shutdown(); });

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ManagerMac/Assets/tray.png"))),
            ToolTipText = "Manager Mac",
            Menu = menu,
        };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }
}

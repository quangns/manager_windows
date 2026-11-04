using CommunityToolkit.Mvvm.ComponentModel;
using ManagerMac.Services;

namespace ManagerMac.ViewModels;

/// <summary>Một mục ở sidebar: nhãn, biểu tượng (dạng hình vẽ, không dùng emoji) và trang tương ứng.</summary>
public sealed record NavItem(string Key, string Label, Avalonia.Media.Geometry Icon, ViewModelBase Page);

public sealed partial class MainViewModel : ObservableObject
{
    public DashboardViewModel Dashboard { get; } = new();
    public CleanupViewModel Cleanup { get; } = new();
    public SuggestionsViewModel Suggestions { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    public List<NavItem> Nav { get; }

    [ObservableProperty] private NavItem _selected;

    public string EditionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} · macOS";

    // Biểu tượng nét đơn giản (Path data, khung 24×24)
    private const string IconHome = "M3,11 L12,4 L21,11 L21,20 L15,20 L15,14 L9,14 L9,20 L3,20 Z";
    private const string IconClean = "M14,3 L21,10 M11,6 L18,13 L12,21 L6,21 L3,18 L11,12 Z";
    private const string IconBulb = "M9,18 L15,18 M10,21 L14,21 M12,3 C8.7,3 6,5.7 6,9 C6,11.2 7.2,13.1 8,14 L8,16 L16,16 L16,14 C16.8,13.1 18,11.2 18,9 C18,5.7 15.3,3 12,3 Z";
    private const string IconChip = "M7,7 L17,7 L17,17 L7,17 Z M10,3 L10,7 M14,3 L14,7 M10,17 L10,21 M14,17 L14,21 M3,10 L7,10 M3,14 L7,14 M17,10 L21,10 M17,14 L21,14";
    private const string IconGear = "M12,9 A3,3 0 1 1 12,15 A3,3 0 1 1 12,9 Z M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5";

    private static Avalonia.Media.Geometry G(string data) => Avalonia.Media.StreamGeometry.Parse(data);

    public MainViewModel()
    {
        Nav =
        [
            new("dashboard", Loc.Instance["Tab_Dashboard"], G(IconHome), Dashboard),
            new("cleanup", Loc.Instance["Tab_Cleanup"], G(IconClean), Cleanup),
            new("suggestions", Loc.Instance["Tab_Suggestions"], G(IconBulb), Suggestions),
            new("processes", Loc.Instance["Tab_Processes"], G(IconChip), Processes),
            new("settings", Loc.Instance["Tab_Settings"], G(IconGear), Settings),
        ];
        _selected = Nav[0];
        OnSelectedChanged(_selected);
    }

    public void Navigate(string key)
    {
        if (Nav.FirstOrDefault(n => n.Key == key) is { } item)
        {
            if (Selected == item) OnSelectedChanged(item);
            else Selected = item;
        }
    }

    partial void OnSelectedChanged(NavItem value)
    {
        if (value.Page == Processes) Processes.Start(); else Processes.Stop();
        if (value.Page == Dashboard) _ = Dashboard.RefreshCommand.ExecuteAsync(null);
    }
}

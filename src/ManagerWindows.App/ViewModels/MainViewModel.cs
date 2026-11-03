using CommunityToolkit.Mvvm.ComponentModel;

namespace ManagerWindows.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public static readonly string[] Tabs = ["dashboard", "cleanup", "suggestions", "relocation", "usage", "priority", "settings"];

    public DashboardViewModel Dashboard { get; } = new();
    public CleanupViewModel Cleanup { get; } = new();
    public SuggestionsViewModel Suggestions { get; } = new();
    public RelocationViewModel Relocation { get; } = new();
    public UsageViewModel Usage { get; } = new();
    public PriorityViewModel Priority { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty] private int _selectedTab;

    public void Navigate(string? tab)
    {
        var i = tab == null ? -1 : Array.IndexOf(Tabs, tab);
        if (i >= 0) SelectedTab = i;
    }

    partial void OnSelectedTabChanged(int value)
    {
        // Tải dữ liệu khi mở tab để không làm chậm lúc khởi động.
        switch (Tabs[value])
        {
            case "dashboard": _ = Dashboard.RefreshCommand.ExecuteAsync(null); break;
            case "relocation": Relocation.LoadRecords(); break;
            case "usage": _ = Usage.RefreshCommand.ExecuteAsync(null); break;
        }
    }
}

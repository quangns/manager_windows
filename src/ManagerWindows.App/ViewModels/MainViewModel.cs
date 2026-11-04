using CommunityToolkit.Mvvm.ComponentModel;

namespace ManagerWindows.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public static readonly string[] Tabs = ["dashboard", "cleanup", "suggestions", "diskusage", "relocation", "usage", "processes", "hibernation", "priority", "tools", "settings"];

    public DashboardViewModel Dashboard { get; } = new();
    public CleanupViewModel Cleanup { get; } = new();
    public SuggestionsViewModel Suggestions { get; } = new();
    public DiskUsageViewModel DiskUsage { get; } = new();
    public RelocationViewModel Relocation { get; } = new();
    public UsageViewModel Usage { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public HibernationViewModel Hibernation { get; } = new();
    public PriorityViewModel Priority { get; } = new();
    public ToolsViewModel Tools { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty] private int _selectedTab;

    /// <summary>"v0.2.1 · Bản portable" / "v0.2.1 · Bản cài đặt" ở sidebar, để biết đang mở bản nào.</summary>
    public string EditionText => Services.Edition.Label;

    public MainViewModel() => Dashboard.NavigateRequested = Navigate;

    public void Navigate(string? tab)
    {
        var i = tab == null ? -1 : Array.IndexOf(Tabs, tab);
        if (i < 0) return;
        // Mở lại đúng tab đang chọn thì không có sự kiện đổi tab: vẫn phải tải lại dữ liệu.
        if (i == SelectedTab) OnTabShown(i);
        else SelectedTab = i;
    }

    partial void OnSelectedTabChanged(int value) => OnTabShown(value);

    private void OnTabShown(int value)
    {
        // Bảng tiến trình chỉ lấy mẫu khi đang mở tab.
        if (Tabs[value] == "processes") Processes.Start();
        else Processes.Stop();
        if (Tabs[value] == "hibernation") Hibernation.Start();
        else Hibernation.Stop();

        // Tải dữ liệu khi mở tab để không làm chậm lúc khởi động.
        switch (Tabs[value])
        {
            case "dashboard": _ = Dashboard.RefreshCommand.ExecuteAsync(null); break;
            case "diskusage": DiskUsage.LoadDrives(); break;
            case "relocation": Relocation.LoadRecords(); break;
            case "usage": _ = Usage.RefreshCommand.ExecuteAsync(null); break;
            case "priority": _ = Priority.LoadAppsCommand.ExecuteAsync(null); break;
            case "tools": Tools.Start(); break;
        }
    }
}

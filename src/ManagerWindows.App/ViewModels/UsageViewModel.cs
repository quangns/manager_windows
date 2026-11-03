using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Ipc;

namespace ManagerWindows.App.ViewModels;

public sealed record UsageRow(string Name, string Exe, string ActiveText, double ActiveSeconds, int Launches, int Sessions);

/// <summary>Một cột trong biểu đồ giờ dùng máy (24 cột, một chuỗi số liệu).</summary>
public sealed record HourBar(int Hour, double Height, string Tooltip, string Label);

public sealed partial class UsageViewModel : ViewModelBase
{
    public const double ChartHeight = 140;

    public ObservableCollection<UsageRow> Apps { get; } = new();
    public ObservableCollection<HourBar> Hours { get; } = new();

    public List<Option<int>> Periods { get; } =
    [
        new(1, Loc.Instance["Period_Today"]),
        new(7, Loc.Instance["Period_7d"]),
        new(30, Loc.Instance["Period_30d"]),
    ];

    [ObservableProperty] private int _periodDays = 7;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private bool _launchesAvailable;

    partial void OnPeriodDaysChanged(int value) => _ = RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var to = DateTime.UtcNow;
        var from = PeriodDays == 1 ? DateTime.Today.ToUniversalTime() : to.AddDays(-PeriodDays);

        // Số lần mở lấy từ service (WMI); không có service thì để trống.
        Dictionary<string, int> launches = new();
        var resp = await Host.Service.SendAsync(IpcCommands.GetLaunchCounts, new LaunchCountsRequest(from, to));
        LaunchesAvailable = resp.Ok;
        if (resp.Ok && resp.Data is { } d)
            launches = new Dictionary<string, int>(
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(d, IpcConstants.Json) ?? new(),
                StringComparer.OrdinalIgnoreCase);

        var (top, hourly) = await Task.Run(() =>
        {
            Host.UsageTracker.Flush();
            return (Host.Usage.TopApps(from, to, 50, launches), Host.Usage.HourlyActiveSeconds(from, to));
        });

        Apps.Clear();
        foreach (var a in top)
            Apps.Add(new UsageRow(a.Name, a.Exe, Fmt.Duration(a.ActiveSeconds), a.ActiveSeconds, a.Launches, a.Sessions));
        TotalText = Loc.F("Usage_Total", ("time", Fmt.Duration(top.Sum(a => a.ActiveSeconds))));

        Hours.Clear();
        var max = Math.Max(hourly.Max(), 1);
        for (var h = 0; h < 24; h++)
            Hours.Add(new HourBar(h, hourly[h] / max * ChartHeight,
                $"{h:00}:00–{(h + 1) % 24:00}:00 · {Fmt.Duration(hourly[h])}",
                h % 3 == 0 ? $"{h}h" : ""));
    }

    [RelayCommand]
    private void Pin(UsageRow? row)
    {
        if (row == null) return;
        var pinned = Host.Config.Priority.Pinned;
        if (!pinned.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Gán danh sách mới thay vì sửa tại chỗ: luồng nền đang đọc danh sách cũ.
            Host.Config.Priority.Pinned = [.. pinned, row.Name];
            Host.SaveConfig();
        }
        Status = Loc.F("Usage_Pinned", ("name", row.Name));
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Ipc;

namespace ManagerWindows.App.ViewModels;

public sealed record DriveView(string Name, string FreeText, double UsedPercent, bool Low);

public sealed record TopAppView(string Name, string ActiveText);

public sealed partial class DashboardViewModel : ViewModelBase
{
    public ObservableCollection<DriveView> Drives { get; } = new();
    public ObservableCollection<TopAppView> TopToday { get; } = new();

    [ObservableProperty] private string _totalFreed = "";
    [ObservableProperty] private string _lastCleanup = "";
    [ObservableProperty] private string _serviceStatus = "";
    [ObservableProperty] private bool _serviceOk;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var used = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
            Drives.Add(new DriveView(d.Name, Loc.F("Dash_FreeOf", ("free", Fmt.Bytes(d.TotalFreeSpace)), ("total", Fmt.Bytes(d.TotalSize))),
                used, d.TotalFreeSpace < d.TotalSize / 10));
        }

        var (freed, last, top) = await Task.Run(() =>
        {
            Host.UsageTracker.Flush();
            var start = DateTime.Today.ToUniversalTime();
            return (Host.Runs.TotalFreed(), Host.Runs.Recent(1).FirstOrDefault(),
                Host.Usage.TopApps(start, DateTime.UtcNow, 5, new Dictionary<string, int>()));
        });
        TotalFreed = Fmt.Bytes(freed);
        LastCleanup = last == null
            ? Loc.Instance["Dash_Never"]
            : $"{last.FinishedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {Fmt.Bytes(last.FreedBytes)}";
        TopToday.Clear();
        foreach (var a in top) TopToday.Add(new TopAppView(a.Name, Fmt.Duration(a.ActiveSeconds)));

        var ping = await Host.Service.SendAsync(IpcCommands.Ping);
        ServiceOk = ping.Ok;
        ServiceStatus = Loc.Instance[ping.Ok ? "Dash_ServiceOk" : "Dash_ServiceMissing"];
    }

    /// <summary>Dọn nhanh: luật người dùng đang bật + luật hệ thống mặc định qua service.</summary>
    [RelayCommand]
    private Task QuickCleanAsync() => RunBusyAsync(async () =>
    {
        var result = await Task.Run(() => Host.Scheduler.Run(Host.Scheduler.EnabledUserRules(), "manual"));
        long freed = result.FreedBytes;
        int deleted = result.Deleted, failed = result.Failed;

        var systemIds = Host.Rules.Where(r => r.Scope == RuleScope.System && r.EnabledByDefault).Select(r => r.Id).ToList();
        var sys = await Host.Service.SendAsync(IpcCommands.RunSystemCleanup, new RunCleanupRequest(systemIds));
        if (sys.Ok && sys.Data is { } d && System.Text.Json.JsonSerializer.Deserialize<CleanupResultDto>(d, IpcConstants.Json) is { } dto)
        {
            freed += dto.FreedBytes;
            deleted += dto.Deleted;
            failed += dto.Failed;
        }
        Status = Loc.F("Clean_Result", ("size", Fmt.Bytes(freed)), ("deleted", deleted), ("failed", failed))
                 + (sys.Ok ? "" : " " + Loc.Instance["Clean_SystemNeedsService"]);
        await RefreshAsync();
    });
}

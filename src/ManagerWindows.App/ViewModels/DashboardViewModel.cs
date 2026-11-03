using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Relocation;
using ManagerWindows.Ipc;

namespace ManagerWindows.App.ViewModels;

/// <summary>Mức đầy của ổ: ok, warn (&lt; 15% trống), low (&lt; 10% trống).</summary>
public sealed record DriveView(string Name, string Label, string FreeText, string TotalText, double UsedRatio, string Level);

/// <summary>Một dòng "Dùng nhiều nhất hôm nay": Ratio là độ dài thanh so với app dùng nhiều nhất.</summary>
public sealed record TopAppView(string Name, string ActiveText, double Ratio);

/// <summary>Một việc nên làm, sinh từ số liệu thật trên máy. Level: info | warn | critical | ok.</summary>
public sealed record ActionItem(string Glyph, string Text, string? ActionLabel, string? Tab, string Level);

public sealed partial class DashboardViewModel : ViewModelBase
{
    public ObservableCollection<DriveView> OtherDrives { get; } = new();
    public ObservableCollection<TopAppView> TopToday { get; } = new();
    public ObservableCollection<ActionItem> Actions { get; } = new();

    /// <summary>Do MainViewModel gán để nút trong "Việc nên làm" mở được tab khác.</summary>
    public Action<string>? NavigateRequested { get; set; }

    [ObservableProperty] private DriveView? _systemDrive;
    [ObservableProperty] private string _totalFreed = "";
    [ObservableProperty] private string _lastCleanup = "";
    [ObservableProperty] private string _lastCleanupDetail = "";
    [ObservableProperty] private string _todayActive = "";
    [ObservableProperty] private string _serviceStatus = "";
    [ObservableProperty] private string _serviceShort = "";
    [ObservableProperty] private bool _serviceOk;
    [ObservableProperty] private bool _hasTopApps;

    [RelayCommand]
    private void Navigate(string? tab)
    {
        if (tab != null) NavigateRequested?.Invoke(tab);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        OtherDrives.Clear();
        SystemDrive = null;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var freeRatio = (double)d.TotalFreeSpace / d.TotalSize;
            var view = new DriveView(d.Name.TrimEnd('\\'), d.VolumeLabel, Fmt.Bytes(d.TotalFreeSpace), Fmt.Bytes(d.TotalSize),
                1 - freeRatio, freeRatio < 0.10 ? "low" : freeRatio < 0.15 ? "warn" : "ok");
            if (string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) SystemDrive = view;
            else OtherDrives.Add(view);
        }

        var (freed, last, top, todaySeconds, relocations) = await Task.Run(() =>
        {
            Host.UsageTracker.Flush();
            var start = DateTime.Today.ToUniversalTime();
            var apps = Host.Usage.TopApps(start, DateTime.UtcNow, 500, new Dictionary<string, int>());
            var rel = Host.Relocations.Active().Select(r => (Record: r, Health: Host.Relocation.Check(r))).ToList();
            return (Host.Runs.TotalFreed(), Host.Runs.Recent(1).FirstOrDefault(), apps, apps.Sum(a => a.ActiveSeconds), rel);
        });

        TotalFreed = freed > 0 ? Fmt.Bytes(freed) : Loc.Instance["Dash_NoData"];
        LastCleanup = last == null ? Loc.Instance["Dash_Never"] : last.FinishedUtc.ToLocalTime().ToString("dd/MM HH:mm");
        LastCleanupDetail = last == null ? "" : Loc.F("Dash_LastFreed", ("size", Fmt.Bytes(last.FreedBytes)));
        TodayActive = todaySeconds > 0 ? Fmt.Duration(todaySeconds) : Loc.Instance["Dash_NoData"];

        TopToday.Clear();
        var max = top.Count > 0 ? Math.Max(top[0].ActiveSeconds, 1) : 1;
        foreach (var a in top.Take(6)) TopToday.Add(new TopAppView(a.Name, Fmt.Duration(a.ActiveSeconds), a.ActiveSeconds / max));
        HasTopApps = TopToday.Count > 0;

        var ping = await Host.Service.SendAsync(IpcCommands.Ping);
        ServiceOk = ping.Ok;
        ServiceStatus = Loc.Instance[ping.Ok ? "Dash_ServiceOk" : Host.IsPortable ? "Dash_ServicePortable" : "Dash_ServiceMissing"];
        ServiceShort = Loc.Instance[ping.Ok ? "Nav_ServiceOk" : Host.IsPortable ? "Nav_ServicePortable" : "Nav_ServiceMissing"];

        BuildActions(last, relocations);
    }

    /// <summary>"Việc nên làm": chỉ dựa trên số liệu thật, không có thì báo mọi thứ ổn.</summary>
    private void BuildActions(Storage.CleanupRunRow? last, List<(RelocationRecord Record, RelocationHealth Health)> relocations)
    {
        Actions.Clear();
        if (SystemDrive is { Level: not "ok" } d)
            Actions.Add(new ActionItem("\uE7BA", Loc.F("Act_DiskLow", ("drive", d.Name), ("free", d.FreeText)),
                Loc.Instance["Act_Scan"], "cleanup", d.Level == "low" ? "critical" : "warn"));

        var broken = relocations.Count(r => r.Health is RelocationHealth.TargetMissing or RelocationHealth.LinkMissing or RelocationHealth.LinkWrongTarget);
        if (broken > 0)
            Actions.Add(new ActionItem("\uE783", Loc.F("Act_RelocBroken", ("count", broken)), Loc.Instance["Act_View"], "relocation", "critical"));

        if (!ServiceOk && !Host.IsPortable)
            Actions.Add(new ActionItem("\uE7BA", Loc.Instance["Act_ServiceMissing"], null, null, "warn"));

        var keepDays = Host.Config.Relocation.BackupKeepDays;
        var backups = relocations.Where(r => r.Record.State == RelocationState.Completed
                                             && (DateTime.UtcNow - r.Record.UpdatedUtc).TotalDays >= keepDays).ToList();
        if (backups.Count > 0)
            Actions.Add(new ActionItem("\uE74D", Loc.F("Act_BackupDue", ("count", backups.Count), ("size", Fmt.Bytes(backups.Sum(b => b.Record.Bytes)))),
                Loc.Instance["Act_View"], "relocation", "info"));

        if (last == null)
            Actions.Add(new ActionItem("\uE946", Loc.Instance["Act_NeverCleaned"], Loc.Instance["Act_Scan"], "cleanup", "info"));
        else if ((DateTime.UtcNow - last.FinishedUtc).TotalDays > 30)
            Actions.Add(new ActionItem("\uE946", Loc.F("Act_CleanOld", ("days", (int)(DateTime.UtcNow - last.FinishedUtc).TotalDays)),
                Loc.Instance["Act_Scan"], "cleanup", "info"));

        if (Actions.Count == 0)
            Actions.Add(new ActionItem("\uE73E", Loc.Instance["Act_AllGood"], null, null, "ok"));
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
        await RefreshAsync();
        Status = Loc.F("Clean_Result", ("size", Fmt.Bytes(freed)), ("deleted", deleted), ("failed", failed))
                 + (sys.Ok ? "" : " " + Loc.Instance["Clean_SystemNeedsService"]);
    });
}

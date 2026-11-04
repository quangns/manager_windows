using System.Diagnostics;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>
/// Mỗi 10 giây lấy mẫu tiến trình, báo ở khay khi một app chạy ngầm (không có cửa sổ, trong phiên người dùng)
/// dùng CPU/RAM vượt ngưỡng liên tục. Bấm thông báo mở tab Tiến trình ở bộ lọc "Chạy ngầm".
/// </summary>
public sealed class BackgroundWatcher(AppHost host) : BackgroundLoop
{
    private readonly ProcessMonitorSampler _sampler = new();
    private readonly BackgroundAlertTracker _tracker = new();
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    protected override TimeSpan Interval => TimeSpan.FromSeconds(10);

    protected override void Tick(CancellationToken ct)
    {
        var opt = host.Config.Monitor;
        if (!opt.AlertsEnabled) return;
        var groups = ProcessGrouping.Group(_sampler.Sample(), _session, _windowsDir);
        foreach (var a in _tracker.Update(groups, DateTime.UtcNow, opt))
        {
            var g = groups.First(x => x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            var display = RunningApps.Describe(a.Name, g.Path);
            var msg = a.Reason == AlertReason.Cpu
                ? Loc.F("Proc_AlertCpuMsg", ("app", display), ("cpu", a.CpuPercent.ToString("0")), ("sec", opt.SustainSeconds))
                : Loc.F("Proc_AlertRamMsg", ("app", display), ("ram", Fmt.Bytes(a.WorkingSetBytes)));
            Log.Info("background-watch", msg);
            host.RaiseNotify(Loc.Instance["Notify_Title"], msg, "processes");
        }
    }
}

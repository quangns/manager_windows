using ManagerWindows.App.ViewModels;
using ManagerWindows.Core.Monitoring;

namespace ManagerWindows.App.Services;

/// <summary>
/// Mỗi giờ kiểm tra tiến trình sót; báo ở khay khi có app treo hoặc tiến trình sót giữ nhiều RAM.
/// Mỗi kiểu chỉ báo lại sau 6 giờ để không làm phiền. Không bao giờ tự dừng tiến trình.
/// </summary>
public sealed class LeftoverWatcher(AppHost host) : BackgroundLoop
{
    private DateTime _lastNotify = DateTime.MinValue;

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override void Tick(CancellationToken ct)
    {
        var opt = host.Config.Leftovers;
        if (!opt.Notify || DateTime.UtcNow - _lastNotify < TimeSpan.FromHours(6)) return;
        var groups = LeftoversViewModel.Scan(host);
        var hung = groups.Count(g => g.Reasons.HasFlag(LeftoverReason.NotResponding));
        var ram = groups.Sum(g => g.WorkingSetBytes);
        if (hung == 0 && ram < (long)opt.NotifyRamMb << 20) return;
        _lastNotify = DateTime.UtcNow;
        var msg = Loc.F("Left_Notify", ("count", groups.Count), ("ram", Fmt.Bytes(ram)));
        Log.Info("leftovers", msg);
        host.RaiseNotify(Loc.Instance["Notify_Title"], msg, "leftovers");
    }
}

using System.Collections.Concurrent;
using ManagerWindows.App.ViewModels;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>
/// Mỗi phút ghi lại app nào đang có cửa sổ (để biết app nào đã đóng hết cửa sổ mà vẫn chạy ngầm).
/// Mỗi giờ kiểm tra tiến trình sót; báo ở khay khi có app treo hoặc tiến trình sót giữ nhiều RAM.
/// Mỗi kiểu chỉ báo lại sau 6 giờ để không làm phiền. Không bao giờ tự dừng tiến trình.
/// </summary>
public sealed class LeftoverWatcher(AppHost host) : BackgroundLoop
{
    private readonly ConcurrentDictionary<string, DateTime> _lastWindow = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastNotify = DateTime.MinValue;
    private DateTime _lastScan = DateTime.UtcNow; // quét lần đầu sau 1 giờ: đủ thời gian ghi nhận cửa sổ

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>Lần cuối thấy mỗi app (theo tên tiến trình) có cửa sổ, từ lúc Manager Windows chạy.</summary>
    public IReadOnlyDictionary<string, DateTime> LastWindowSeen => new Dictionary<string, DateTime>(_lastWindow, StringComparer.OrdinalIgnoreCase);

    protected override void Tick(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var name in ProcessTree.WindowedAppNames()) _lastWindow[name] = now;

        if (now - _lastScan < TimeSpan.FromHours(1)) return;
        _lastScan = now;
        var opt = host.Config.Leftovers;
        if (!opt.Notify || now - _lastNotify < TimeSpan.FromHours(6)) return;
        var groups = LeftoversViewModel.Scan(host);
        var hung = groups.Count(g => g.Reasons.HasFlag(LeftoverReason.NotResponding));
        var ram = groups.Sum(g => g.WorkingSetBytes);
        if (hung == 0 && ram < (long)opt.NotifyRamMb << 20) return;
        _lastNotify = now;
        var msg = Loc.F("Left_Notify", ("count", groups.Count), ("ram", Fmt.Bytes(ram)));
        Log.Info("leftovers", msg);
        host.RaiseNotify(Loc.Instance["Notify_Title"], msg, "leftovers");
    }
}

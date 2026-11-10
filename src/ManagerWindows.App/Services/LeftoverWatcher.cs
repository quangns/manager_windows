using System.Collections.Concurrent;
using ManagerWindows.App.ViewModels;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>
/// Mỗi phút ghi lại app nào đang có cửa sổ (để biết app nào đã đóng hết cửa sổ mà vẫn chạy ngầm).
/// Mỗi giờ kiểm tra tiến trình sót; báo ở khay khi có app treo hoặc tiến trình sót giữ nhiều RAM.
/// Mỗi kiểu chỉ báo lại sau 6 giờ để không làm phiền.
/// Chỉ khi người dùng tick "tự dừng" (tab Tiến trình): mỗi 15 phút dừng app chạy ngầm lâu không hoạt động /
/// app đã đóng cửa sổ mà vẫn chạy — trừ danh sách "không bao giờ ngủ", app chạy nền hợp lệ và app treo.
/// </summary>
public sealed class LeftoverWatcher(AppHost host) : BackgroundLoop
{
    private readonly ConcurrentDictionary<string, DateTime> _lastWindow = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastNotify = DateTime.MinValue;
    private DateTime _lastScan = DateTime.UtcNow; // quét lần đầu sau 1 giờ: đủ thời gian ghi nhận cửa sổ
    private DateTime _lastAutoStop = DateTime.UtcNow;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>Lần cuối thấy mỗi app (theo tên tiến trình) có cửa sổ, từ lúc Manager Windows chạy.</summary>
    public IReadOnlyDictionary<string, DateTime> LastWindowSeen => new Dictionary<string, DateTime>(_lastWindow, StringComparer.OrdinalIgnoreCase);

    protected override void Tick(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var name in ProcessTree.WindowedAppNames()) _lastWindow[name] = now;
        if (now - _lastAutoStop >= TimeSpan.FromMinutes(15))
        {
            _lastAutoStop = now;
            AutoStop();
        }

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

    /// <summary>
    /// Tự dừng theo tuỳ chọn. Chỉ dừng tiến trình cùng quyền người dùng (không nhờ dịch vụ nền dừng app chạy quyền Admin),
    /// ghi log và báo ở khay để người dùng biết, thêm app vào danh sách "không bao giờ ngủ" nếu cần giữ.
    /// </summary>
    private void AutoStop()
    {
        var opt = host.Config.Leftovers;
        if (!opt.AutoStopIdle && !opt.AutoStopClosed) return;
        var targets = LeftoverDetector.AutoStopTargets(LeftoversViewModel.Scan(host), opt, LeftoversViewModel.NeverStop(host));
        if (targets.Count == 0) return;
        int ok = 0, failed = 0;
        long freed = 0;
        var stopped = new List<string>();
        foreach (var g in targets)
        {
            var any = false;
            foreach (var pid in g.Pids)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    p.Kill();
                    ok++;
                    any = true;
                }
                catch (ArgumentException) { } // đã tự thoát
                catch (Exception) { failed++; } // không đủ quyền (app chạy quyền Admin): bỏ qua
            }
            if (any)
            {
                freed += g.WorkingSetBytes;
                stopped.Add(RunningApps.Describe(g.Name, g.Path));
            }
        }
        if (stopped.Count == 0) return;
        var msg = Loc.F("Left_AutoStopped", ("apps", string.Join(", ", stopped)), ("ram", Fmt.Bytes(freed)));
        Log.Info("leftovers", $"tự dừng {string.Join(",", targets.Select(t => t.Name))}: {ok} tiến trình, lỗi {failed}");
        host.RaiseNotify(Loc.Instance["Notify_Title"], msg, "processes");
    }
}

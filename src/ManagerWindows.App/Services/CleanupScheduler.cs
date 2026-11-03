using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Scheduling;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>
/// Dọn rác phần của người dùng (Scope=User) theo lịch. Phần hệ thống (Scope=System) do service tự chạy
/// theo lịch riêng, hoặc app nhờ service chạy khi người dùng bấm "Dọn ngay".
/// </summary>
public sealed class CleanupScheduler(AppHost host) : BackgroundLoop
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    public bool IsEnabled(CleanupRule r) =>
        host.Config.Cleanup.EnabledRuleIds?.Contains(r.Id) ?? r.EnabledByDefault;

    /// <summary>
    /// Luật người dùng đang bật dùng cho dọn tự động/dọn nhanh. Bỏ qua mục "nên xem lại":
    /// mục đó chỉ được dọn thủ công sau khi người dùng đã xem danh sách file.
    /// </summary>
    public List<CleanupRule> EnabledUserRules() =>
        host.Rules.Where(r => r.Scope == RuleScope.User && r.Risk != RuleRisk.Review && IsEnabled(r)).ToList();

    protected override void Tick(CancellationToken ct)
    {
        var cfg = host.Config.Cleanup;
        var now = DateTime.Now;
        var idleMinutes = SystemInfo.IdleSeconds() / 60;
        if (!ScheduleEvaluator.IsDue(cfg.Schedule, cfg.LastRunLocal, now, idleMinutes, SystemInfo.OnBattery())) return;

        var result = Run(EnabledUserRules(), "schedule", ct);
        cfg.LastRunLocal = now;
        host.SaveConfig();
        if (result.FreedBytes > 0)
            host.RaiseNotify(Loc.Instance["Notify_Title"],
                Loc.F("Clean_Result", ("size", Fmt.Bytes(result.FreedBytes)), ("deleted", result.Deleted), ("failed", result.Failed)),
                "cleanup");
    }

    public CleanupPlan Plan(IEnumerable<CleanupRule> rules, CancellationToken ct = default) =>
        new CleanupPlanner(SystemClock.Instance, new WindowsRecycleBin()).Plan(rules, ct);

    public CleanupResult Execute(CleanupPlan plan, string trigger, CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            var started = DateTime.UtcNow;
            var result = new CleanupExecutor(new RecycleBinDeleter(), SystemClock.Instance, new WindowsRecycleBin()).Execute(plan, ct);
            host.Runs.Add(started, DateTime.UtcNow, trigger, result.FreedBytes, result.Deleted, result.Failed);
            Log.Info("cleanup", $"{trigger}: freed {result.FreedBytes}, deleted {result.Deleted}, failed {result.Failed}");
            return result;
        }
        finally { _lock.Release(); }
    }

    public CleanupResult Run(IEnumerable<CleanupRule> rules, string trigger, CancellationToken ct = default) =>
        Execute(Plan(rules, ct), trigger, ct);
}

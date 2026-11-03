using System.Text.Json;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Scheduling;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;
using ManagerWindows.Storage;

namespace ManagerWindows.Service;

public sealed class ServiceConfig
{
    public Schedule Schedule { get; set; } = new() { RequireIdleMinutes = 0 };
    public List<string>? EnabledRuleIds { get; set; }
    public DateTime? LastRunLocal { get; set; }
}

/// <summary>Dọn các thư mục rác cấp hệ thống (Scope=System). Chỉ chạy luật trong file của thư mục cài đặt.</summary>
public sealed class SystemCleanupService(CleanupRunRepository runs, ILogger<SystemCleanupService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public List<CleanupRule> SystemRules() =>
        RuleLoader.Load(ServicePaths.RulesFile).Where(r => r.Scope == RuleScope.System).ToList();

    public ServiceConfig LoadConfig()
    {
        try
        {
            return File.Exists(ServicePaths.ConfigFile)
                ? JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(ServicePaths.ConfigFile), RuleLoader.JsonOptions) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public void SaveConfig(ServiceConfig c) =>
        File.WriteAllText(ServicePaths.ConfigFile, JsonSerializer.Serialize(c, RuleLoader.JsonOptions));

    public List<RuleEstimate> Preview()
    {
        var plan = new CleanupPlanner(SystemClock.Instance).Plan(SystemRules());
        return plan.Rules.Select(r => new RuleEstimate(r.Rule.Id, r.TotalBytes, r.Files.Count)).ToList();
    }

    public async Task<CleanupResultDto> RunAsync(IReadOnlyCollection<string>? ruleIds, string trigger, CancellationToken ct)
    {
        var all = SystemRules();
        var unknown = ruleIds?.Where(id => all.All(r => r.Id != id)).ToList();
        if (unknown is { Count: > 0 }) throw new InvalidDataException($"Luật không hợp lệ: {string.Join(", ", unknown)}");
        var selected = ruleIds == null
            ? all.Where(r => r.EnabledByDefault).ToList()
            : all.Where(r => ruleIds.Contains(r.Id)).ToList();

        await _lock.WaitAsync(ct);
        try
        {
            var started = DateTime.UtcNow;
            var plan = new CleanupPlanner(SystemClock.Instance).Plan(selected, ct);
            var result = new CleanupExecutor(new RecycleBinDeleter(), SystemClock.Instance).Execute(plan, ct);
            runs.Add(started, DateTime.UtcNow, trigger, result.FreedBytes, result.Deleted, result.Failed);
            log.LogInformation("Dọn rác hệ thống ({Trigger}): giải phóng {Bytes} byte, xoá {Deleted}, lỗi {Failed}",
                trigger, result.FreedBytes, result.Deleted, result.Failed);
            return new CleanupResultDto(result.FreedBytes, result.Deleted, result.Failed);
        }
        finally { _lock.Release(); }
    }
}

public sealed class SystemCleanupWorker(SystemCleanupService cleanup, UsageRepository usage, ILogger<SystemCleanupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        var lastPurge = DateTime.MinValue;
        do
        {
            try
            {
                var cfg = cleanup.LoadConfig();
                var now = DateTime.Now;
                // Service chạy ở session 0 nên không đo được thời gian rảnh của người dùng: chỉ xét lịch và nguồn điện.
                if (ScheduleEvaluator.IsDue(cfg.Schedule, cfg.LastRunLocal, now, double.MaxValue, SystemInfo.OnBattery()))
                {
                    await cleanup.RunAsync(cfg.EnabledRuleIds, "schedule", ct);
                    cfg.LastRunLocal = now;
                    cleanup.SaveConfig(cfg);
                }
                if ((DateTime.UtcNow - lastPurge).TotalHours >= 24)
                {
                    usage.Purge(DateTime.UtcNow.AddDays(-90));
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Lỗi khi dọn rác theo lịch");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

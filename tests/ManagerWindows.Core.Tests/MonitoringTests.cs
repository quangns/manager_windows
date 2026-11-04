using ManagerWindows.Core.Monitoring;

namespace ManagerWindows.Core.Tests;

public class MonitoringTests
{
    private const string Win = @"C:\Windows";
    private static readonly DateTime T0 = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    private static ProcessSample S(int pid, string name, bool window = false, int session = 1, string? path = null, double cpu = 0, long ws = 0) =>
        new(pid, name, path ?? $@"C:\Program Files\{name}\{name}.exe", session, window, cpu, ws);

    [Fact]
    public void Groups_by_name_and_classifies()
    {
        var groups = ProcessGrouping.Group([
            S(1, "chrome", window: true, cpu: 5, ws: 100),
            S(2, "chrome", cpu: 10, ws: 300),
            S(3, "Chrome", cpu: 1, ws: 50),
            S(4, "OneDrive", cpu: 2, ws: 80),
            S(5, "svchost", session: 0, path: @"C:\Windows\System32\svchost.exe"),
            S(6, "RuntimeBroker", path: @"C:\Windows\System32\RuntimeBroker.exe"),
        ], currentSession: 1, Win);

        var chrome = groups.Single(g => g.Name.Equals("chrome", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, chrome.Count);
        Assert.Equal(16, chrome.CpuPercent);
        Assert.Equal(450, chrome.WorkingSetBytes);
        Assert.Equal(ProcessKind.App, chrome.Kind);
        Assert.Equal([1, 2, 3], chrome.Pids);
        Assert.Equal(ProcessKind.Background, groups.Single(g => g.Name == "OneDrive").Kind);
        Assert.Equal(ProcessKind.System, groups.Single(g => g.Name == "svchost").Kind);
        Assert.Equal(ProcessKind.System, groups.Single(g => g.Name == "RuntimeBroker").Kind);
    }

    private static AppGroup G(string name, double cpu, long ws = 0, ProcessKind kind = ProcessKind.Background) =>
        new(name, null, kind, 1, cpu, ws, [1]);

    [Fact]
    public void Alerts_only_after_sustained_usage_then_respects_cooldown()
    {
        var opt = new MonitorOptions { CpuPercent = 25, SustainSeconds = 60, CooldownMinutes = 30 };
        var t = new BackgroundAlertTracker();

        Assert.Empty(t.Update([G("Updater", 40)], T0, opt));
        Assert.Empty(t.Update([G("Updater", 40)], T0.AddSeconds(30), opt));
        var a = Assert.Single(t.Update([G("Updater", 40)], T0.AddSeconds(60), opt));
        Assert.Equal(AlertReason.Cpu, a.Reason);
        // Vẫn cao nhưng trong thời gian nghỉ: không báo lại.
        Assert.Empty(t.Update([G("Updater", 40)], T0.AddMinutes(10), opt));
        Assert.Single(t.Update([G("Updater", 40)], T0.AddMinutes(31), opt));
    }

    [Fact]
    public void Spike_that_drops_resets_the_timer()
    {
        var opt = new MonitorOptions { CpuPercent = 25, SustainSeconds = 60 };
        var t = new BackgroundAlertTracker();
        t.Update([G("Indexer", 80)], T0, opt);
        t.Update([G("Indexer", 2)], T0.AddSeconds(40), opt);   // hạ xuống
        Assert.Empty(t.Update([G("Indexer", 80)], T0.AddSeconds(70), opt)); // tính lại từ đầu
        Assert.Single(t.Update([G("Indexer", 80)], T0.AddSeconds(130), opt));
    }

    [Fact]
    public void Ignores_apps_with_windows_ignored_list_and_disabled_alerts()
    {
        var opt = new MonitorOptions { CpuPercent = 10, SustainSeconds = 0, Ignored = ["Backup"] };
        var t = new BackgroundAlertTracker();
        var alerts = t.Update([G("Excel", 90, kind: ProcessKind.App), G("Backup", 90), G("svchost", 90, kind: ProcessKind.System), G("Sync", 5, ws: 2000L << 20)], T0, opt);

        var only = Assert.Single(alerts);
        Assert.Equal("Sync", only.Name);
        Assert.Equal(AlertReason.Ram, only.Reason);
        Assert.Empty(new BackgroundAlertTracker().Update([G("Sync", 99)], T0, new MonitorOptions { AlertsEnabled = false, SustainSeconds = 0 }));
    }
}

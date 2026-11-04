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

    private static AppGroup A(string name, double cpu = 0, long ws = 0, double io = 0, ProcessKind kind = ProcessKind.App) =>
        new(name, null, kind, 1, cpu, ws, [1], io);

    [Fact]
    public void Lag_diagnosis_names_busiest_resource_and_its_top_apps()
    {
        var groups = new[]
        {
            A("svchost", io: 40e6, kind: ProcessKind.System),
            A("OneDrive", io: 8e6, kind: ProcessKind.Background),
            A("chrome", cpu: 30, ws: 3_000_000_000, io: 500),
            A("EXCEL", cpu: 60, ws: 1_000_000_000, io: 2e6),
            A("notepad", io: 2048),
        };

        var causes = LagDiagnosis.Analyze(cpuPercent: 92, diskBusyPercent: 97, ramUsedPercent: 70, groups, diskDetail: "C:");

        Assert.Equal([LagResource.Disk, LagResource.Cpu], causes.Select(c => c.Resource));
        var disk = causes[0];
        Assert.Equal("C:", disk.Detail);
        Assert.Equal(["svchost", "OneDrive", "EXCEL"], disk.TopApps.Select(a => a.Name)); // chrome < 1 KB/s bị bỏ
        Assert.Equal(["EXCEL", "chrome"], causes[1].TopApps.Select(a => a.Name));
    }

    [Fact]
    public void No_lag_causes_when_below_thresholds()
    {
        Assert.Empty(LagDiagnosis.Analyze(40, 20, 60, [A("chrome", cpu: 40)]));
        var mem = Assert.Single(LagDiagnosis.Analyze(10, 10, 95, [A("a", ws: 10), A("b", ws: 30)]));
        Assert.Equal(LagResource.Memory, mem.Resource);
        Assert.Equal("b", mem.TopApps[0].Name);
    }

    [Fact]
    public void Grouping_sums_io_rate()
    {
        var g = Assert.Single(ProcessGrouping.Group(
            [new ProcessSample(1, "x", null, 1, true, 0, 0, 1000), new ProcessSample(2, "x", null, 1, false, 0, 0, 500)], 1, Win));
        Assert.Equal(1500, g.IoBytesPerSec);
    }
}

public class RestartTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --profile-directory=\"Profile 1\"",
        "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "--profile-directory=\"Profile 1\"")]
    [InlineData("C:\\Windows\\system32\\notepad.exe C:\\a b.txt", "C:\\Windows\\system32\\notepad.exe", "C:\\a b.txt")]
    [InlineData("C:\\Program Files\\App X\\app.exe", "C:\\Program Files\\App X\\app.exe", "")]
    [InlineData("explorer.exe", "explorer.exe", "")]
    [InlineData("\"C:\\x\\EXCEL.EXE\"", "C:\\x\\EXCEL.EXE", "")]
    public void Splits_command_line(string cmd, string file, string args)
    {
        var r = RestartPlanner.Split(cmd);
        Assert.Equal(file, r.FileName);
        Assert.Equal(args, r.Arguments);
    }

    [Fact]
    public void Restarts_only_root_processes()
    {
        var chrome = "\"C:\\Chrome\\chrome.exe\"";
        var plan = RestartPlanner.Plan([
            new(100, 50, chrome, "C:\\Chrome\\chrome.exe"),                  // gốc (cha là explorer)
            new(101, 100, chrome + " --type=renderer", "C:\\Chrome\\chrome.exe"),
            new(102, 100, chrome + " --type=gpu-process", "C:\\Chrome\\chrome.exe"),
            new(103, 101, chrome + " --type=utility", "C:\\Chrome\\chrome.exe"),
        ]);
        var only = Assert.Single(plan);
        Assert.Equal("C:\\Chrome\\chrome.exe", only.FileName);
        Assert.Equal("", only.Arguments);
    }

    [Fact]
    public void Falls_back_to_executable_path_when_command_line_unknown()
    {
        var plan = RestartPlanner.Plan([new(7, 1, null, "C:\\App\\app.exe"), new(8, 2, "", null)]);
        Assert.Equal([new LaunchCommand("C:\\App\\app.exe", "")], plan);
    }
}

public class LeftoverTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc);
    private const int Me = 1, Self = 999;

    private static ProcInfo P(int pid, int parent, string name, double ageMin = 120, bool window = false, bool hung = false,
        int session = Me, double cpuSec = 60, long wsMb = 50, string? path = null) =>
        new(pid, parent, name, path ?? $@"C:\Apps\{name}.exe", session, Now.AddMinutes(-ageMin), window, hung,
            TimeSpan.FromSeconds(cpuSec), wsMb << 20);

    private static List<LeftoverGroup> Detect(params ProcInfo[] all) =>
        LeftoverDetector.Detect(all, Me, Self, Now, new LeftoverOptions(), @"C:\Windows");

    [Fact]
    public void Finds_orphans_and_groups_them()
    {
        var r = Detect(
            P(10, 1, "explorer", ageMin: 600, window: true),
            P(20, 10, "Code", window: true),
            P(21, 20, "node"),                 // cha (Code) còn sống → không phải mồ côi
            P(30, 777, "node", wsMb: 300),     // cha đã thoát
            P(31, 777, "node", wsMb: 200),
            P(40, 888, "conhost", path: @"C:\Windows\System32\conhost.exe"));

        var node = r.Single(g => g.Name == "node");
        Assert.Equal([30, 31], node.Pids);
        Assert.Equal(LeftoverReason.Orphan, node.Reasons);
        Assert.Equal(500L << 20, node.WorkingSetBytes);
        Assert.Contains(r, g => g.Name == "conhost");
        Assert.DoesNotContain(r, g => g.Name == "Code" || g.Name == "explorer");
    }

    [Fact]
    public void Detects_reused_parent_pid()
    {
        // PID 50 giờ là một tiến trình mới chạy 5 phút trước, còn con đã chạy 2 giờ → cha thật đã thoát.
        var r = Detect(P(50, 10, "notepad", ageMin: 5, window: true), P(60, 50, "helper", ageMin: 120));
        Assert.Equal("helper", Assert.Single(r).Name);
    }

    [Fact]
    public void Skips_young_windowed_protected_other_session_self_and_ignored()
    {
        var opt = new LeftoverOptions { Ignored = ["OneDrive"] };
        var r = LeftoverDetector.Detect([
            P(1, 500, "setup", ageMin: 3),                 // mới chạy
            P(2, 500, "Spotify", window: true),            // có cửa sổ
            P(3, 500, "svchost"),                          // được bảo vệ
            P(4, 500, "remote", session: 2),               // phiên khác
            P(Self, 500, "ManagerWindows.App"),            // chính app
            P(5, 500, "OneDrive"),                         // người dùng bỏ qua
            P(6, 0, "Idle-root"),                          // cha là 0: tiến trình gốc hệ thống
        ], Me, Self, Now, opt, @"C:\Windows");
        Assert.Empty(r);
    }

    [Fact]
    public void Flags_hung_windows_and_idle_background_hogs()
    {
        var r = Detect(
            P(10, 1, "explorer", ageMin: 600, window: true),
            P(70, 10, "EXCEL", window: true, hung: true, ageMin: 2),          // treo: báo cả khi mới chạy
            P(80, 10, "Updater", ageMin: 300, cpuSec: 1, wsMb: 400),          // 5 giờ, 1 giây CPU, 400 MB
            P(81, 10, "Busy", ageMin: 300, cpuSec: 3000, wsMb: 400),          // vẫn làm việc → không báo
            P(82, 10, "Small", ageMin: 300, cpuSec: 1, wsMb: 20),             // ít RAM → không báo
            P(83, 10, "SysHelper", ageMin: 300, cpuSec: 1, wsMb: 400, path: @"C:\Windows\System32\SysHelper.exe"));

        Assert.Equal("EXCEL", r[0].Name); // treo lên đầu
        Assert.Equal(LeftoverReason.NotResponding, r[0].Reasons);
        Assert.Equal(LeftoverReason.IdleBackground, r.Single(g => g.Name == "Updater").Reasons);
        Assert.DoesNotContain(r, g => g.Name is "Busy" or "Small" or "SysHelper");
    }
}

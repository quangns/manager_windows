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

    private static List<LeftoverGroup> DetectClosed(Dictionary<string, DateTime>? lastWindow, HashSet<string>? keep, params ProcInfo[] all) =>
        LeftoverDetector.Detect(all, Me, Self, Now, new LeftoverOptions(), @"C:\Windows", lastWindow, keep);

    /// <summary>Chrome đã đóng cửa sổ: 1 tiến trình chính + 6 tiến trình con nhỏ, không cái nào đủ 100 MB nhưng cộng lại nhiều.</summary>
    private static ProcInfo[] ChromeNoWindow(bool withWindow = false) =>
        [P(10, 0, "chrome", ageMin: 300, wsMb: 90, window: withWindow), .. Enumerable.Range(11, 6).Select(i => P(i, 10, "chrome", ageMin: 300, wsMb: 60))];

    [Fact]
    public void Browser_running_after_its_window_closed_is_reported_as_one_app()
    {
        var g = Assert.Single(DetectClosed(null, null, ChromeNoWindow()));
        Assert.Equal("chrome", g.Name);
        Assert.Equal(LeftoverReason.WindowClosed, g.Reasons);
        Assert.Equal(7, g.Pids.Count);                       // dừng cả tiến trình chính lẫn tiến trình con
        Assert.Equal(450L << 20, g.WorkingSetBytes);
        Assert.Empty(DetectClosed(null, null, ChromeNoWindow(withWindow: true))); // còn cửa sổ: đang dùng
    }

    [Fact]
    public void Any_app_seen_with_a_window_then_closed_is_reported_after_a_while()
    {
        ProcInfo[] app = [P(20, 0, "Code", wsMb: 300), P(21, 20, "Code", wsMb: 200)];
        Assert.Empty(DetectClosed(null, null, app));                                                   // chưa từng thấy cửa sổ, không phải trình duyệt
        Assert.Empty(DetectClosed(new() { ["Code"] = Now.AddMinutes(-2) }, null, app));               // vừa đóng 2 phút
        Assert.Equal(LeftoverReason.WindowClosed, Assert.Single(DetectClosed(new() { ["code"] = Now.AddMinutes(-30) }, null, app)).Reasons);
    }

    [Fact]
    public void Tray_apps_small_apps_and_windows_components_are_not_reported_as_closed()
    {
        var closed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            { ["Zalo"] = Now.AddHours(-1), ["Tiny"] = Now.AddHours(-1), ["SearchHost"] = Now.AddHours(-1) };
        var r = DetectClosed(closed, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Zalo" },
            P(30, 0, "Zalo", wsMb: 400),                                          // app chat ở khay: được phép chạy nền
            P(31, 0, "Tiny", wsMb: 40),                                           // ít RAM
            P(32, 0, "SearchHost", wsMb: 300, path: @"C:\Windows\SystemApps\x\SearchHost.exe"));
        Assert.Empty(r);
    }

    [Fact]
    public void Lingering_browser_that_just_started_is_left_alone()
    {
        ProcInfo[] fresh = [P(40, 0, "msedge", ageMin: 2, wsMb: 200)]; // vừa tự chạy lúc đăng nhập
        Assert.Empty(DetectClosed(null, null, fresh));
    }

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

public class HibernationTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private static HibernationCandidate C(string name, bool window = true, bool minimized = false, double cpu = 0, double io = 0,
        long wsMb = 300, bool audio = false, bool clip = false, bool fg = false) =>
        new(name, [1], window, minimized, cpu, io, wsMb << 20, audio, clip, fg);

    private static HibernationVerdict V(HibernationCandidate c, int idleMin = 30, HibernationOptions? opt = null, string[]? asleep = null) =>
        HibernationPlanner.Decide([c], _ => Now.AddMinutes(-idleMin), Now, opt ?? new HibernationOptions(),
            new HashSet<string>(asleep ?? [], StringComparer.OrdinalIgnoreCase)).Single().Verdict;

    [Fact]
    public void Idle_quiet_app_is_hibernated()
    {
        Assert.Equal(HibernationVerdict.Hibernate, V(C("EXCEL")));
        Assert.Equal(HibernationVerdict.InUse, V(C("EXCEL"), idleMin: 10));
        Assert.Equal(HibernationVerdict.InUse, V(C("EXCEL", fg: true)));
    }

    [Fact]
    public void Never_hibernates_unsafe_apps()
    {
        Assert.Equal(HibernationVerdict.Excluded, V(C("Zalo")));                    // sẽ không nhận tin nhắn
        Assert.Equal(HibernationVerdict.PlayingAudio, V(C("chrome", audio: true)));
        Assert.Equal(HibernationVerdict.OwnsClipboard, V(C("WINWORD", clip: true)));
        Assert.Equal(HibernationVerdict.Busy, V(C("Photoshop", cpu: 15)));
        Assert.Equal(HibernationVerdict.Busy, V(C("chrome", io: 5 * 1024 * 1024)));  // đang tải file
        Assert.Equal(HibernationVerdict.TooSmall, V(C("notepad", wsMb: 10)));
        Assert.Equal(HibernationVerdict.NoWindow, V(C("node", window: false)));
        Assert.Equal(HibernationVerdict.Protected, V(C("explorer")));
        Assert.Equal(HibernationVerdict.Protected, V(C("ManagerWindows.App")));
        Assert.Equal(HibernationVerdict.AlreadyAsleep, V(C("EXCEL"), asleep: ["excel"]));
    }

    [Fact]
    public void Only_minimized_option_and_custom_idle_time()
    {
        var opt = new HibernationOptions { OnlyMinimized = true, IdleMinutes = 5 };
        Assert.Equal(HibernationVerdict.NotMinimized, V(C("EXCEL"), idleMin: 6, opt: opt));
        Assert.Equal(HibernationVerdict.Hibernate, V(C("EXCEL", minimized: true), idleMin: 6, opt: opt));
    }

    [Fact]
    public void Collects_whole_process_tree()
    {
        var parent = new Dictionary<int, int> { [10] = 1, [11] = 10, [12] = 10, [13] = 12, [20] = 1, [4] = 0 };
        Assert.Equal(new HashSet<int> { 10, 11, 12, 13 }, HibernationPlanner.WithDescendants([10], parent));
    }

    [Fact]
    public void Activity_tracker_uses_start_time_for_unseen_apps()
    {
        var t = new AppActivityTracker(Now.AddHours(-1));
        t.MarkActive("chrome", Now);
        Assert.Equal(Now, t.LastActive("CHROME"));
        Assert.Equal(Now.AddHours(-1), t.LastActive("EXCEL"));
    }
}

public class HibernationDefaultsTests
{
    [Fact]
    public void Defaults_cover_chat_mail_remote_vpn_typing()
    {
        var o = new HibernationOptions();
        foreach (var n in new[] { "Zalo", "olk", "UltraViewer_Desktop", "AnyDesk", "DeskIn", "tailscale-ipn", "wireguard", "UniKeyNT", "EVKey64" })
            Assert.Contains(n, o.Excluded, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(o.Excluded.Count, o.Excluded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Upgrade_adds_new_defaults_once_and_keeps_user_removals()
    {
        // Cấu hình cũ: người dùng đã bỏ Zalo, thêm app riêng.
        var o = new HibernationOptions { Excluded = ["Telegram", "myapp"], DefaultsVersion = 0 };
        Assert.True(o.UpgradeDefaults());
        Assert.DoesNotContain("Zalo", o.Excluded);
        Assert.Contains("myapp", o.Excluded);
        Assert.Contains("tailscale-ipn", o.Excluded);
        Assert.Contains("UniKeyNT", o.Excluded);
        Assert.Equal(HibernationOptions.CurrentDefaultsVersion, o.DefaultsVersion);

        o.Excluded = o.Excluded.Where(n => n != "tailscale-ipn").ToList();
        Assert.False(o.UpgradeDefaults());
        Assert.DoesNotContain("tailscale-ipn", o.Excluded);
    }
}

public class HibernationConfigRoundTripTests
{
    [Fact]
    public void Removed_app_stays_removed_after_restart_and_no_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mw-cfg-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ManagerWindows.Core.Config.ConfigStore(path);
            var cfg = store.Load();
            Assert.Equal(cfg.Hibernation.Excluded.Count, cfg.Hibernation.Excluded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            cfg.Hibernation.Excluded = cfg.Hibernation.Excluded.Where(n => n != "Zalo" && n != "tailscale-ipn").ToList();
            store.Save(cfg);

            var again = store.Load();
            Assert.DoesNotContain("Zalo", again.Hibernation.Excluded);
            Assert.DoesNotContain("tailscale-ipn", again.Hibernation.Excluded);
            Assert.Equal(again.Hibernation.Excluded.Count, again.Hibernation.Excluded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally { File.Delete(path); }
    }
}

public class HibernationExcludedListTests
{
    [Fact]
    public void Normalizes_and_detects_existing_names()
    {
        var o = new HibernationOptions { Excluded = ["Zalo", "zalo.exe", " Chrome.EXE ", "", "chrome", "Telegram"] };
        Assert.True(o.NormalizeExcluded());
        Assert.Equal(["Zalo", "Chrome", "Telegram"], o.Excluded);
        Assert.True(o.IsExcluded("ZALO.exe"));
        Assert.True(o.IsExcluded("chrome"));
        Assert.False(o.IsExcluded("notepad"));
        Assert.False(o.NormalizeExcluded());
    }

    [Fact]
    public void Overlay_position_defaults_serialize()
    {
        // NaN từng làm hỏng việc lưu cả file cấu hình (System.Text.Json không ghi được NaN).
        var json = System.Text.Json.JsonSerializer.Serialize(new ManagerWindows.Core.Config.AppConfig(), ManagerWindows.Core.Cleaning.RuleLoader.JsonOptions);
        Assert.Contains("\"Overlay\"", json, StringComparison.OrdinalIgnoreCase);
    }
}

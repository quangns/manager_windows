using System.Diagnostics;
using System.Text.Json;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Core.Priority;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>App đang ngủ đông: các tiến trình đã tạm dừng (kèm thời điểm khởi động để không nhầm khi PID bị dùng lại).</summary>
public sealed record SleepingApp(string Name, List<SleepingProcess> Processes, DateTime SinceUtc, long RamBeforeBytes);

public sealed record SleepingProcess(int Pid, long StartTicks);

public enum ExcludeResult { Added, AlreadyListed, NotAllowed }

/// <summary>
/// Ngủ đông app không dùng: tạm dừng mọi tiến trình của app (kể cả tiến trình con) và trả RAM cho Windows; app giữ nguyên
/// trạng thái. Đánh thức ngay khi app được đưa lên trước, khôi phục từ thu nhỏ, hoặc có tiến trình mới cùng tên
/// (vd. mở file bằng app đó). Thoát app / khởi động lại thì đánh thức tất cả.
/// </summary>
public sealed class HibernationService : BackgroundLoop
{
    private readonly AppHost _host;
    private readonly AppActivityTracker _activity = new(DateTime.UtcNow);
    private readonly ProcessMonitorSampler _sampler = new();
    private readonly Dictionary<string, SleepingApp> _asleep = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly string _stateFile;
    private Timer? _wakeTimer;
    private ForegroundHook? _hook;

    public HibernationService(AppHost host)
    {
        _host = host;
        _stateFile = Path.Combine(host.DataDir, "hibernated.json");
    }

    public IReadOnlyList<HibernationDecision> LastDecisions { get; private set; } = [];
    public event Action? Changed;

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    public List<SleepingApp> Asleep()
    {
        lock (_gate) return _asleep.Values.ToList();
    }

    public bool IsAsleep(string name)
    {
        lock (_gate) return _asleep.ContainsKey(name);
    }

    public DateTime LastActive(string name) => _activity.LastActive(name);

    /// <summary>Gọi trên luồng UI: móc sự kiện cửa sổ trước, đánh thức các app còn ngủ từ lần chạy trước.</summary>
    public void Attach()
    {
        WakeLeftoversFromLastRun();
        _hook = new ForegroundHook(OnActivated);
        _wakeTimer = new Timer(_ => WakeCheck(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnActivated(int pid)
    {
        var name = NameOf(pid);
        if (name == null) return;
        _activity.MarkActive(name, DateTime.UtcNow);
        if (IsAsleep(name)) Task.Run(() => Wake(name, "foreground"));
    }

    /// <summary>
    /// Mỗi giây: cửa sổ trước thuộc app đang ngủ (phòng khi Windows không phát sự kiện), hoặc app có tiến trình mới
    /// (mở file bằng app đó → tiến trình mới chuyển yêu cầu sang tiến trình đang ngủ, phải đánh thức).
    /// </summary>
    private void WakeCheck()
    {
        try
        {
            List<SleepingApp> asleep;
            lock (_gate) asleep = _asleep.Values.ToList();
            if (asleep.Count == 0) return;
            var fg = WindowInfo.ForegroundPid();
            foreach (var app in asleep)
            {
                if (app.Processes.Any(p => p.Pid == fg)) { Wake(app.Name, "foreground"); continue; }
                var known = app.Processes.Select(p => p.Pid).ToHashSet();
                var procs = Process.GetProcessesByName(app.Name);
                var hasNew = procs.Any(p => !known.Contains(p.Id));
                var anyAlive = procs.Any(p => known.Contains(p.Id));
                foreach (var p in procs) p.Dispose();
                if (hasNew) Wake(app.Name, "new-process");
                else if (!anyAlive) Forget(app.Name); // app đã bị đóng (End task) trong lúc ngủ
            }
        }
        catch (Exception ex) { Log.Error("hibernation", ex); }
    }

    protected override void Tick(CancellationToken ct)
    {
        var opt = _host.Config.Hibernation;
        var fgPid = WindowInfo.ForegroundPid();
        if (NameOf(fgPid) is { } fgName) _activity.MarkActive(fgName, DateTime.UtcNow);

        if (!opt.Enabled)
        {
            if (Asleep().Count > 0) WakeAll("disabled");
            LastDecisions = [];
            return;
        }

        var samples = _sampler.Sample();
        var groups = ProcessGrouping.Group(samples, _session, _windowsDir).Where(g => g.Kind == ProcessKind.App).ToList();
        var parents = ProcessTree.ParentMap();
        var audio = AudioSessions.ActivePids();
        var clipboard = WindowInfo.ClipboardPids();
        var minimized = WindowInfo.AllMinimizedByPid();
        var sampleByPid = samples.ToDictionary(s => s.Pid);

        var candidates = groups.Select(g =>
        {
            var tree = HibernationPlanner.WithDescendants(g.Pids, parents)
                .Where(p => sampleByPid.TryGetValue(p, out var s) && s.SessionId == _session).ToHashSet();
            var windowPids = g.Pids.Where(minimized.ContainsKey).ToList();
            return new HibernationCandidate(g.Name, tree.ToList(), true,
                windowPids.Count > 0 && windowPids.All(p => minimized[p]),
                tree.Sum(p => sampleByPid[p].CpuPercent), tree.Sum(p => sampleByPid[p].IoBytesPerSec),
                tree.Sum(p => sampleByPid[p].WorkingSetBytes),
                tree.Overlaps(audio), tree.Overlaps(clipboard), tree.Contains(fgPid));
        }).ToList();

        HashSet<string> asleepNames;
        lock (_gate) asleepNames = _asleep.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decisions = HibernationPlanner.Decide(candidates, _activity.LastActive, DateTime.UtcNow, opt, asleepNames);
        LastDecisions = decisions;

        foreach (var d in decisions.Where(d => d.Verdict == HibernationVerdict.Hibernate))
        {
            var c = candidates.First(x => x.Name == d.Name);
            Hibernate(c.Name, c.Pids, c.WorkingSetBytes);
        }
        Changed?.Invoke();
    }

    /// <summary>Cho ngủ ngay một app (nút trên giao diện). Vẫn tôn trọng các điều kiện an toàn trừ thời gian không dùng.</summary>
    public HibernationVerdict HibernateNow(string name)
    {
        var d = LastDecisions.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (d != null && d.Verdict is not (HibernationVerdict.Hibernate or HibernationVerdict.InUse or HibernationVerdict.NotMinimized or HibernationVerdict.TooSmall))
            return d.Verdict;
        var procs = Process.GetProcessesByName(name);
        var roots = procs.Where(p => p.SessionId == _session).Select(p => p.Id).ToList();
        long ws = 0;
        foreach (var p in procs) { try { ws += p.WorkingSet64; } catch (InvalidOperationException) { } p.Dispose(); }
        var pids = HibernationPlanner.WithDescendants(roots, ProcessTree.ParentMap()).ToList();
        if (pids.Count == 0) return HibernationVerdict.NoWindow;
        Hibernate(name, pids, ws);
        Changed?.Invoke();
        return HibernationVerdict.Hibernate;
    }

    private void Hibernate(string name, IReadOnlyCollection<int> pids, long ramBefore)
    {
        var self = Environment.ProcessId;
        var done = new List<SleepingProcess>();
        foreach (var pid in pids)
        {
            if (pid == self) continue;
            string pname;
            long start;
            try
            {
                using var p = Process.GetProcessById(pid);
                pname = p.ProcessName;
                start = p.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception) { continue; } // đã thoát hoặc không đủ quyền
            if (ProtectedProcesses.IsProtected(pname)) continue;
            if (ProcessSuspender.Suspend(pid)) done.Add(new SleepingProcess(pid, start));
        }
        if (done.Count == 0) return;
        // Trả RAM sau khi đã tạm dừng hết: app không chạy nên không nạp lại ngay.
        foreach (var p in done) MemoryApi.TrimWorkingSet(p.Pid);
        lock (_gate) _asleep[name] = new SleepingApp(name, done, DateTime.UtcNow, ramBefore);
        Persist();
        Log.Info("hibernation", $"sleep {name}: {done.Count} tiến trình, {ramBefore / 1048576} MB");
    }

    /// <summary>App có được đưa vào danh sách không bao giờ ngủ không (chặn tiến trình hệ thống và chính app này).</summary>
    public static bool CanExclude(string name) =>
        !ProtectedProcesses.IsProtected(name) && !name.Equals("ManagerWindows.App", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Thêm app vào danh sách không bao giờ ngủ (lưu ngay) và đánh thức nếu nó đang ngủ.
    /// App đã có trong danh sách thì không thêm lần nữa.
    /// </summary>
    public ExcludeResult Exclude(string name)
    {
        name = HibernationOptions.NormalizeName(name);
        if (name.Length == 0 || !CanExclude(name)) return ExcludeResult.NotAllowed;
        var o = _host.Config.Hibernation;
        if (IsAsleep(name)) Wake(name, "excluded");
        if (o.IsExcluded(name)) return ExcludeResult.AlreadyListed;
        o.Excluded = [.. o.Excluded, name]; // danh sách mới: luồng nền đang đọc danh sách cũ
        _host.SaveConfig();
        ExcludedChanged?.Invoke();
        return ExcludeResult.Added;
    }

    /// <summary>Danh sách không bao giờ ngủ vừa đổi (tab Ngủ đông nạp lại).</summary>
    public event Action? ExcludedChanged;

    public void Wake(string name, string reason)
    {
        SleepingApp? app;
        lock (_gate)
        {
            if (!_asleep.Remove(name, out app)) return;
        }
        foreach (var p in app.Processes) ResumeIfSame(p);
        _activity.MarkActive(name, DateTime.UtcNow);
        Persist();
        Log.Info("hibernation", $"wake {name} ({reason})");
        Changed?.Invoke();
    }

    private void Forget(string name)
    {
        SleepingApp? app;
        lock (_gate)
        {
            if (!_asleep.Remove(name, out app)) return;
        }
        foreach (var p in app.Processes) ResumeIfSame(p); // tiến trình con còn sót (nếu có) chạy tiếp để tự thoát
        Persist();
        Log.Info("hibernation", $"{name} đã thoát trong lúc ngủ");
        Changed?.Invoke();
    }

    public void WakeAll(string reason)
    {
        foreach (var a in Asleep()) Wake(a.Name, reason);
    }

    /// <summary>Chỉ chạy tiếp đúng tiến trình đã tạm dừng (PID có thể đã bị một tiến trình khác dùng lại).</summary>
    private static void ResumeIfSame(SleepingProcess sp)
    {
        try
        {
            using var p = Process.GetProcessById(sp.Pid);
            if (p.StartTime.ToUniversalTime().Ticks == sp.StartTicks) ProcessSuspender.Resume(sp.Pid);
        }
        catch (Exception) { } // đã thoát
    }

    private void Persist()
    {
        try
        {
            List<SleepingProcess> all;
            lock (_gate) all = _asleep.Values.SelectMany(a => a.Processes).ToList();
            if (all.Count == 0) { File.Delete(_stateFile); return; }
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Error("hibernation", ex); }
    }

    /// <summary>Manager Windows bị tắt đột ngột khi còn app đang ngủ: lần chạy sau đánh thức lại hết.</summary>
    private void WakeLeftoversFromLastRun()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var list = JsonSerializer.Deserialize<List<SleepingProcess>>(File.ReadAllText(_stateFile)) ?? [];
            foreach (var p in list) ResumeIfSame(p);
            File.Delete(_stateFile);
            if (list.Count > 0) Log.Info("hibernation", $"đánh thức {list.Count} tiến trình còn ngủ từ lần chạy trước");
        }
        catch (Exception ex) { Log.Error("hibernation", ex); }
    }

    protected override void OnStop()
    {
        _wakeTimer?.Dispose();
        _hook?.Dispose();
        WakeAll("exit");
    }

    private static string? NameOf(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception) { return null; }
    }
}

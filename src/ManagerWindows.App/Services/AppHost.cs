using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Config;
using ManagerWindows.Core.Relocation;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;
using ManagerWindows.Storage;

namespace ManagerWindows.App.Services;

/// <summary>Nơi khởi tạo và giữ mọi thành phần dùng chung của app (chạy trong phiên người dùng).</summary>
public sealed class AppHost
{
    public static AppHost Current { get; private set; } = null!;

    public string DataDir { get; }
    public ConfigStore ConfigStore { get; }
    public AppConfig Config { get; private set; }
    public Database Db { get; }
    public CleanupRunRepository Runs { get; }
    public RelocationRepository Relocations { get; }
    public UsageRepository Usage { get; }
    public List<CleanupRule> Rules { get; }
    public List<AppProfile> Profiles { get; }
    public IpcClient Service { get; } = new();

    public UsageTracker UsageTracker { get; }
    public CleanupScheduler Scheduler { get; }
    public PriorityController Priority { get; }
    public RelocationService Relocation { get; }

    /// <summary>Hiện thông báo ở khay hệ thống (tiêu đề, nội dung, tab cần mở khi bấm vào).</summary>
    public event Action<string, string, string?>? Notify;
    public event Action? ConfigSaved;

    public AppHost()
    {
        Current = this;
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagerWindows");
        Directory.CreateDirectory(DataDir);
        ConfigStore = new ConfigStore(Path.Combine(DataDir, "config.json"));
        Config = ConfigStore.Load();
        Db = new Database(Path.Combine(DataDir, "data.db"));
        Runs = new CleanupRunRepository(Db);
        Relocations = new RelocationRepository(Db);
        Usage = new UsageRepository(Db);

        var rulesDir = Path.Combine(AppContext.BaseDirectory, "rules");
        Rules = RuleLoader.Load(Path.Combine(rulesDir, "cleanup-rules.json"));
        Profiles = AppProfile.Load(Path.Combine(rulesDir, "app-profiles.json"));

        UsageTracker = new UsageTracker(this);
        Scheduler = new CleanupScheduler(this);
        Priority = new PriorityController(this);
        Relocation = new RelocationService(this);
    }

    public RelocationEngine CreateEngine() =>
        new(new JunctionApi(), new WindowsVolumeApi(), new RobocopyCopier(), Relocations, SystemClock.Instance);

    public void SaveConfig()
    {
        ConfigStore.Save(Config);
        Autostart.Apply(Config.StartWithWindows);
        ConfigSaved?.Invoke();
    }

    public void RaiseNotify(string title, string message, string? tab = null) => Notify?.Invoke(title, message, tab);

    public void Start()
    {
        Autostart.Apply(Config.StartWithWindows);
        UsageTracker.Start();
        Scheduler.Start();
        Priority.Start();
        Relocation.StartHealthChecks();
    }

    public void Stop()
    {
        UsageTracker.Stop();
        Scheduler.Stop();
        Priority.Stop();
        Relocation.Stop();
    }
}

/// <summary>Tự chạy khi đăng nhập qua HKCU\...\Run (chỉ user hiện tại, không cần admin).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ManagerWindows";

    public static void Apply(bool enabled)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName);
    }
}

/// <summary>Vòng lặp nền có chu kỳ, bắt mọi lỗi để không làm sập app.</summary>
public abstract class BackgroundLoop
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    protected abstract TimeSpan Interval { get; }
    protected abstract void Tick(CancellationToken ct);
    protected virtual void OnStop() { }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _task = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try { Tick(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error(GetType().Name, ex); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(3000); } catch (AggregateException) { }
        try { OnStop(); } catch (Exception ex) { Log.Error(GetType().Name, ex); }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Error(string source, Exception ex) => Write("ERROR", source, ex.ToString());
    public static void Info(string source, string message) => Write("INFO", source, message);

    private static void Write(string level, string source, string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagerWindows", "logs");
            Directory.CreateDirectory(dir);
            lock (Gate)
                File.AppendAllText(Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss} [{level}] {source}: {message}{Environment.NewLine}");
        }
        catch { }
    }
}

using System.Diagnostics;
using ManagerWindows.Core.Excel;
using ManagerWindows.Core.Priority;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

public sealed record ManagedProcessView(int Pid, string Name, PriorityLevel Original, PriorityLevel Applied, string Reason);

public sealed record ExcelStatus(bool Running, bool Heavy, string? File, long? FileBytes, long WorkingSet, double Cpu);

/// <summary>
/// Mỗi 5 giây: chụp danh sách process → <see cref="PriorityPolicy"/> → áp dụng. Phát hiện Excel đang mở file nặng
/// để tăng ưu tiên cho Excel, hạ ưu tiên app nền và gợi ý cách xử lý file.
/// </summary>
public sealed class PriorityController : BackgroundLoop
{
    private readonly AppHost _host;
    private readonly ProcessSampler _sampler = new();
    private readonly PriorityApplier _applier;
    private readonly HashSet<int> _elevatedFailed = new();
    private readonly Queue<double> _excelCpu = new();
    private List<string> _topUsed = new();
    private DateTime _topUsedAt = DateTime.MinValue;
    private DateTime _excelFileAt = DateTime.MinValue;
    private string? _excelFile;
    private string? _notifiedFile;
    private bool _wasEnabled;
    private DateTime _serviceRetryAt = DateTime.MinValue;

    public PriorityController(AppHost host)
    {
        _host = host;
        _applier = new PriorityApplier(ElevatedSet);
    }

    public ExcelStatus Excel { get; private set; } = new(false, false, null, null, 0, 0);
    public IReadOnlyList<ManagedProcessView> Managed { get; private set; } = [];

    /// <summary>Excel vừa chuyển sang trạng thái nặng (đường dẫn file nếu đoán được).</summary>
    public event Action<string?>? ExcelHeavy;

    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    protected override void Tick(CancellationToken ct)
    {
        var opt = _host.Config.Priority;
        var (fgPid, _) = SystemInfo.ForegroundProcess();
        var snaps = _sampler.Sample(fgPid);

        var heavy = UpdateExcel(snaps);

        if (!opt.Enabled)
        {
            if (_wasEnabled) _applier.RestoreAll();
            _wasEnabled = false;
            Managed = [];
            return;
        }
        _wasEnabled = true;

        if ((DateTime.UtcNow - _topUsedAt).TotalMinutes >= 30)
        {
            _topUsed = _host.Usage.TopApps(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, 10, new Dictionary<string, int>())
                .Select(a => a.Name).ToList();
            _topUsedAt = DateTime.UtcNow;
        }

        if (_elevatedFailed.Count > 2000) _elevatedFailed.Clear(); // PID được dùng lại sau một thời gian
        var decisions = PriorityPolicy.Decide(snaps, _topUsed, opt, heavy);
        _applier.Apply(decisions, snaps);
        var reasons = decisions.ToDictionary(d => d.Pid, d => d.Reason);
        Managed = _applier.Managed.Select(kv => new ManagedProcessView(kv.Key, kv.Value.Name, kv.Value.Original, kv.Value.Applied,
            reasons.GetValueOrDefault(kv.Key, ""))).ToList();
    }

    private bool UpdateExcel(List<ProcessSnapshot> snaps)
    {
        var excel = snaps.Where(p => p.Name.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)).ToList();
        if (excel.Count == 0)
        {
            Excel = new ExcelStatus(false, false, null, null, 0, 0);
            _excelCpu.Clear();
            _notifiedFile = null;
            return false;
        }

        var ws = excel.Sum(p => p.WorkingSetBytes);
        _excelCpu.Enqueue(excel.Sum(p => p.CpuPercent));
        while (_excelCpu.Count > 6) _excelCpu.Dequeue(); // trung bình 30 giây
        var cpu = _excelCpu.Average();

        if ((DateTime.UtcNow - _excelFileAt).TotalSeconds >= 30)
        {
            _excelFile = null;
            foreach (var p in Process.GetProcessesByName("EXCEL"))
                using (p)
                    if (!string.IsNullOrEmpty(p.MainWindowTitle))
                        _excelFile ??= OfficeInfo.GuessOpenFile(p.MainWindowTitle);
            _excelFileAt = DateTime.UtcNow;
        }
        long? size = _excelFile != null && File.Exists(_excelFile) ? new FileInfo(_excelFile).Length : null;
        var heavy = ExcelHeavyDetector.IsHeavy(_host.Config.Excel, size, ws, cpu);
        Excel = new ExcelStatus(true, heavy, _excelFile, size, ws, cpu);

        var key = _excelFile ?? "(unknown)";
        if (heavy && _notifiedFile != key)
        {
            _notifiedFile = key;
            ExcelHeavy?.Invoke(_excelFile);
        }
        return heavy;
    }

    /// <summary>Không đủ quyền (process chạy admin) thì nhờ service; lỗi một lần thì không thử lại PID đó.</summary>
    private bool ElevatedSet(int pid, PriorityLevel level, bool eco)
    {
        if (_elevatedFailed.Contains(pid) || DateTime.UtcNow < _serviceRetryAt) return false;
        var r = _host.Service.SendAsync(IpcCommands.SetPriority, new SetPriorityRequest(pid, level, eco)).GetAwaiter().GetResult();
        if (!r.Ok) _elevatedFailed.Add(pid);
        // Không kết nối được service (bản portable/chưa cài): 10 phút sau mới thử lại.
        if (!r.Ok && r.Error?.StartsWith("Không kết nối") == true) _serviceRetryAt = DateTime.UtcNow.AddMinutes(10);
        return r.Ok;
    }

    protected override void OnStop() => _applier.RestoreAll();
}

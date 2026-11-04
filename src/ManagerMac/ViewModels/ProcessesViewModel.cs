using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerMac.Services;
using ManagerWindows.Core.Monitoring;

namespace ManagerMac.ViewModels;

public sealed partial class ProcessRow(string name) : ObservableObject
{
    public string Name { get; } = name;
    public IReadOnlyList<int> Pids { get; set; } = [];
    public string? Path { get; set; }
    public bool CanRestart => Kind == ProcessKind.App && Path?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true;
    public bool CanEnd => Kind != ProcessKind.System;

    [ObservableProperty] private ProcessKind _kind;
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private double _cpuRatio;
    [ObservableProperty] private long _ram;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private double _ramRatio;
}

public enum ProcessFilter { UserApps, App, Background, All }
public enum ProcessSort { Cpu, Ram, Name }

public sealed partial class ProcessesViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, ProcessRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _sampling;

    public ObservableCollection<ProcessRow> Items { get; } = new();

    public List<Option<ProcessFilter>> Filters { get; } =
    [
        new(ProcessFilter.UserApps, Loc.Instance["Proc_FilterUserApps"]),
        new(ProcessFilter.App, Loc.Instance["Proc_Kind_App"]),
        new(ProcessFilter.Background, Loc.Instance["Proc_Kind_Background"]),
        new(ProcessFilter.All, Loc.Instance["Proc_KindAll"]),
    ];

    public List<Option<ProcessSort>> Sorts { get; } =
    [
        new(ProcessSort.Cpu, "CPU"),
        new(ProcessSort.Ram, "RAM"),
        new(ProcessSort.Name, Loc.Instance["Col_App"]),
    ];

    [ObservableProperty] private Option<ProcessFilter> _filter;
    [ObservableProperty] private Option<ProcessSort> _sort;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private double _cpuTotal;
    [ObservableProperty] private string _cpuTotalText = "";
    [ObservableProperty] private double _ramUsed;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _processCount = "";

    public ProcessesViewModel()
    {
        _filter = Filters[0];
        _sort = Sorts[0];
        _timer.Tick += async (_, _) => await SampleAsync();
    }

    partial void OnFilterChanged(Option<ProcessFilter> value) => Reorder();
    partial void OnSortChanged(Option<ProcessSort> value) => Reorder();
    partial void OnSearchChanged(string value) => Reorder();

    public void Start()
    {
        _ = SampleAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var (groups, total, used) = await Task.Run(() => (MacPlatform.AppGroups(), MacPlatform.TotalMemory(), MacPlatform.UsedMemory()));
            Apply(groups);
            // %CPU của ps tính theo một nhân: chia số nhân để ra % toàn máy như Activity Monitor (chế độ tổng).
            CpuTotal = Math.Clamp(groups.Sum(g => g.CpuPercent) / Environment.ProcessorCount, 0, 100);
            CpuTotalText = $"{CpuTotal:0}%";
            RamUsed = total == 0 ? 0 : (double)used / total;
            RamText = $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}";
            ProcessCount = groups.Sum(g => g.Count).ToString("N0");
        }
        catch (Exception ex) { Log.Error("processes", ex); }
        finally { _sampling = false; }
    }

    private void Apply(List<AppGroup> groups)
    {
        var maxCpu = Math.Max(groups.Select(g => g.CpuPercent).DefaultIfEmpty(0).Max(), 1);
        var maxRam = Math.Max(groups.Select(g => g.WorkingSetBytes).DefaultIfEmpty(0).Max(), 1);
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            alive.Add(g.Name);
            if (!_rows.TryGetValue(g.Name, out var row)) _rows[g.Name] = row = new ProcessRow(g.Name);
            row.Pids = g.Pids;
            row.Path = g.Path;
            row.Kind = g.Kind;
            row.KindText = Loc.Instance["Proc_Kind_" + g.Kind];
            row.Count = g.Count;
            row.Cpu = g.CpuPercent;
            row.CpuText = g.CpuPercent < 0.1 ? "0%" : $"{g.CpuPercent:0.#}%";
            row.CpuRatio = g.CpuPercent / maxCpu;
            row.Ram = g.WorkingSetBytes;
            row.RamText = Fmt.Bytes(g.WorkingSetBytes);
            row.RamRatio = (double)g.WorkingSetBytes / maxRam;
        }
        foreach (var gone in _rows.Keys.Where(k => !alive.Contains(k)).ToList()) _rows.Remove(gone);
        Reorder();
    }

    /// <summary>Lọc + sắp xếp, giữ nguyên đối tượng dòng để không mất vị trí cuộn.</summary>
    private void Reorder()
    {
        IEnumerable<ProcessRow> q = _rows.Values.Where(r => Filter.Value switch
        {
            ProcessFilter.App => r.Kind == ProcessKind.App,
            ProcessFilter.Background => r.Kind == ProcessKind.Background,
            ProcessFilter.All => true,
            _ => r.Kind != ProcessKind.System,
        });
        if (Search.Length > 0) q = q.Where(r => r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        var desired = (Sort.Value switch
        {
            ProcessSort.Ram => q.OrderByDescending(r => r.Ram),
            ProcessSort.Name => q.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(r => r.Cpu),
        }).ToList();

        for (var i = Items.Count - 1; i >= 0; i--)
            if (!desired.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var cur = Items.IndexOf(desired[i]);
            if (cur < 0) Items.Insert(i, desired[i]);
            else if (cur != i) Items.Move(cur, i);
        }
    }

    /// <summary>End task: SIGTERM (cho app tự dọn), quá 5 giây mới SIGKILL.</summary>
    [RelayCommand]
    private Task EndTaskAsync(ProcessRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !row.CanEnd) return;
        if (!await Confirm(Loc.F("Proc_ConfirmEnd", ("app", row.Name), ("count", row.Count)))) { Status = ""; return; }
        var forced = await StopAsync(row.Pids, TimeSpan.FromSeconds(5));
        Status = Loc.F("Proc_Ended", ("app", row.Name), ("count", row.Count)) + (forced > 0 ? " " + Loc.F("Mac_Forced", ("count", forced)) : "");
        await SampleAsync();
    });

    /// <summary>Khởi động lại app: thoát như Cmd+Q (app có thể hỏi lưu), quá 10 giây mới buộc dừng, rồi mở lại gói .app.</summary>
    [RelayCommand]
    private Task RestartAsync(ProcessRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !row.CanRestart || row.Path == null) return;
        if (!await Confirm(Loc.F("Restart_Confirm", ("app", row.Name)))) { Status = ""; return; }
        Status = Loc.F("Restart_Working", ("app", row.Name));
        await Task.Run(() => MacPlatform.QuitApp(row.Name));
        var forced = await StopAsync(row.Pids, TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        MacPlatform.Open(row.Path);
        Status = Loc.F(forced > 0 ? "Restart_DoneForced" : "Restart_Done", ("app", row.Name), ("count", forced));
        await SampleAsync();
    });

    /// <summary>Chờ tiến trình tự thoát; còn sót thì SIGTERM, cuối cùng SIGKILL. Trả về số tiến trình phải buộc dừng.</summary>
    private static async Task<int> StopAsync(IReadOnlyList<int> pids, TimeSpan grace)
    {
        foreach (var pid in pids) MacPlatform.Signal(pid, MacPlatform.SIGTERM);
        var until = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < until && pids.Any(MacPlatform.Alive)) await Task.Delay(300);
        var forced = 0;
        foreach (var pid in pids.Where(MacPlatform.Alive))
            if (MacPlatform.Signal(pid, MacPlatform.SIGKILL)) forced++;
        return forced;
    }

    [RelayCommand]
    private static void Reveal(ProcessRow? row)
    {
        if (row?.Path != null) MacPlatform.Reveal(row.Path);
    }

    /// <summary>Xả bộ nhớ đệm bằng lệnh purge của macOS (hỏi mật khẩu quản trị).</summary>
    [RelayCommand]
    private Task PurgeAsync() => RunBusyAsync(async () =>
    {
        var before = MacPlatform.UsedMemory();
        await Task.Run(MacPlatform.Purge);
        await Task.Delay(1000);
        var after = MacPlatform.UsedMemory();
        Status = Loc.F("Mac_Purged", ("before", Fmt.Bytes(before)), ("after", Fmt.Bytes(after)));
        await SampleAsync();
    });
}

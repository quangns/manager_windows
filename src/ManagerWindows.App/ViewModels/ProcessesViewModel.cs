using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.App.Views;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Core.Priority;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một app trong bảng Tiến trình, cập nhật tại chỗ mỗi lần lấy mẫu để giữ vị trí cuộn và dòng đang chọn.</summary>
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(string name) => Name = name;

    public string Name { get; }
    public IReadOnlyList<int> Pids { get; set; } = [];
    public bool IsProtected => ProtectedProcesses.IsProtected(Name);
    public bool CanAct => !IsProtected;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private ProcessKind _kind;
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _cpuText = "";
    /// <summary>Độ dài thanh CPU (0–1) so với app dùng CPU nhiều nhất lúc này.</summary>
    [ObservableProperty] private double _cpuRatio;
    [ObservableProperty] private long _ram;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private double _ramRatio;
}

/// <summary>Bộ lọc loại: mặc định ẩn tiến trình hệ thống, chỉ hiện khi chọn "Tất cả".</summary>
public enum ProcessFilter { UserApps, App, Background, All }

public sealed partial class ProcessesViewModel : ViewModelBase
{
    private readonly ProcessMonitorSampler _sampler = new();
    private readonly CpuMeter _cpuMeter = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, ProcessRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private bool _sampling;

    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public ListCollectionView View { get; }

    public List<Option<ProcessFilter>> Kinds { get; } =
    [
        new(ProcessFilter.UserApps, Loc.Instance["Proc_FilterUserApps"]),
        new(ProcessFilter.App, Loc.Instance["Proc_Kind_App"]),
        new(ProcessFilter.Background, Loc.Instance["Proc_Kind_Background"]),
        new(ProcessFilter.All, Loc.Instance["Proc_KindAll"]),
    ];

    [ObservableProperty] private ProcessFilter _kindFilter = ProcessFilter.UserApps;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private double _cpuTotal;
    [ObservableProperty] private string _cpuTotalText = "";
    [ObservableProperty] private double _ramUsedRatio;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _processCount = "";
    [ObservableProperty] private string _backgroundCount = "";

    // Cài đặt cảnh báo app chạy ngầm
    [ObservableProperty] private bool _alertsEnabled;
    [ObservableProperty] private int _alertCpu;
    [ObservableProperty] private int _alertRamMb;
    [ObservableProperty] private int _alertSeconds;
    [ObservableProperty] private string _ignoredText;

    public ProcessesViewModel()
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ProcessRow r
            && Matches(r.Kind)
            && (Search.Length == 0
                || r.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        // Sắp xếp lại khi số liệu đổi (CPU/RAM thay đổi mỗi 2 giây).
        View.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRow.Cpu), nameof(ProcessRow.Ram), nameof(ProcessRow.Count), nameof(ProcessRow.DisplayName) })
            View.LiveSortingProperties.Add(p);
        View.IsLiveFiltering = true;
        View.LiveFilteringProperties.Add(nameof(ProcessRow.Kind));

        var m = Host.Config.Monitor;
        (_alertsEnabled, _alertCpu, _alertRamMb, _alertSeconds) = (m.AlertsEnabled, m.CpuPercent, m.RamMb, m.SustainSeconds);
        _ignoredText = TextList.Join(m.Ignored);

        _timer.Tick += async (_, _) => await SampleAsync();
    }

    private bool Matches(ProcessKind kind) => KindFilter switch
    {
        ProcessFilter.App => kind == ProcessKind.App,
        ProcessFilter.Background => kind == ProcessKind.Background,
        ProcessFilter.All => true,
        _ => kind != ProcessKind.System, // mặc định: ẩn tiến trình hệ thống
    };

    partial void OnKindFilterChanged(ProcessFilter value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    /// <summary>Bắt đầu lấy mẫu khi mở tab; dừng khi rời tab để không tốn CPU.</summary>
    public void Start(ProcessFilter? filter = null)
    {
        if (filter is { } f) KindFilter = f;
        _ = SampleAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private static bool WindowVisible() =>
        Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible && w.WindowState != WindowState.Minimized);

    private async Task SampleAsync()
    {
        if (_sampling || !WindowVisible()) return;
        _sampling = true;
        try
        {
            var (groups, cpu, mem) = await Task.Run(() =>
            {
                var g = ProcessGrouping.Group(_sampler.Sample(), _session, _windowsDir);
                return (g, _cpuMeter.Sample(), SystemInfo.Memory());
            });
            Apply(groups);
            CpuTotal = cpu;
            CpuTotalText = $"{cpu:0}%";
            var used = mem.TotalBytes - mem.AvailBytes;
            RamUsedRatio = mem.TotalBytes == 0 ? 0 : (double)used / mem.TotalBytes;
            RamText = Loc.F("Proc_RamUsed", ("used", Fmt.Bytes((long)used)), ("total", Fmt.Bytes((long)mem.TotalBytes)));
            ProcessCount = groups.Sum(x => x.Count).ToString("N0");
            BackgroundCount = groups.Count(x => x.Kind == ProcessKind.Background).ToString("N0");
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
            if (!_rows.TryGetValue(g.Name, out var row))
            {
                row = new ProcessRow(g.Name) { DisplayName = RunningApps.Describe(g.Name, g.Path) };
                _rows[g.Name] = row;
                Rows.Add(row);
            }
            row.Pids = g.Pids;
            row.Kind = g.Kind;
            row.KindText = Loc.Instance["Proc_Kind_" + g.Kind];
            row.Count = g.Count;
            row.Cpu = Math.Round(g.CpuPercent, 1);
            row.CpuText = g.CpuPercent < 0.1 ? "0%" : $"{g.CpuPercent:0.#}%";
            row.CpuRatio = g.CpuPercent / maxCpu;
            row.Ram = g.WorkingSetBytes;
            row.RamText = Fmt.Bytes(g.WorkingSetBytes);
            row.RamRatio = (double)g.WorkingSetBytes / maxRam;
        }
        foreach (var gone in _rows.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            Rows.Remove(_rows[gone]);
            _rows.Remove(gone);
        }
    }

    /// <summary>End task: đóng mọi tiến trình của app. Không cho đóng tiến trình hệ thống quan trọng.</summary>
    [RelayCommand]
    private Task EndTaskAsync(ProcessRow? row) => RunBusyAsync(async () =>
    {
        if (row == null) return;
        if (!row.CanAct || row.Name.Equals("ManagerWindows.App", StringComparison.OrdinalIgnoreCase))
        {
            Status = Loc.F("Proc_Protected", ("app", row.DisplayName));
            return;
        }
        if (!Confirm(Loc.F("Proc_ConfirmEnd", ("app", row.DisplayName), ("count", row.Count))))
        {
            Status = "";
            return;
        }

        int closed = 0, failed = 0;
        foreach (var pid in row.Pids.ToList())
        {
            var ok = await Task.Run(() =>
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                    return true;
                }
                catch (ArgumentException) { return true; } // đã thoát
                catch (Exception) { return false; }        // không đủ quyền: nhờ dịch vụ nền
            });
            if (!ok) ok = (await Host.Service.SendAsync(IpcCommands.KillProcess, new KillProcessRequest(pid))).Ok;
            if (ok) closed++; else failed++;
        }
        Status = failed == 0
            ? Loc.F("Proc_Ended", ("app", row.DisplayName), ("count", closed))
            : Loc.F("Proc_EndPartial", ("app", row.DisplayName), ("count", closed), ("failed", failed));
        Log.Info("processes", $"end task {row.Name}: {closed} ok, {failed} failed");
        await SampleAsync();
    });

    /// <summary>Giải phóng RAM một app: ép trả working set. App sẽ nạp lại phần cần khi dùng tiếp.</summary>
    [RelayCommand]
    private Task TrimAppAsync(ProcessRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !row.CanAct) return;
        var before = row.Ram;
        var pids = row.Pids.ToList();
        var (ok, failed) = await Task.Run(() =>
        {
            int o = 0, f = 0;
            foreach (var pid in pids) if (MemoryApi.TrimWorkingSet(pid)) o++; else f++;
            return (o, f);
        });
        await Task.Delay(500);
        await SampleAsync();
        var freed = Math.Max(0, before - row.Ram);
        Status = Loc.F(failed == 0 ? "Proc_TrimmedApp" : "Proc_TrimmedAppPartial",
            ("app", row.DisplayName), ("size", Fmt.Bytes(freed)), ("failed", failed));
    });

    /// <summary>
    /// Giải phóng RAM toàn máy: trim mọi app trong phiên (dịch vụ nền làm được cả app chạy quyền admin)
    /// và xả bộ nhớ đệm standby. Báo RAM trống trước/sau.
    /// </summary>
    [RelayCommand]
    private Task FreeMemoryAsync() => RunBusyAsync(async () =>
    {
        var before = SystemInfo.Memory().AvailBytes;
        var svc = await Host.Service.SendAsync(IpcCommands.TrimMemory);
        if (!svc.Ok)
        {
            // Không có dịch vụ nền (bản portable): tự trim các app của người dùng đủ quyền.
            await Task.Run(() => MemoryApi.TrimAll(p => p.SessionId == _session && !ProtectedProcesses.IsProtected(p.ProcessName)));
        }
        await Task.Delay(1000);
        var after = SystemInfo.Memory().AvailBytes;
        var freed = after > before ? (long)(after - before) : 0;
        var purged = svc.Ok && svc.Data is { } d
            && System.Text.Json.JsonSerializer.Deserialize<TrimMemoryResult>(d, IpcConstants.Json)?.StandbyPurged == true;
        Status = Loc.F("Proc_Freed", ("size", Fmt.Bytes(freed)), ("before", Fmt.Bytes((long)before)), ("after", Fmt.Bytes((long)after)))
                 + " " + Loc.Instance[purged ? "Proc_StandbyPurged" : "Proc_StandbySkipped"];
        await SampleAsync();
    });

    [RelayCommand]
    private void OpenLocation(ProcessRow? row)
    {
        if (row == null || row.Pids.Count == 0) return;
        var path = SystemInfo.ProcessPath(row.Pids[0]);
        if (path != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>Thêm app vào danh sách bỏ qua cảnh báo chạy ngầm.</summary>
    [RelayCommand]
    private void IgnoreAlerts(ProcessRow? row)
    {
        if (row == null) return;
        var m = Host.Config.Monitor;
        if (!m.Ignored.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
        {
            m.Ignored = [.. m.Ignored, row.Name];
            Host.SaveConfig();
        }
        IgnoredText = TextList.Join(m.Ignored);
        Status = Loc.F("Proc_Ignored", ("app", row.DisplayName));
    }

    [RelayCommand]
    private void SaveAlerts()
    {
        var m = Host.Config.Monitor;
        m.AlertsEnabled = AlertsEnabled;
        m.CpuPercent = Math.Clamp(AlertCpu, 1, 100);
        m.RamMb = Math.Max(100, AlertRamMb);
        m.SustainSeconds = Math.Clamp(AlertSeconds, 10, 3600);
        m.Ignored = TextList.Split(IgnoredText).Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToList();
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
    }
}

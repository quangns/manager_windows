using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Excel;
using ManagerWindows.Core.Priority;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một app đang được điều phối, gộp mọi tiến trình cùng tên.</summary>
public sealed record ManagedRow(string Name, int Processes, string Original, string Applied, string Reason);

/// <summary>Level: critical | warning | info, quyết định màu huy hiệu.</summary>
public sealed record SuggestionText(string Severity, string Text, string Level);

/// <summary>Một dòng trong danh sách chọn app: đổi lựa chọn là lưu và áp dụng ngay.</summary>
public sealed partial class AppChoiceRow : ObservableObject
{
    private readonly Action<AppChoiceRow> _changed;

    public AppChoiceRow(string name, string displayName, int processes, long workingSet, bool running, AppChoice choice,
        Action<AppChoiceRow> changed)
    {
        Name = name;
        DisplayName = displayName;
        Processes = processes;
        WorkingSet = workingSet;
        Running = running;
        _choice = choice;
        _changed = changed;
    }

    public string Name { get; }
    public string DisplayName { get; }
    public int Processes { get; }
    public long WorkingSet { get; }
    public bool Running { get; }
    public string ProcessesText => Running ? Processes.ToString() : "";
    public string RamText => Running ? Fmt.Bytes(WorkingSet) : Loc.Instance["Prio_NotRunning"];

    [ObservableProperty] private AppChoice _choice;

    private bool _silent;

    /// <summary>Cập nhật hiển thị theo cấu hình mà không lưu lại lần nữa.</summary>
    public void SetChoiceSilently(AppChoice choice)
    {
        _silent = true;
        Choice = choice;
        _silent = false;
    }

    partial void OnChoiceChanged(AppChoice value)
    {
        if (!_silent) _changed(this);
    }
}

public sealed partial class PriorityViewModel : ViewModelBase
{
    public ObservableCollection<ManagedRow> Managed { get; } = new();
    public ObservableCollection<SuggestionText> ExcelSuggestions { get; } = new();
    public ObservableCollection<AppChoiceRow> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    public List<Option<AppChoice>> Choices { get; } =
    [
        new(AppChoice.Normal, Loc.Instance["Choice_Normal"]),
        new(AppChoice.Priority, Loc.Instance["Choice_Priority"]),
        new(AppChoice.Background, Loc.Instance["Choice_Background"]),
    ];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _topUsedCount;
    [ObservableProperty] private bool _allowHigh;
    [ObservableProperty] private bool _excelEnabled;
    [ObservableProperty] private int _heavyFileMb;
    [ObservableProperty] private string _excelStatus = "";
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private string _reportSummary = "";
    [ObservableProperty] private string _appFilter = "";
    [ObservableProperty] private bool _onlyRunning = true;
    [ObservableProperty] private string _newAppName = "";
    [ObservableProperty] private string _chosenSummary = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public PriorityViewModel()
    {
        var p = Host.Config.Priority;
        _enabled = p.Enabled;
        _topUsedCount = p.TopUsedCount;
        _allowHigh = p.AllowHigh;
        _excelEnabled = Host.Config.Excel.Enabled;
        _heavyFileMb = Host.Config.Excel.HeavyFileMb;

        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = o => o is AppChoiceRow r
            && (!OnlyRunning || r.Running)
            && (AppFilter.Length == 0
                || r.DisplayName.Contains(AppFilter, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(AppFilter, StringComparison.OrdinalIgnoreCase));
        UpdateChosenSummary();

        // Ghim từ tab Thống kê: cập nhật lại lựa chọn trong danh sách.
        Host.ConfigSaved += () =>
        {
            foreach (var r in Apps) r.SetChoiceSilently(Host.Config.Priority.GetChoice(r.Name));
            UpdateChosenSummary();
        };
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
    }

    partial void OnAppFilterChanged(string value) => AppsView.Refresh();
    partial void OnOnlyRunningChanged(bool value) => AppsView.Refresh();

    /// <summary>Tải danh sách app đang chạy (gọi khi mở tab hoặc bấm Làm mới).</summary>
    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        var running = await Task.Run(RunningApps.Snapshot);
        var opt = Host.Config.Priority;
        Apps.Clear();
        foreach (var a in running)
            Apps.Add(new AppChoiceRow(a.Name, a.DisplayName, a.Processes, a.WorkingSetBytes, true, opt.GetChoice(a.Name), OnChoiceChanged));

        // App đã chọn nhưng đang không chạy vẫn hiện để bỏ chọn được (ẩn khi bật "Chỉ app đang chạy").
        var names = new HashSet<string>(running.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var n in opt.Pinned.Concat(opt.Background).Where(n => !names.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            Apps.Add(new AppChoiceRow(n, n, 0, 0, false, opt.GetChoice(n), OnChoiceChanged));
        UpdateChosenSummary();
    }

    private void OnChoiceChanged(AppChoiceRow row)
    {
        Host.Config.Priority.SetChoice(row.Name, row.Choice);
        Host.SaveConfig();
        UpdateChosenSummary();
        Status = Loc.F("Prio_ChoiceSaved", ("app", row.DisplayName), ("choice", Choices.First(c => c.Value == row.Choice).Label));
    }

    /// <summary>Ưu tiên một app theo tên process, kể cả khi app chưa mở (vd. EXCEL).</summary>
    [RelayCommand]
    private void AddApp()
    {
        var name = NewAppName.Trim();
        if (name.Length == 0) return;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var row = Apps.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            row = new AppChoiceRow(name, name, 0, 0, false, AppChoice.Normal, OnChoiceChanged);
            Apps.Add(row);
        }
        row.Choice = AppChoice.Priority; // gọi OnChoiceChanged → lưu
        NewAppName = "";
    }

    private void UpdateChosenSummary()
    {
        var p = Host.Config.Priority;
        ChosenSummary = Loc.F("Prio_ChosenSummary",
            ("priority", p.Pinned.Count == 0 ? "—" : string.Join(", ", p.Pinned)),
            ("background", p.Background.Count == 0 ? "—" : string.Join(", ", p.Background)));
    }

    private void RefreshLive()
    {
        // Gộp theo app: Chrome có thể vài chục tiến trình con, hiện một dòng.
        var groups = Host.Priority.Managed.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ManagedRow(g.Key, g.Count(),
                Loc.Instance["Level_" + g.First().Original],
                Loc.Instance["Level_" + g.Max(m => m.Applied)],
                string.Join(", ", g.Select(m => m.Reason).Where(r => r.Length > 0).Distinct().Select(r => Loc.Instance["Reason_" + r]))))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Managed.Clear();
        foreach (var r in groups) Managed.Add(r);

        var x = Host.Priority.Excel;
        ExcelStatus = !x.Running
            ? Loc.Instance["Excel_NotRunning"]
            : Loc.F(x.Heavy ? "Excel_Heavy" : "Excel_Normal",
                ("file", x.File ?? "?"), ("ram", Fmt.Bytes(x.WorkingSet)), ("cpu", x.Cpu.ToString("0")));
    }

    [RelayCommand]
    private void Save()
    {
        var p = Host.Config.Priority;
        p.Enabled = Enabled;
        p.TopUsedCount = Math.Clamp(TopUsedCount, 0, 10);
        p.AllowHigh = AllowHigh;
        Host.Config.Excel.Enabled = ExcelEnabled;
        Host.Config.Excel.HeavyFileMb = Math.Max(1, HeavyFileMb);
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
    }

    [RelayCommand]
    private Task PickAndAnalyzeAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm;*.xlsb;*.xls" };
        return dlg.ShowDialog() == true ? AnalyzeAsync(dlg.FileName) : Task.CompletedTask;
    }

    /// <summary>Phân tích file và hiện gợi ý (gọi cả khi phát hiện Excel nặng).</summary>
    public Task AnalyzeAsync(string path) => RunBusyAsync(async () =>
    {
        var report = await Task.Run(() => XlsxAnalyzer.Analyze(path, new ExcelEnvironment
        {
            Office64Bit = OfficeInfo.Is64Bit(),
            FreeMemoryPercent = SystemInfo.Memory().FreePercent,
        }));
        ReportTitle = Path.GetFileName(path);
        ReportSummary = report.Parsed
            ? Loc.F("Excel_Summary",
                ("size", Fmt.Bytes(report.FileBytes)), ("sheets", report.Sheets.Count),
                ("cells", report.Sheets.Sum(s => s.Cells).ToString("N0")),
                ("formulas", report.Sheets.Sum(s => s.Formulas).ToString("N0")),
                ("styles", report.CellStyles.ToString("N0")),
                ("media", Fmt.Bytes(report.MediaBytes)))
            : Loc.F("Excel_SummaryBinary", ("size", Fmt.Bytes(report.FileBytes)));
        ExcelSuggestions.Clear();
        foreach (var s in report.Suggestions.OrderByDescending(s => s.Severity))
            ExcelSuggestions.Add(new SuggestionText(Loc.Instance["Severity_" + s.Severity], Loc.F("ExcelS_" + s.Code, s.Args),
                s.Severity.ToString().ToLowerInvariant()));
        Status = "";
    });
}

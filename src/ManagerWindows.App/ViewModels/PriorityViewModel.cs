using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Excel;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

public sealed record ManagedRow(string Name, int Pid, string Original, string Applied);

public sealed record SuggestionText(string Severity, string Text, bool Critical);

public sealed partial class PriorityViewModel : ViewModelBase
{
    public ObservableCollection<ManagedRow> Managed { get; } = new();
    public ObservableCollection<SuggestionText> ExcelSuggestions { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _topUsedCount;
    [ObservableProperty] private bool _allowHigh;
    [ObservableProperty] private string _pinnedText;
    [ObservableProperty] private string _backgroundText;
    [ObservableProperty] private bool _excelEnabled;
    [ObservableProperty] private int _heavyFileMb;
    [ObservableProperty] private string _excelStatus = "";
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private string _reportSummary = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public PriorityViewModel()
    {
        var p = Host.Config.Priority;
        _enabled = p.Enabled;
        _topUsedCount = p.TopUsedCount;
        _allowHigh = p.AllowHigh;
        _pinnedText = TextList.Join(p.Pinned);
        _backgroundText = TextList.Join(p.Background);
        _excelEnabled = Host.Config.Excel.Enabled;
        _heavyFileMb = Host.Config.Excel.HeavyFileMb;
        Host.ConfigSaved += () => PinnedText = TextList.Join(Host.Config.Priority.Pinned); // ghim từ tab Thống kê
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
    }

    private void RefreshLive()
    {
        var current = Host.Priority.Managed;
        Managed.Clear();
        foreach (var m in current.OrderBy(m => m.Name))
            Managed.Add(new ManagedRow(m.Name, m.Pid, Loc.Instance["Level_" + m.Original], Loc.Instance["Level_" + m.Applied]));

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
        p.Pinned = TextList.Split(PinnedText).Select(StripExe).ToList();
        p.Background = TextList.Split(BackgroundText).Select(StripExe).ToList();
        Host.Config.Excel.Enabled = ExcelEnabled;
        Host.Config.Excel.HeavyFileMb = Math.Max(1, HeavyFileMb);
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
    }

    private static string StripExe(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

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
                s.Severity == Severity.Critical));
        Status = "";
    });
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Core.Scheduling;
using ManagerWindows.App.Views;
using ManagerWindows.Ipc;

namespace ManagerWindows.App.ViewModels;

public sealed partial class RuleRow(CleanupRule rule, bool isChecked) : ObservableObject
{
    public CleanupRule Rule { get; } = rule;
    public string Name => Rule.DisplayName(Loc.Instance.Lang);
    public string ScopeText => Loc.Instance[Rule.Scope == RuleScope.System ? "Scope_System" : "Scope_User"];
    public bool IsReview => Rule.Risk == RuleRisk.Review;
    public bool IsRecycleBin => Rule.Kind == RuleKind.RecycleBin;
    /// <summary>Mục hệ thống chỉ có số liệu ước tính từ service, không xem danh sách file.</summary>
    public string ReviewLabel => Loc.Instance[IsRecycleBin ? "Clean_OpenRecycleBin" : "Clean_Review"];

    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private string _estimate = "—";
    [ObservableProperty] private string _files = "";
    [ObservableProperty] private bool _canReview;
    [ObservableProperty] private bool _reviewed;
}

public sealed record HistoryRow(string Time, string Trigger, string Freed, int Deleted, int Failed);

public sealed partial class CleanupViewModel : ViewModelBase
{
    public ObservableCollection<RuleRow> Rules { get; } = new();
    public ObservableCollection<HistoryRow> History { get; } = new();

    public List<Option<Frequency>> Frequencies { get; } =
    [
        new(Frequency.Off, Loc.Instance["Freq_Off"]),
        new(Frequency.Daily, Loc.Instance["Freq_Daily"]),
        new(Frequency.Weekly, Loc.Instance["Freq_Weekly"]),
    ];

    public List<Option<DayOfWeek>> Days { get; } = Enum.GetValues<DayOfWeek>()
        .Select(d => new Option<DayOfWeek>(d, System.Globalization.CultureInfo.GetCultureInfo(Loc.Instance.Lang == "vi" ? "vi-VN" : "en-US").DateTimeFormat.GetDayName(d)))
        .ToList();

    public List<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] private Frequency _frequency;
    [ObservableProperty] private DayOfWeek _day;
    [ObservableProperty] private int _hour;
    [ObservableProperty] private int _idleMinutes;
    [ObservableProperty] private bool _skipOnBattery;

    private CleanupPlan? _userPlan;
    /// <summary>File người dùng bỏ tick khi xem lại: giữ lại, không xoá.</summary>
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public CleanupViewModel()
    {
        foreach (var r in Host.Rules) Rules.Add(new RuleRow(r, Host.Scheduler.IsEnabled(r)));
        var s = Host.Config.Cleanup.Schedule;
        (_frequency, _day, _hour, _idleMinutes, _skipOnBattery) = (s.Frequency, s.Day, s.Hour, s.RequireIdleMinutes, s.SkipOnBattery);
        LoadHistory();
    }

    private void LoadHistory()
    {
        History.Clear();
        foreach (var r in Host.Runs.Recent(15))
            History.Add(new HistoryRow(r.FinishedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                Loc.Instance["Trigger_" + r.Trigger], Fmt.Bytes(r.FreedBytes), r.Deleted, r.Failed));
    }

    /// <summary>Chạy thử: tính dung lượng từng luật, chưa xoá gì.</summary>
    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        var userRules = Rules.Where(r => r.Rule.Scope == RuleScope.User).Select(r => r.Rule).ToList();
        _userPlan = await Task.Run(() => Host.Scheduler.Plan(userRules));
        foreach (var rp in _userPlan.Rules)
        {
            var row = Rules.First(r => r.Rule.Id == rp.Rule.Id);
            // Danh sách file đã đổi: phải xem lại từ đầu.
            row.Reviewed = false;
            row.CanReview = row.IsRecycleBin || rp.Files.Count > 0;
            UpdateRowEstimate(row, rp);
        }

        var sys = await Host.Service.SendAsync(IpcCommands.PreviewSystemCleanup);
        var estimates = sys.Ok && sys.Data is { } d
            ? System.Text.Json.JsonSerializer.Deserialize<List<RuleEstimate>>(d, IpcConstants.Json) ?? []
            : [];
        foreach (var row in Rules.Where(r => r.Rule.Scope == RuleScope.System))
        {
            var e = estimates.FirstOrDefault(x => x.RuleId == row.Rule.Id);
            row.Estimate = e == null ? (sys.Ok ? "0 B" : "?") : Fmt.Bytes(e.Bytes);
            row.Files = e?.Files.ToString("N0") ?? "";
        }

        var total = _userPlan.Without(_excluded).TotalBytes + estimates.Sum(e => e.Bytes);
        Status = Loc.F("Clean_ScanResult", ("size", Fmt.Bytes(total)))
                 + (sys.Ok ? "" : " " + Loc.Instance["Clean_SystemNeedsService"]);
    });

    private void UpdateRowEstimate(RuleRow row, RulePlan rp)
    {
        if (rp.Rule.Kind == RuleKind.RecycleBin)
        {
            row.Estimate = Fmt.Bytes(rp.TotalBytes);
            row.Files = "";
            return;
        }
        var kept = rp.Files.Where(f => !_excluded.Contains(f.Path)).ToList();
        var skipped = rp.Files.Count - kept.Count;
        row.Estimate = Fmt.Bytes(kept.Sum(f => f.Size));
        row.Files = skipped == 0
            ? kept.Count.ToString("N0")
            : Loc.F("Clean_FilesKept", ("count", kept.Count.ToString("N0")), ("kept", skipped.ToString("N0")));
    }

    /// <summary>Xem danh sách file của một mục sau khi quét, bỏ tick file muốn giữ.</summary>
    [RelayCommand]
    private void Review(RuleRow? row)
    {
        if (row == null) return;
        if (row.IsRecycleBin)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = false });
            row.Reviewed = true;
            return;
        }
        var rp = _userPlan?.Rules.FirstOrDefault(r => r.Rule.Id == row.Rule.Id);
        if (rp == null) return;

        var vm = new ReviewViewModel(row.Name, row.IsReview, rp.Files, _excluded);
        var window = new ReviewWindow
        {
            DataContext = vm,
            Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible),
        };
        if (window.ShowDialog() != true) return;
        vm.ApplyTo(_excluded);
        row.Reviewed = true;
        UpdateRowEstimate(row, rp);
        Status = Loc.F("Clean_ScanResult", ("size", Fmt.Bytes(_userPlan!.Without(_excluded).TotalBytes)));
    }

    [RelayCommand]
    private Task RunAsync() => RunBusyAsync(async () =>
    {
        var checkedRows = Rules.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0) return;

        // Mục "nên xem lại" phải được xem danh sách file trước khi dọn.
        var notReviewed = checkedRows.Where(r => r.IsReview && !r.Reviewed && (r.CanReview || _userPlan == null)).ToList();
        if (notReviewed.Count > 0)
        {
            Status = "";
            Info(Loc.F("Clean_ReviewRequired", ("rules", string.Join("\n• ", notReviewed.Select(r => r.Name).Prepend("")))));
            return;
        }
        if (!Confirm(Loc.F("Clean_ConfirmRun", ("rules", string.Join("\n• ", checkedRows.Select(r => r.Name).Prepend(""))))))
        {
            Status = "";
            return;
        }

        var userRules = checkedRows.Where(r => r.Rule.Scope == RuleScope.User).Select(r => r.Rule).ToList();
        var result = await Task.Run(() =>
        {
            // Dùng lại kết quả chạy thử nếu có, nhưng chỉ các luật đã tick; executor sẽ kiểm tra lại từng file.
            var plan = (_userPlan ?? Host.Scheduler.Plan(userRules)).Without(_excluded);
            var filtered = new CleanupPlan();
            filtered.Rules.AddRange(plan.Rules.Where(rp => userRules.Any(u => u.Id == rp.Rule.Id)));
            return Host.Scheduler.Execute(filtered, "manual");
        });
        long freed = result.FreedBytes;
        int deleted = result.Deleted, failed = result.Failed;

        var systemIds = checkedRows.Where(r => r.Rule.Scope == RuleScope.System).Select(r => r.Rule.Id).ToList();
        var sysOk = true;
        if (systemIds.Count > 0)
        {
            var sys = await Host.Service.SendAsync(IpcCommands.RunSystemCleanup, new RunCleanupRequest(systemIds));
            sysOk = sys.Ok;
            if (sys.Ok && sys.Data is { } d && System.Text.Json.JsonSerializer.Deserialize<CleanupResultDto>(d, IpcConstants.Json) is { } dto)
            {
                freed += dto.FreedBytes;
                deleted += dto.Deleted;
                failed += dto.Failed;
            }
        }

        // Nhớ lựa chọn để lần dọn theo lịch dùng đúng các luật này.
        Host.Config.Cleanup.EnabledRuleIds = checkedRows.Select(r => r.Rule.Id).ToList();
        Host.SaveConfig();
        _userPlan = null;
        _excluded.Clear();
        foreach (var r in Rules) { r.Estimate = "—"; r.Files = ""; r.CanReview = false; r.Reviewed = false; }
        LoadHistory();
        Status = Loc.F("Clean_Result", ("size", Fmt.Bytes(freed)), ("deleted", deleted), ("failed", failed))
                 + (sysOk ? "" : " " + Loc.Instance["Clean_SystemNeedsService"]);
    });

    [RelayCommand]
    private void SaveSchedule()
    {
        var s = Host.Config.Cleanup.Schedule;
        (s.Frequency, s.Day, s.Hour, s.RequireIdleMinutes, s.SkipOnBattery) = (Frequency, Day, Hour, Math.Max(0, IdleMinutes), SkipOnBattery);
        Host.Config.Cleanup.EnabledRuleIds = Rules.Where(r => r.IsChecked).Select(r => r.Rule.Id).ToList();
        Host.SaveConfig();
        Status = Loc.Instance["Saved"];
    }
}

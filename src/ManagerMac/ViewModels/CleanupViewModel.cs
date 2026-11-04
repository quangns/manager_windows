using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerMac.Services;
using ManagerMac.Views;
using ManagerWindows.Core.Cleaning;

namespace ManagerMac.ViewModels;

public sealed partial class RuleRow(CleanupRule rule, bool isChecked) : ObservableObject
{
    public CleanupRule Rule { get; } = rule;
    public string Name => Rule.DisplayName(Loc.Instance.Lang);
    public bool IsReview => Rule.Risk == RuleRisk.Review;

    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private string _estimate = "—";
    [ObservableProperty] private string _files = "";
    [ObservableProperty] private bool _canReview;
    [ObservableProperty] private bool _reviewed;
}

public sealed record HistoryRow(string Time, string Freed, int Deleted, int Failed);

public sealed partial class CleanupViewModel : ViewModelBase
{
    public ObservableCollection<RuleRow> Rules { get; } = new();
    public ObservableCollection<HistoryRow> History { get; } = new();

    private CleanupPlan? _plan;
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);

    public CleanupViewModel()
    {
        foreach (var r in Host.Rules) Rules.Add(new RuleRow(r, Host.IsEnabled(r)));
        LoadHistory();
    }

    private void LoadHistory()
    {
        History.Clear();
        foreach (var r in Host.Runs.Recent(15))
            History.Add(new HistoryRow(r.FinishedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), Fmt.Bytes(r.FreedBytes), r.Deleted, r.Failed));
    }

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        _plan = await Task.Run(() => Host.Plan(Rules.Select(r => r.Rule)));
        foreach (var rp in _plan.Rules)
        {
            var row = Rules.First(r => r.Rule.Id == rp.Rule.Id);
            row.Reviewed = false;
            row.CanReview = rp.Files.Count > 0;
            UpdateRow(row, rp);
        }
        Status = Loc.F("Clean_ScanResult", ("size", Fmt.Bytes(_plan.Without(_excluded).TotalBytes)));
    });

    private void UpdateRow(RuleRow row, RulePlan rp)
    {
        var kept = rp.Files.Where(f => !_excluded.Contains(f.Path)).ToList();
        row.Estimate = Fmt.Bytes(kept.Sum(f => f.Size));
        var skipped = rp.Files.Count - kept.Count;
        row.Files = skipped == 0 ? kept.Count.ToString("N0")
            : Loc.F("Clean_FilesKept", ("count", kept.Count.ToString("N0")), ("kept", skipped.ToString("N0")));
    }

    [RelayCommand]
    private async Task ReviewAsync(RuleRow? row)
    {
        var rp = _plan?.Rules.FirstOrDefault(r => r.Rule.Id == row?.Rule.Id);
        if (row == null || rp == null) return;
        var vm = new ReviewViewModel(row.Name, row.IsReview, rp.Files, _excluded);
        var window = new ReviewWindow { DataContext = vm };
        var ok = Dialogs.Owner is { } owner ? await window.ShowDialog<bool>(owner) : false;
        if (!ok) return;
        vm.ApplyTo(_excluded);
        row.Reviewed = true;
        UpdateRow(row, rp);
        Status = Loc.F("Clean_ScanResult", ("size", Fmt.Bytes(_plan!.Without(_excluded).TotalBytes)));
    }

    [RelayCommand]
    private Task RunAsync() => RunBusyAsync(async () =>
    {
        var checkedRows = Rules.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0) { Status = ""; return; }
        var notReviewed = checkedRows.Where(r => r.IsReview && !r.Reviewed && (r.CanReview || _plan == null)).ToList();
        if (notReviewed.Count > 0)
        {
            Status = "";
            await Info(Loc.F("Clean_ReviewRequired", ("rules", string.Join("\n• ", notReviewed.Select(r => r.Name).Prepend("")))));
            return;
        }
        if (!await Confirm(Loc.F("Clean_ConfirmRun", ("rules", string.Join("\n• ", checkedRows.Select(r => r.Name).Prepend("")))))) { Status = ""; return; }

        var ids = checkedRows.Select(r => r.Rule.Id).ToHashSet();
        var result = await Task.Run(() =>
        {
            var plan = (_plan ?? Host.Plan(checkedRows.Select(r => r.Rule))).Without(_excluded);
            var filtered = new CleanupPlan();
            filtered.Rules.AddRange(plan.Rules.Where(rp => ids.Contains(rp.Rule.Id)));
            return Host.Execute(filtered, "manual");
        });
        Host.Config.Cleanup.EnabledRuleIds = ids.ToList();
        Host.SaveConfig();
        _plan = null;
        _excluded.Clear();
        foreach (var r in Rules) { r.Estimate = "—"; r.Files = ""; r.CanReview = false; r.Reviewed = false; }
        LoadHistory();
        Status = Loc.F("Clean_Result", ("size", Fmt.Bytes(result.FreedBytes)), ("deleted", result.Deleted), ("failed", result.Failed));
    });
}

public sealed partial class ReviewFileRow(CleanupCandidate c, bool include) : ObservableObject
{
    public CleanupCandidate Candidate { get; } = c;
    public string Path => Candidate.Path;
    public string RelativePath => System.IO.Path.GetRelativePath(Candidate.Root, Candidate.Path);
    public string SizeText => Fmt.Bytes(Candidate.Size);
    public string ModifiedText => Candidate.LastWriteUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    [ObservableProperty] private bool _include = include;
}

/// <summary>Danh sách file của một mục dọn; bỏ tick file nào thì giữ file đó.</summary>
public sealed partial class ReviewViewModel : ViewModelBase
{
    private readonly List<ReviewFileRow> _all;

    public string Title { get; }
    public bool ShowRiskNote { get; }
    public ObservableCollection<ReviewFileRow> Items { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";

    public ReviewViewModel(string ruleName, bool risk, IEnumerable<CleanupCandidate> files, IReadOnlySet<string> excluded)
    {
        Title = Loc.F("Review_Title", ("rule", ruleName));
        ShowRiskNote = risk;
        _all = files.OrderByDescending(f => f.Size).Select(f => new ReviewFileRow(f, !excluded.Contains(f.Path))).ToList();
        foreach (var r in _all) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReviewFileRow.Include)) UpdateSummary(); };
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var r in _all.Where(r => Filter.Length == 0 || r.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase)).Take(5000))
            Items.Add(r);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var inc = _all.Where(r => r.Include).ToList();
        Summary = Loc.F("Review_Summary", ("count", inc.Count.ToString("N0")), ("size", Fmt.Bytes(inc.Sum(r => r.Candidate.Size))),
            ("kept", (_all.Count - inc.Count).ToString("N0")), ("keptSize", Fmt.Bytes(_all.Where(r => !r.Include).Sum(r => r.Candidate.Size))));
    }

    [RelayCommand] private void IncludeVisible() { foreach (var r in Items) r.Include = true; }
    [RelayCommand] private void ExcludeVisible() { foreach (var r in Items) r.Include = false; }
    [RelayCommand] private static void Reveal(ReviewFileRow? row) { if (row != null) MacPlatform.Reveal(row.Path); }

    public void ApplyTo(HashSet<string> excluded)
    {
        foreach (var r in _all)
            if (r.Include) excluded.Remove(r.Path); else excluded.Add(r.Path);
    }
}

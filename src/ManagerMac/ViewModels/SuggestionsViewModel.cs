using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerMac.Services;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;

namespace ManagerMac.ViewModels;

public sealed partial class SuggestionRow(FileSuggestion s) : ObservableObject
{
    public FileSuggestion Suggestion { get; } = s;
    public string KindText => Loc.Instance["Kind_" + Suggestion.Kind];
    public string Path => Suggestion.Path;
    public string SizeText => Fmt.Bytes(Suggestion.Size);
    public string LastUsed => Suggestion.LastUsedUtc.ToLocalTime().ToString("dd/MM/yyyy");

    [ObservableProperty] private bool _isChecked;
}

public sealed partial class SuggestionsViewModel : ViewModelBase
{
    private List<SuggestionRow> _all = new();

    public ObservableCollection<SuggestionRow> Items { get; } = new();

    public List<Option<SuggestionKind?>> Kinds { get; } =
    [
        new(null, Loc.Instance["Kind_All"]),
        new(SuggestionKind.Stale, Loc.Instance["Kind_Stale"]),
        new(SuggestionKind.LargeFile, Loc.Instance["Kind_LargeFile"]),
        new(SuggestionKind.OldInstaller, Loc.Instance["Kind_OldInstaller"]),
        new(SuggestionKind.Duplicate, Loc.Instance["Kind_Duplicate"]),
    ];

    [ObservableProperty] private string _rootsText;
    [ObservableProperty] private Option<SuggestionKind?> _kind;
    [ObservableProperty] private string _selectedSummary = "";

    public SuggestionsViewModel()
    {
        _rootsText = TextList.Join(Host.Config.Cleanup.SuggestionRoots);
        _kind = Kinds[0];
    }

    partial void OnKindChanged(Option<SuggestionKind?> value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var r in _all.Where(r => Kind.Value == null || r.Suggestion.Kind == Kind.Value)) Items.Add(r);
    }

    private void UpdateSummary()
    {
        var sel = _all.Where(i => i.IsChecked).ToList();
        SelectedSummary = Loc.F("Sug_Selected", ("count", sel.Count), ("size", Fmt.Bytes(sel.Sum(s => s.Suggestion.Size))));
    }

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        var roots = TextList.Split(RootsText);
        Host.Config.Cleanup.SuggestionRoots = roots;
        Host.SaveConfig();
        var expanded = roots.Select(r => Environment.ExpandEnvironmentVariables(r).Replace("~/", MacPlatform.Home + "/")).ToList();
        var result = await Task.Run(() => new SuggestionScanner(SystemClock.Instance).Scan(expanded, Host.Config.Cleanup.Suggestions));
        _all = result.Take(5000).Select(s =>
        {
            var row = new SuggestionRow(s);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SuggestionRow.IsChecked)) UpdateSummary(); };
            return row;
        }).ToList();
        ApplyFilter();
        UpdateSummary();
        Status = Loc.F("Sug_Found", ("count", result.Count), ("size", Fmt.Bytes(result.Sum(r => r.Size))));
    });

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var r in Items) r.IsChecked = true;
    }

    [RelayCommand]
    private Task MoveToTrashAsync() => RunBusyAsync(async () =>
    {
        var sel = _all.Where(i => i.IsChecked).ToList();
        if (sel.Count == 0) { Status = ""; return; }
        if (!await Confirm(Loc.F("Mac_ConfirmTrash", ("count", sel.Count), ("size", Fmt.Bytes(sel.Sum(s => s.Suggestion.Size))))))
        {
            Status = "";
            return;
        }
        var trash = new MacTrashDeleter();
        var done = await Task.Run(() => sel.Where(r =>
        {
            try { trash.Recycle(r.Path); return true; }
            catch (Exception ex) { Log.Error("trash", ex); return false; }
        }).ToList());
        _all = _all.Except(done).ToList();
        ApplyFilter();
        UpdateSummary();
        Status = Loc.F("Mac_Trashed", ("count", done.Count), ("size", Fmt.Bytes(done.Sum(d => d.Suggestion.Size))));
    });

    [RelayCommand]
    private static void Reveal(SuggestionRow? row)
    {
        if (row != null) MacPlatform.Reveal(row.Path);
    }
}

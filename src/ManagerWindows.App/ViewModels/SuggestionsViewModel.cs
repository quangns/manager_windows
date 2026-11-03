using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

public sealed partial class SuggestionRow(FileSuggestion s) : ObservableObject
{
    public FileSuggestion Suggestion { get; } = s;
    public string KindText => Loc.Instance["Kind_" + Suggestion.Kind];
    public string Path => Suggestion.Path;
    public string SizeText => Fmt.Bytes(Suggestion.Size);
    public long Size => Suggestion.Size;
    public string LastUsed => Suggestion.LastUsedUtc.ToLocalTime().ToString("dd/MM/yyyy");

    [ObservableProperty] private bool _isChecked;
}

public sealed partial class SuggestionsViewModel : ViewModelBase
{
    public ObservableCollection<SuggestionRow> Items { get; } = new();
    public ICollectionView View { get; }

    public List<Option<SuggestionKind?>> Kinds { get; } =
    [
        new(null, Loc.Instance["Kind_All"]),
        new(SuggestionKind.Stale, Loc.Instance["Kind_Stale"]),
        new(SuggestionKind.LargeFile, Loc.Instance["Kind_LargeFile"]),
        new(SuggestionKind.OldInstaller, Loc.Instance["Kind_OldInstaller"]),
        new(SuggestionKind.Duplicate, Loc.Instance["Kind_Duplicate"]),
    ];

    [ObservableProperty] private string _rootsText;
    [ObservableProperty] private SuggestionKind? _kindFilter;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private bool _lastAccessOff;

    public SuggestionsViewModel()
    {
        _rootsText = TextList.Join(Host.Config.Cleanup.SuggestionRoots);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => KindFilter == null || ((SuggestionRow)o).Suggestion.Kind == KindFilter;
    }

    partial void OnKindFilterChanged(SuggestionKind? value) => View.Refresh();

    private void UpdateSummary()
    {
        var sel = Items.Where(i => i.IsChecked).ToList();
        SelectedSummary = Loc.F("Sug_Selected", ("count", sel.Count), ("size", Fmt.Bytes(sel.Sum(s => s.Size))));
    }

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        var roots = TextList.Split(RootsText);
        Host.Config.Cleanup.SuggestionRoots = roots;
        Host.SaveConfig();

        var opt = Host.Config.Cleanup.Suggestions;
        opt.LastAccessReliable = SystemInfo.LastAccessReliable();
        LastAccessOff = !opt.LastAccessReliable;
        var progress = new Progress<string>(p => Status = Loc.F("Sug_Scanning", ("path", p)));
        var expanded = roots.Select(Environment.ExpandEnvironmentVariables).ToList();
        var result = await Task.Run(() => new SuggestionScanner(SystemClock.Instance).Scan(expanded, opt, progress));

        foreach (var i in Items) i.PropertyChanged -= OnRowChanged;
        Items.Clear();
        foreach (var s in result.Take(5000))
        {
            var row = new SuggestionRow(s);
            row.PropertyChanged += OnRowChanged;
            Items.Add(row);
        }
        Status = Loc.F("Sug_Found", ("count", result.Count), ("size", Fmt.Bytes(result.Sum(r => r.Size))));
        UpdateSummary();
    });

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SuggestionRow.IsChecked)) UpdateSummary();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (SuggestionRow r in View) r.IsChecked = true;
    }

    [RelayCommand]
    private Task RecycleSelectedAsync() => RunBusyAsync(async () =>
    {
        var sel = Items.Where(i => i.IsChecked).ToList();
        if (sel.Count == 0) { Status = ""; return; }
        if (!Confirm(Loc.F("Sug_ConfirmRecycle", ("count", sel.Count), ("size", Fmt.Bytes(sel.Sum(s => s.Size))))))
        {
            Status = "";
            return;
        }
        var deleter = new RecycleBinDeleter();
        var done = await Task.Run(() =>
        {
            var ok = new List<SuggestionRow>();
            foreach (var r in sel)
                try { deleter.Recycle(r.Path); ok.Add(r); }
                catch (Exception ex) { Log.Error("recycle", ex); }
            return ok;
        });
        foreach (var r in done) { r.PropertyChanged -= OnRowChanged; Items.Remove(r); }
        Status = Loc.F("Sug_Recycled", ("count", done.Count), ("size", Fmt.Bytes(done.Sum(d => d.Size))));
        UpdateSummary();
    });

    [RelayCommand]
    private static void OpenFolder(SuggestionRow? row)
    {
        if (row == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = false });
    }
}

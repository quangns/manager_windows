using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Cleaning;

namespace ManagerWindows.App.ViewModels;

public sealed partial class ReviewFileRow(CleanupCandidate c, bool include) : ObservableObject
{
    public CleanupCandidate Candidate { get; } = c;
    public string Path => Candidate.Path;
    /// <summary>Đường dẫn tương đối so với thư mục gốc của luật cho dễ đọc; đường dẫn đầy đủ ở tooltip.</summary>
    public string RelativePath => System.IO.Path.GetRelativePath(Candidate.Root, Candidate.Path);
    public string Root => Candidate.Root;
    public long Size => Candidate.Size;
    public string SizeText => Fmt.Bytes(Candidate.Size);
    public DateTime Modified => Candidate.LastWriteUtc;
    public string ModifiedText => Candidate.LastWriteUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    [ObservableProperty] private bool _include = include;
}

/// <summary>
/// Danh sách file của một mục dọn để người dùng kiểm tra trước khi xoá. Chỉ khi bấm "Xong" lựa chọn mới
/// được áp dụng; "Huỷ" giữ nguyên lựa chọn cũ.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    public string Title { get; }
    public bool ShowRiskNote { get; }
    public ObservableCollection<ReviewFileRow> Items { get; }
    public ICollectionView View { get; }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";

    // Cộng dồn khi tick/bỏ tick để không phải duyệt lại cả danh sách (cache trình duyệt có thể vài chục nghìn file).
    private int _includeCount;
    private long _includeBytes;
    private readonly int _totalCount;
    private readonly long _totalBytes;

    public ReviewViewModel(string ruleName, bool isReviewRisk, IEnumerable<CleanupCandidate> files, IReadOnlySet<string> excluded)
    {
        Title = Loc.F("Review_Title", ("rule", ruleName));
        ShowRiskNote = isReviewRisk;
        Items = new ObservableCollection<ReviewFileRow>(
            files.OrderByDescending(f => f.Size).Select(f => new ReviewFileRow(f, !excluded.Contains(f.Path))));
        foreach (var row in Items)
        {
            row.PropertyChanged += OnRowChanged;
            _totalCount++;
            _totalBytes += row.Size;
            if (row.Include) { _includeCount++; _includeBytes += row.Size; }
        }
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => Filter.Length == 0 || ((ReviewFileRow)o).Path.Contains(Filter, StringComparison.OrdinalIgnoreCase);
        UpdateSummary();
    }

    partial void OnFilterChanged(string value) => View.Refresh();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ReviewFileRow.Include) || sender is not ReviewFileRow row) return;
        _includeCount += row.Include ? 1 : -1;
        _includeBytes += row.Include ? row.Size : -row.Size;
        UpdateSummary();
    }

    private void UpdateSummary() =>
        Summary = Loc.F("Review_Summary",
            ("count", _includeCount.ToString("N0")), ("size", Fmt.Bytes(_includeBytes)),
            ("kept", (_totalCount - _includeCount).ToString("N0")), ("keptSize", Fmt.Bytes(_totalBytes - _includeBytes)));

    [RelayCommand]
    private void IncludeVisible() => SetVisible(true);

    [RelayCommand]
    private void ExcludeVisible() => SetVisible(false);

    private void SetVisible(bool include)
    {
        foreach (ReviewFileRow r in View) r.Include = include;
    }

    [RelayCommand]
    private static void OpenFolder(ReviewFileRow? row)
    {
        if (row == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = false });
    }

    /// <summary>Ghi lựa chọn vào tập file được giữ lại.</summary>
    public void ApplyTo(HashSet<string> excluded)
    {
        foreach (var r in Items)
        {
            if (r.Include) excluded.Remove(r.Path);
            else excluded.Add(r.Path);
        }
    }
}

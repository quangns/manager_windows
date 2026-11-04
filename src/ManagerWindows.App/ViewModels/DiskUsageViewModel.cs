using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.DiskUsage;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một dòng của cây dung lượng (cây được "trải phẳng" vào DataGrid, thụt lề theo độ sâu).</summary>
public sealed partial class DiskRow : ObservableObject
{
    private DiskRow(DiskRow? parent, int depth) { ParentRow = parent; Depth = depth; }

    public static DiskRow ForDir(DirNode node, DiskRow? parent) => new(parent, parent == null ? 0 : parent.Depth + 1) { Node = node };
    public static DiskRow ForFile(FileEntry file, DiskRow parent) => new(parent, parent.Depth + 1) { File = file };
    public static DiskRow ForMore(int count, long size, DiskRow parent) => new(parent, parent.Depth + 1) { MoreCount = count, MoreSize = size };

    public DirNode? Node { get; private init; }
    public FileEntry? File { get; private init; }
    public DiskRow? ParentRow { get; }
    public int Depth { get; }
    private int MoreCount { get; init; }
    private long MoreSize { get; init; }

    public bool IsDir => Node != null;
    public bool IsFile => File != null;
    public bool IsMore => Node == null && File == null;
    public string FullPath => Node?.FullPath ?? File?.Path ?? "";
    public string Name => Node?.Name ?? (File != null ? Path.GetFileName(File.Path) : Loc.F("Disk_MoreFiles", ("count", MoreCount)));
    public long Size => Node?.Size ?? File?.Size ?? MoreSize;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public bool CanExpand => Node is { IsLink: false } n && (n.Children.Count > 0 || n.DirectFileCount > 0);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chevron))] private bool _isExpanded;
    public string Chevron => !CanExpand ? "" : IsExpanded ? "\uE70D" : "\uE76C";
    public string Glyph => Node is { IsLink: true } ? "\uE71B" : Node is { AccessDenied: true } ? "\uE72E" : IsDir ? "\uE8B7" : IsMore ? "\uE712" : "\uE8A5";
    public string? Note => Node switch
    {
        { IsLink: true } => Loc.Instance["Disk_Link"],
        { AccessDenied: true } => Loc.Instance["Disk_Denied"],
        _ => null,
    };

    /// <summary>Tỉ lệ so với thư mục cha (thanh ngang kiểu TreeSize).</summary>
    public double Ratio => ParentRow is { Size: > 0 } p ? (double)Size / p.Size : ParentRow == null ? 1 : 0;
    public string PercentText => $"{Ratio * 100:0.0} %";
    public string SizeText => Fmt.Bytes(Size);
    public string FilesText => Node != null ? Node.FileCount.ToString("N0") : "";
    public string DirsText => Node != null ? Node.DirCount.ToString("N0") : "";
    public string LastWriteText
    {
        get
        {
            var t = Node?.LastWriteUtc ?? File?.LastWriteUtc ?? DateTime.MinValue;
            return t > DateTime.MinValue ? t.ToLocalTime().ToString("dd/MM/yyyy") : "";
        }
    }

    /// <summary>Số liệu đổi (sau khi xoá một mục bên trong): vẽ lại cả dòng.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class TopFileRow(FileEntry f, long largest)
{
    public FileEntry Entry { get; } = f;
    public string Path => Entry.Path;
    public string Name => System.IO.Path.GetFileName(Entry.Path);
    public string Folder => System.IO.Path.GetDirectoryName(Entry.Path) ?? "";
    public string SizeText => Fmt.Bytes(Entry.Size);
    public double Ratio => largest > 0 ? (double)Entry.Size / largest : 0;
    public string LastWriteText => Entry.LastWriteUtc.ToLocalTime().ToString("dd/MM/yyyy");
}

/// <summary>Tab "Dung lượng": quét ổ/thư mục, cây thư mục xếp từ lớn đến bé (kiểu TreeSize) và danh sách file lớn nhất.</summary>
public sealed partial class DiskUsageViewModel : ViewModelBase
{
    /// <summary>Số file tối đa hiện khi mở một thư mục; phần còn lại gộp một dòng.</summary>
    private const int FilesPerFolder = 200;
    private const int TopFileCount = 500;

    public ObservableCollection<DiskRow> Rows { get; } = new();
    public ObservableCollection<TopFileRow> TopFiles { get; } = new();
    public ObservableCollection<Option<string>> Drives { get; } = new();

    [ObservableProperty] private string _rootPath = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    [ObservableProperty] private string? _selectedDrive;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TreeVisible), nameof(FilesVisible))] private bool _showTopFiles;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TreeVisible), nameof(FilesVisible))] private bool _hasResult;
    public bool TreeVisible => HasResult && !ShowTopFiles;
    public bool FilesVisible => HasResult && ShowTopFiles;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _filesText = "";
    [ObservableProperty] private string _dirsText = "";
    [ObservableProperty] private string _errorsText = "";
    [ObservableProperty] private bool _hasErrors;
    [ObservableProperty] private string _elapsedText = "";
    /// <summary>Gốc là cả ổ: hiện thanh đã dùng / dung lượng ổ.</summary>
    [ObservableProperty] private string _driveText = "";
    [ObservableProperty] private double _driveRatio;
    [ObservableProperty] private bool _isDriveRoot;

    private CancellationTokenSource? _cts;
    private DiskUsageResult? _result;

    public void LoadDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
        {
            var used = d.TotalSize - d.TotalFreeSpace;
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" {d.VolumeLabel}";
            Drives.Add(new(d.RootDirectory.FullName, $"{d.Name}{label} — {Fmt.Bytes(used)} / {Fmt.Bytes(d.TotalSize)}"));
        }
    }

    partial void OnSelectedDriveChanged(string? value)
    {
        if (value != null) RootPath = value;
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(RootPath) ? RootPath : null };
        if (dlg.ShowDialog() == true) RootPath = dlg.FolderName;
    }

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        var root = Environment.ExpandEnvironmentVariables(RootPath.Trim().Trim('"'));
        if (!Directory.Exists(root))
        {
            Status = Loc.F("Disk_NotFound", ("path", root));
            return;
        }
        _cts = new CancellationTokenSource();
        var progress = new Progress<DiskUsageProgress>(p => Status = Loc.F("Disk_Scanning",
            ("files", p.Files.ToString("N0")), ("size", Fmt.Bytes(p.Bytes)), ("path", p.CurrentPath)));
        try
        {
            var token = _cts.Token;
            _result = await Task.Run(() => DiskUsageScanner.Scan(root, progress, token, TopFileCount));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
        Show(_result);
        Status = Loc.F(_result.Cancelled ? "Disk_Stopped" : "Disk_Done",
            ("size", Fmt.Bytes(_result.Root.Size)), ("time", ElapsedText));
    });

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private void Show(DiskUsageResult r)
    {
        Rows.Clear();
        var rootRow = DiskRow.ForDir(r.Root, null);
        Rows.Add(rootRow);
        Expand(rootRow);

        TopFiles.Clear();
        var largest = r.TopFiles.Count > 0 ? r.TopFiles[0].Size : 0;
        foreach (var f in r.TopFiles) TopFiles.Add(new TopFileRow(f, largest));

        ElapsedText = r.Elapsed.TotalSeconds < 60 ? $"{r.Elapsed.TotalSeconds:0.0} s" : Fmt.Duration(r.Elapsed.TotalSeconds);
        ErrorsText = Loc.F("Disk_Errors", ("count", r.Errors));
        HasErrors = r.Errors > 0;
        UpdateTotals();

        var drive = Path.GetPathRoot(r.Root.FullPath);
        IsDriveRoot = drive != null && string.Equals(drive.TrimEnd('\\', '/'), r.Root.FullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        if (IsDriveRoot)
        {
            try
            {
                var d = new DriveInfo(drive!);
                var used = d.TotalSize - d.TotalFreeSpace;
                DriveRatio = d.TotalSize > 0 ? (double)used / d.TotalSize : 0;
                DriveText = Loc.F("Disk_DriveUsed", ("used", Fmt.Bytes(used)), ("total", Fmt.Bytes(d.TotalSize)),
                    ("free", Fmt.Bytes(d.TotalFreeSpace)), ("hidden", Fmt.Bytes(Math.Max(0, used - r.Root.Size))));
            }
            catch (IOException) { IsDriveRoot = false; }
        }
        HasResult = true;
    }

    private void UpdateTotals()
    {
        if (_result == null) return;
        TotalText = Fmt.Bytes(_result.Root.Size);
        FilesText = _result.Root.FileCount.ToString("N0");
        DirsText = _result.Root.DirCount.ToString("N0");
    }

    [RelayCommand]
    private void Toggle(DiskRow? row)
    {
        if (row == null || !row.CanExpand) return;
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    private void Expand(DiskRow row)
    {
        if (row.Node == null || row.IsExpanded) return;
        var node = row.Node;
        var items = new List<DiskRow>(node.Children.Select(c => DiskRow.ForDir(c, row)));
        if (node.DirectFileCount > 0)
        {
            var files = DiskUsageScanner.ListFiles(node.FullPath);
            items.AddRange(files.Take(FilesPerFolder).Select(f => DiskRow.ForFile(f, row)));
            if (files.Count > FilesPerFolder)
                items.Add(DiskRow.ForMore(files.Count - FilesPerFolder, files.Skip(FilesPerFolder).Sum(f => f.Size), row));
        }
        // Thư mục và file trộn chung, lớn đến bé; dòng "còn N file nhỏ" luôn ở cuối.
        items = items.OrderBy(i => i.IsMore).ThenByDescending(i => i.Size).ToList();
        var at = Rows.IndexOf(row) + 1;
        foreach (var i in items) Rows.Insert(at++, i);
        row.IsExpanded = true;
    }

    private void Collapse(DiskRow row)
    {
        var at = Rows.IndexOf(row) + 1;
        while (at < Rows.Count && Rows[at].Depth > row.Depth) Rows.RemoveAt(at);
        row.IsExpanded = false;
    }

    [RelayCommand]
    private void ShowTree() => ShowTopFiles = false;

    [RelayCommand]
    private void ShowFiles() => ShowTopFiles = true;

    [RelayCommand]
    private static void OpenInExplorer(object? item)
    {
        var (path, isFile) = item switch
        {
            DiskRow { IsMore: false } r => (r.FullPath, r.IsFile),
            DiskRow { ParentRow: { } p } => (p.FullPath, false),
            TopFileRow t => (t.Path, true),
            _ => ("", false),
        };
        if (path.Length == 0) return;
        var args = isFile ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false });
    }

    [RelayCommand]
    private void CopyPath(object? item)
    {
        var path = item switch { DiskRow r => r.FullPath, TopFileRow t => t.Path, _ => "" };
        if (path.Length == 0) return;
        try { Clipboard.SetText(path); Status = Loc.F("Disk_Copied", ("path", path)); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard đang bị app khác giữ
    }

    /// <summary>Quét riêng thư mục này (đặt làm gốc).</summary>
    [RelayCommand]
    private async Task ScanHereAsync(DiskRow? row)
    {
        if (row is not { IsDir: true }) return;
        RootPath = row.FullPath;
        await ScanAsync();
    }

    /// <summary>Không cho xoá gốc ổ, thư mục hệ thống/chương trình, thư mục người dùng, và junction (xoá junction qua Thùng rác có thể xoá luôn data thật).</summary>
    private static bool IsProtectedPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase))
            return true;
        string[] exact =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        if (exact.Any(e => e.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(e), full, StringComparison.OrdinalIgnoreCase)))
            return true;
        string[] under =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        return under.Any(u => u.Length > 0 &&
            (string.Equals(u, full, StringComparison.OrdinalIgnoreCase) ||
             full.StartsWith(Path.TrimEndingDirectorySeparator(u) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private Task RecycleAsync(object? item) => RunBusyAsync(async () =>
    {
        var (path, size, row) = item switch
        {
            DiskRow { IsMore: false, ParentRow: not null } r => (r.FullPath, r.Size, r),
            TopFileRow t => (t.Path, t.Entry.Size, (DiskRow?)null),
            _ => ("", 0L, (DiskRow?)null),
        };
        Status = "";
        if (path.Length == 0) return;
        if (row?.Node is { IsLink: true } || IsProtectedPath(path))
        {
            Status = Loc.F("Disk_Protected", ("path", path));
            return;
        }
        if (!Confirm(Loc.F("Disk_ConfirmRecycle", ("path", path), ("size", Fmt.Bytes(size))))) return;

        await Task.Run(() => new RecycleBinDeleter().Recycle(path));
        ForgetPath(path, size, row);
        Status = Loc.F("Disk_Recycled", ("path", path), ("size", Fmt.Bytes(size)));
    });

    /// <summary>Gỡ mục vừa xoá khỏi cây và danh sách file lớn, trừ dung lượng lên tới gốc.</summary>
    private void ForgetPath(string path, long size, DiskRow? row)
    {
        if (_result == null) return;
        row ??= Rows.FirstOrDefault(r => r.IsFile && string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (row?.Node is { } node)
        {
            if (row.IsExpanded) Collapse(row);
            node.Parent?.Remove(node);
            Rows.Remove(row);
            TopFiles.Where(t => t.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList()
                .ForEach(t => TopFiles.Remove(t));
        }
        else
        {
            if (row != null) Rows.Remove(row);
            FindDir(_result.Root, Path.GetDirectoryName(path))?.RemoveFile(size);
            if (TopFiles.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)) is { } top)
                TopFiles.Remove(top);
        }
        foreach (var r in Rows) r.Refresh();
        UpdateTotals();
    }

    private static DirNode? FindDir(DirNode root, string? dir)
    {
        if (dir == null) return null;
        var n = root;
        while (!string.Equals(n.FullPath, dir, StringComparison.OrdinalIgnoreCase))
        {
            n = n.Children.FirstOrDefault(c => dir.StartsWith(c.FullPath, StringComparison.OrdinalIgnoreCase)
                && (dir.Length == c.FullPath.Length || dir[c.FullPath.Length] == Path.DirectorySeparatorChar));
            if (n == null) return null;
        }
        return n;
    }
}

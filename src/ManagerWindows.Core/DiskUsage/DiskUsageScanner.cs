using System.IO.Enumeration;

namespace ManagerWindows.Core.DiskUsage;

/// <summary>Một thư mục trong cây dung lượng. Chỉ lưu thư mục (file đọc lại khi mở rộng) để quét cả ổ vẫn nhẹ RAM.</summary>
public sealed class DirNode(string name, string fullPath, DirNode? parent)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public DirNode? Parent { get; } = parent;

    /// <summary>Tổng dung lượng cả cây con (byte, theo kích thước file).</summary>
    public long Size { get; internal set; }
    /// <summary>Dung lượng các file nằm trực tiếp trong thư mục.</summary>
    public long FilesSize { get; internal set; }
    public int DirectFileCount { get; internal set; }
    /// <summary>Số file / thư mục con của cả cây con.</summary>
    public long FileCount { get; internal set; }
    public long DirCount { get; internal set; }
    /// <summary>Lần sửa mới nhất của file bất kỳ trong cây con.</summary>
    public DateTime LastWriteUtc { get; internal set; }
    /// <summary>Junction/symlink: không đi vào (tránh đếm trùng, vd. data đã chuyển ổ bằng tab Di chuyển data).</summary>
    public bool IsLink { get; internal set; }
    /// <summary>Không có quyền đọc thư mục này.</summary>
    public bool AccessDenied { get; internal set; }
    /// <summary>Thư mục con, đã xếp từ lớn đến bé.</summary>
    public List<DirNode> Children { get; internal set; } = [];

    /// <summary>Bớt dung lượng sau khi xoá một mục bên trong (cập nhật lên tới gốc).</summary>
    public void Subtract(long bytes, long files, long dirs)
    {
        for (var n = this; n != null; n = n.Parent)
        {
            n.Size = Math.Max(0, n.Size - bytes);
            n.FileCount = Math.Max(0, n.FileCount - files);
            n.DirCount = Math.Max(0, n.DirCount - dirs);
        }
    }

    /// <summary>Một file nằm trực tiếp trong thư mục này vừa bị xoá.</summary>
    public void RemoveFile(long bytes)
    {
        FilesSize = Math.Max(0, FilesSize - bytes);
        DirectFileCount = Math.Max(0, DirectFileCount - 1);
        Subtract(bytes, 1, 0);
    }

    public void Remove(DirNode child)
    {
        if (!Children.Remove(child)) return;
        Subtract(child.Size, child.FileCount, child.DirCount + 1);
    }
}

public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc);

public sealed record DiskUsageProgress(long Files, long Dirs, long Bytes, string CurrentPath);

public sealed class DiskUsageResult
{
    public required DirNode Root { get; init; }
    /// <summary>File lớn nhất, xếp từ lớn đến bé.</summary>
    public required List<FileEntry> TopFiles { get; init; }
    /// <summary>Số thư mục không đọc được (thiếu quyền, đang bị xoá…).</summary>
    public int Errors { get; init; }
    /// <summary>File chỉ nằm trên mây (OneDrive "Files On-Demand"…): không tính dung lượng vì không chiếm ổ.</summary>
    public long CloudOnlyFiles { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>
/// Quét dung lượng kiểu TreeSize: cộng dồn kích thước file theo cây thư mục, xếp từ lớn đến bé.
/// Quét song song các nhánh gần gốc, không đi theo junction/symlink, bỏ qua thư mục không có quyền.
/// </summary>
public static class DiskUsageScanner
{
    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | FileAttributes.Offline;

    /// <summary>Nhánh ở độ sâu nhỏ hơn ngần này được quét song song.</summary>
    private const int ParallelDepth = 3;

    private readonly record struct Entry(string Name, bool IsDir, long Length, DateTime LastWriteUtc, FileAttributes Attributes);

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false, // muốn biết thư mục nào không đọc được
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static DiskUsageResult Scan(string rootPath, IProgress<DiskUsageProgress>? progress = null,
        CancellationToken ct = default, int topFiles = 500)
    {
        var started = DateTime.UtcNow;
        rootPath = Path.GetFullPath(rootPath);
        var root = new DirNode(rootPath, rootPath, null);
        var state = new ScanState(topFiles, ct);
        using var timer = progress == null ? null : new Timer(_ =>
            progress.Report(new DiskUsageProgress(Interlocked.Read(ref state.Files), Interlocked.Read(ref state.Dirs),
                Interlocked.Read(ref state.Bytes), state.CurrentPath)), null, 200, 250);

        try { ScanDir(root, 0, state); }
        catch (OperationCanceledException) { }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException)) { }
        if (ct.IsCancellationRequested) Totals(root); // dừng giữa chừng: cộng lại phần đã quét được

        progress?.Report(new DiskUsageProgress(state.Files, state.Dirs, state.Bytes, rootPath));
        return new DiskUsageResult
        {
            Root = root,
            TopFiles = state.TopFiles(),
            Errors = state.Errors,
            CloudOnlyFiles = state.CloudOnly,
            Elapsed = DateTime.UtcNow - started,
            Cancelled = ct.IsCancellationRequested,
        };
    }

    /// <summary>Đọc các file nằm trực tiếp trong thư mục (khi mở rộng một nút trên giao diện), lớn đến bé.</summary>
    public static List<FileEntry> ListFiles(string dir)
    {
        var list = new List<FileEntry>();
        try
        {
            foreach (var e in Enumerate(dir))
                if (!e.IsDir)
                    list.Add(new FileEntry(Path.Combine(dir, e.Name), SizeOf(e), e.LastWriteUtc));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }

    private static long SizeOf(Entry e) =>
        (e.Attributes & CloudOnly) != 0 || (e.Attributes & FileAttributes.ReparsePoint) != 0 ? 0 : e.Length;

    private static FileSystemEnumerable<Entry> Enumerate(string dir) =>
        new(dir, (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length,
            e.LastWriteTimeUtc.UtcDateTime, e.Attributes), Options);

    private static void ScanDir(DirNode node, int depth, ScanState s)
    {
        s.Ct.ThrowIfCancellationRequested();
        s.CurrentPath = node.FullPath;
        var subdirs = new List<DirNode>();
        long filesSize = 0;
        var fileCount = 0;
        var lastWrite = DateTime.MinValue;
        try
        {
            foreach (var e in Enumerate(node.FullPath))
            {
                if (e.IsDir)
                {
                    var child = new DirNode(e.Name, Path.Combine(node.FullPath, e.Name), node);
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) child.IsLink = true;
                    subdirs.Add(child);
                    continue;
                }
                fileCount++;
                if ((e.Attributes & CloudOnly) != 0) Interlocked.Increment(ref s.CloudOnly);
                var size = SizeOf(e);
                filesSize += size;
                if (e.LastWriteUtc > lastWrite) lastWrite = e.LastWriteUtc;
                if (size > s.TopThreshold) s.OfferFile(Path.Combine(node.FullPath, e.Name), size, e.LastWriteUtc);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.AccessDenied = true;
            Interlocked.Increment(ref s.Errors);
        }
        Interlocked.Add(ref s.Files, fileCount);
        Interlocked.Add(ref s.Bytes, filesSize);
        Interlocked.Add(ref s.Dirs, subdirs.Count);
        // Gắn ngay để nếu bị dừng giữa chừng vẫn cộng được phần đã quét (xem Totals).
        node.FilesSize = filesSize;
        node.DirectFileCount = fileCount;
        node.Size = filesSize;
        node.FileCount = fileCount;
        node.LastWriteUtc = lastWrite;
        node.Children = subdirs;

        var toScan = subdirs.Where(d => !d.IsLink).ToList();
        if (depth < ParallelDepth && toScan.Count > 1)
            Parallel.ForEach(toScan, new ParallelOptions { CancellationToken = s.Ct }, d => ScanDir(d, depth + 1, s));
        else
            foreach (var d in toScan) ScanDir(d, depth + 1, s);

        node.Size = filesSize + subdirs.Sum(d => d.Size);
        node.FileCount = fileCount + subdirs.Sum(d => d.FileCount);
        node.DirCount = subdirs.Count + subdirs.Sum(d => d.DirCount);
        node.LastWriteUtc = subdirs.Select(d => d.LastWriteUtc).Append(lastWrite).Max();
        subdirs.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>Tính lại tổng từ dưới lên (dùng khi bị dừng giữa chừng, nút chưa quét xong chưa có tổng).</summary>
    private static void Totals(DirNode node)
    {
        foreach (var c in node.Children) Totals(c);
        if (node.Children.Count == 0) return;
        node.Size = node.FilesSize + node.Children.Sum(d => d.Size);
        node.FileCount = node.DirectFileCount + node.Children.Sum(d => d.FileCount);
        node.DirCount = node.Children.Count + node.Children.Sum(d => d.DirCount);
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    private sealed class ScanState(int topCount, CancellationToken ct)
    {
        public readonly CancellationToken Ct = ct;
        public long Files, Dirs, Bytes, CloudOnly;
        public int Errors;
        public volatile string CurrentPath = "";

        // Đống min giữ N file lớn nhất; ngưỡng đọc không khoá để bỏ nhanh file nhỏ.
        private readonly PriorityQueue<FileEntry, long> _top = new();
        private readonly object _gate = new();
        private long _threshold = -1;
        public long TopThreshold => Interlocked.Read(ref _threshold);

        public void OfferFile(string path, long size, DateTime lastWrite)
        {
            if (topCount <= 0) return;
            lock (_gate)
            {
                if (_top.Count < topCount) _top.Enqueue(new FileEntry(path, size, lastWrite), size);
                else if (size > _top.Peek().Size) _top.EnqueueDequeue(new FileEntry(path, size, lastWrite), size);
                else return;
                if (_top.Count >= topCount) Interlocked.Exchange(ref _threshold, _top.Peek().Size);
            }
        }

        public List<FileEntry> TopFiles()
        {
            lock (_gate) return _top.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Size).ToList();
        }
    }
}

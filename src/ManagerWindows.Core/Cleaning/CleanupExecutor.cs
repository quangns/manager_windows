using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Cleaning;

public sealed class CleanupResult
{
    public long FreedBytes { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();

    internal void AddError(string msg)
    {
        Failed++;
        if (Errors.Count < 50) Errors.Add(msg);
    }
}

/// <summary>Thực hiện một <see cref="CleanupPlan"/>, kiểm tra lại từng file ngay trước khi xoá.</summary>
public sealed class CleanupExecutor(IFileDeleter deleter, IClock clock, IRecycleBin? recycleBin = null)
{
    public CleanupResult Execute(CleanupPlan plan, CancellationToken ct = default)
    {
        var result = new CleanupResult();
        foreach (var rp in plan.Rules)
        {
            if (rp.Rule.Kind == RuleKind.RecycleBin)
            {
                try
                {
                    recycleBin?.Empty();
                    result.FreedBytes += rp.TotalBytes;
                }
                catch (Exception ex) { result.AddError($"{rp.Rule.Id}: {ex.Message}"); }
                continue;
            }

            // Thư mục vừa bị chính lần dọn này sửa (xoá file bên trong) thì không xét tuổi khi dọn thư mục rỗng.
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in rp.Files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(c.Path);
                    // Kiểm tra lại: file có thể đã đổi từ lúc chạy thử.
                    if (!fi.Exists || SafeFileWalker.IsReparsePoint(fi)) continue;
                    if (!SafeFileWalker.IsUnder(fi.FullName, c.Root)) continue;
                    if (HasReparseAncestor(fi.Directory, c.Root)) continue;
                    var entry = new FileEntry(fi.FullName, fi.Length, fi.LastWriteTimeUtc, fi.LastAccessTimeUtc, fi.CreationTimeUtc);
                    if (!CleanupPlanner.IsEligible(rp.Rule, entry, clock.UtcNow)) continue;

                    if (rp.Rule.Action == CleanupAction.Recycle) deleter.Recycle(fi.FullName);
                    else
                    {
                        if (fi.IsReadOnly) fi.IsReadOnly = false;
                        deleter.Delete(fi.FullName);
                    }
                    result.Deleted++;
                    result.FreedBytes += entry.Size;
                    touched.Add(fi.DirectoryName!);
                }
                catch (IOException ex) { result.AddError($"{c.Path}: {ex.Message}"); } // file đang mở
                catch (UnauthorizedAccessException ex) { result.AddError($"{c.Path}: {ex.Message}"); }
            }

            if (rp.Rule.DeleteEmptyDirectories)
                foreach (var root in rp.Roots)
                    DeleteEmptyDirectories(root, root, rp.Rule.MinAgeDays, touched);
        }
        return result;
    }

    private static bool HasReparseAncestor(DirectoryInfo? dir, string root)
    {
        for (var d = dir; d != null && SafeFileWalker.IsUnder(d.FullName, root); d = d.Parent)
            if (SafeFileWalker.IsReparsePoint(d)) return true;
        return false;
    }

    /// <summary>Xoá thư mục con rỗng từ dưới lên; giữ lại thư mục gốc và thư mục vừa mới tạo.</summary>
    private bool DeleteEmptyDirectories(string dir, string root, int minAgeDays, HashSet<string> touched)
    {
        var empty = true;
        foreach (var sub in SafeFileWalker.EnumerateDirectories(dir))
            if (!DeleteEmptyDirectories(sub, root, minAgeDays, touched)) empty = false;

        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return true;
            if (info.EnumerateFileSystemInfos().Any()) return false;
            if (!empty) return false;
            if (SafeFileWalker.IsUnder(root, dir)) return false; // chính là gốc
            if (!touched.Contains(info.FullName) && (clock.UtcNow - info.LastWriteTimeUtc).TotalDays < minAgeDays) return false;
            info.Delete(false);
            if (info.Parent != null) touched.Add(info.Parent.FullName);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

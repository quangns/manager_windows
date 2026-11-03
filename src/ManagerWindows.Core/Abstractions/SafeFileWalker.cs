namespace ManagerWindows.Core.Abstractions;

public sealed record FileEntry(
    string Path,
    long Size,
    DateTime LastWriteUtc,
    DateTime LastAccessUtc,
    DateTime CreationUtc);

/// <summary>
/// Duyệt cây thư mục KHÔNG đi theo reparse point (junction/symlink). Quan trọng vì sau khi chuyển data
/// app sang ổ khác, junction trong AppData trỏ tới data thật: đi theo nó là có thể xoá nhầm.
/// </summary>
public static class SafeFileWalker
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static bool IsReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    public static IEnumerable<FileEntry> EnumerateFiles(string root, bool recursive = true,
        Func<string, bool>? skipDirectory = null, CancellationToken ct = default)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || IsReparsePoint(rootInfo)) yield break;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(rootInfo);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos("*", Options).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (IsReparsePoint(child)) continue;
                if (child is DirectoryInfo sub)
                {
                    if (recursive && (skipDirectory == null || !skipDirectory(sub.FullName)))
                        stack.Push(sub);
                }
                else if (child is FileInfo f)
                {
                    FileEntry entry;
                    try { entry = new FileEntry(f.FullName, f.Length, f.LastWriteTimeUtc, f.LastAccessTimeUtc, f.CreationTimeUtc); }
                    catch (IOException) { continue; }
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Thư mục con trực tiếp (không phải reparse point).</summary>
    public static IEnumerable<string> EnumerateDirectories(string root)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists) return [];
        try
        {
            return info.EnumerateDirectories("*", Options)
                .Where(d => !IsReparsePoint(d))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static (long Bytes, long Files) Measure(string root, CancellationToken ct = default)
    {
        long bytes = 0, files = 0;
        foreach (var f in EnumerateFiles(root, true, null, ct))
        {
            bytes += f.Size;
            files++;
        }
        return (bytes, files);
    }

    /// <summary>Xoá cả cây thư mục, gặp reparse point thì chỉ gỡ liên kết chứ không xoá nội dung đích.</summary>
    public static void DeleteTree(string root)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists) return;
        if (IsReparsePoint(info)) { info.Delete(false); return; }

        foreach (var child in info.EnumerateFileSystemInfos("*", Options).ToList())
        {
            if (child is DirectoryInfo d)
            {
                if (IsReparsePoint(d)) d.Delete(false);
                else DeleteTree(d.FullName);
            }
            else
            {
                if ((child.Attributes & FileAttributes.ReadOnly) != 0)
                    child.Attributes &= ~FileAttributes.ReadOnly;
                child.Delete();
            }
        }
        info.Delete(false);
    }

    /// <summary>path có nằm trong root (hoặc bằng root) không, so sánh không phân biệt hoa thường như Windows.</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (p.Equals(r, cmp)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }
}

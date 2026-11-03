using System.Security.Cryptography;
using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Cleaning;

public enum SuggestionKind { Stale, LargeFile, OldInstaller, Duplicate }

public sealed record FileSuggestion(
    string Path,
    long Size,
    DateTime LastUsedUtc,
    SuggestionKind Kind,
    double Score,
    string? DuplicateGroup = null);

public sealed class SuggestionOptions
{
    public int StaleDays { get; set; } = 180;
    public long StaleMinBytes { get; set; } = 1L << 20;          // bỏ qua file < 1 MB khi xét "lâu không dùng"
    public long LargeFileBytes { get; set; } = 500L << 20;
    public int OldInstallerDays { get; set; } = 30;
    public long DuplicateMinBytes { get; set; } = 1L << 20;
    /// <summary>Windows có cập nhật LastAccess không (NtfsDisableLastAccessUpdate). false → chỉ dùng LastWrite.</summary>
    public bool LastAccessReliable { get; set; } = true;
    public List<string> InstallerExtensions { get; set; } = [".exe", ".msi", ".iso", ".zip", ".rar", ".7z"];
    /// <summary>Tên thư mục bỏ qua khi quét (so khớp tên, không phân biệt hoa thường).</summary>
    public List<string> SkipDirectoryNames { get; set; } =
        ["AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
         "System Volume Information", "node_modules", ".git"];
}

/// <summary>
/// Quét các thư mục người dùng chọn và đưa ra GỢI Ý dọn. Không bao giờ tự xoá: người dùng tick chọn trên UI.
/// </summary>
public sealed class SuggestionScanner(IClock clock)
{
    public List<FileSuggestion> Scan(IEnumerable<string> roots, SuggestionOptions opt,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var all = new List<FileEntry>();
        var skip = new HashSet<string>(opt.SkipDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        foreach (var root in roots.Where(Directory.Exists))
        {
            progress?.Report(root);
            all.AddRange(SafeFileWalker.EnumerateFiles(root, true, d => skip.Contains(Path.GetFileName(d)), ct));
        }

        var result = new List<FileSuggestion>();
        foreach (var f in all)
        {
            var lastUsed = LastUsed(f, opt);
            var ageDays = (now - lastUsed).TotalDays;
            var ext = Path.GetExtension(f.Path);

            if (opt.InstallerExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                && SafeFileWalker.IsUnder(f.Path, downloads) && ageDays >= opt.OldInstallerDays)
                result.Add(new FileSuggestion(f.Path, f.Size, lastUsed, SuggestionKind.OldInstaller, Score(ageDays, f.Size)));
            else if (f.Size >= opt.StaleMinBytes && ageDays >= opt.StaleDays)
                result.Add(new FileSuggestion(f.Path, f.Size, lastUsed, SuggestionKind.Stale, Score(ageDays, f.Size)));
            else if (f.Size >= opt.LargeFileBytes)
                result.Add(new FileSuggestion(f.Path, f.Size, lastUsed, SuggestionKind.LargeFile, Score(ageDays, f.Size)));
        }

        foreach (var group in DuplicateFinder.Find(all.Where(f => f.Size >= opt.DuplicateMinBytes), ct))
        {
            // Giữ bản dùng gần nhất, gợi ý xoá các bản còn lại.
            var keep = group.Files.MaxBy(f => LastUsed(f, opt))!;
            foreach (var f in group.Files.Where(f => f != keep))
                result.Add(new FileSuggestion(f.Path, f.Size, LastUsed(f, opt), SuggestionKind.Duplicate,
                    Score((now - LastUsed(f, opt)).TotalDays, f.Size), group.Hash[..12]));
        }

        return result.OrderByDescending(s => s.Score).ToList();
    }

    internal static DateTime LastUsed(FileEntry f, SuggestionOptions opt) =>
        opt.LastAccessReliable && f.LastAccessUtc > f.LastWriteUtc ? f.LastAccessUtc : f.LastWriteUtc;

    /// <summary>Điểm ưu tiên: file càng cũ và càng lớn càng đáng dọn.</summary>
    internal static double Score(double ageDays, long size) =>
        Math.Max(ageDays, 1) / 30.0 * Math.Log10(size / 1048576.0 + 1.0 + 1e-9);
}

public sealed record DuplicateGroup(string Hash, List<FileEntry> Files);

public static class DuplicateFinder
{
    private const int HeadBytes = 64 * 1024;

    /// <summary>Lọc theo kích thước → hash 64 KB đầu → hash toàn bộ, để không phải đọc hết mọi file.</summary>
    public static List<DuplicateGroup> Find(IEnumerable<FileEntry> files, CancellationToken ct = default)
    {
        var groups = new List<DuplicateGroup>();
        foreach (var bySize in files.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            foreach (var byHead in bySize.GroupBy(f => HashFile(f.Path, HeadBytes)).Where(g => g.Key != null && g.Count() > 1))
            {
                ct.ThrowIfCancellationRequested();
                var candidates = byHead.ToList();
                var full = candidates[0].Size <= HeadBytes
                    ? candidates.GroupBy(_ => byHead.Key)
                    : candidates.GroupBy(f => HashFile(f.Path, long.MaxValue));
                foreach (var g in full.Where(g => g.Key != null && g.Count() > 1))
                    groups.Add(new DuplicateGroup(g.Key!, g.ToList()));
            }
        }
        return groups;
    }

    private static string? HashFile(string path, long maxBytes)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            using var sha = SHA256.Create();
            var buf = new byte[81920];
            long remaining = maxBytes;
            int n;
            while (remaining > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
            {
                sha.TransformBlock(buf, 0, n, null, 0);
                remaining -= n;
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

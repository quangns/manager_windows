using System.IO.Enumeration;
using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Cleaning;

public interface IRecycleBin
{
    long QuerySize();
    void Empty();
}

public sealed record CleanupCandidate(string RuleId, string Root, string Path, long Size, DateTime LastWriteUtc);

public sealed class RulePlan
{
    public required CleanupRule Rule { get; init; }
    public List<string> Roots { get; } = new();
    public List<CleanupCandidate> Files { get; } = new();
    /// <summary>Dung lượng ước tính (với RecycleBin là dung lượng Thùng rác).</summary>
    public long TotalBytes { get; set; }
}

public sealed class CleanupPlan
{
    public List<RulePlan> Rules { get; } = new();
    public long TotalBytes => Rules.Sum(r => r.TotalBytes);
    public int FileCount => Rules.Sum(r => r.Files.Count);

    /// <summary>Bản kế hoạch bỏ đi các file người dùng muốn giữ (bỏ tick khi xem lại danh sách).</summary>
    public CleanupPlan Without(IReadOnlySet<string> excludedPaths)
    {
        var plan = new CleanupPlan();
        foreach (var rp in Rules)
        {
            var copy = new RulePlan { Rule = rp.Rule };
            copy.Roots.AddRange(rp.Roots);
            if (rp.Rule.Kind == RuleKind.RecycleBin) copy.TotalBytes = rp.TotalBytes;
            foreach (var f in rp.Files.Where(f => !excludedPaths.Contains(f.Path)))
            {
                copy.Files.Add(f);
                copy.TotalBytes += f.Size;
            }
            plan.Rules.Add(copy);
        }
        return plan;
    }
}

/// <summary>Lập danh sách file sẽ xoá (chạy thử). Không xoá gì.</summary>
public sealed class CleanupPlanner(IClock clock, IRecycleBin? recycleBin = null)
{
    public CleanupPlan Plan(IEnumerable<CleanupRule> rules, CancellationToken ct = default)
    {
        var plan = new CleanupPlan();
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var rp = new RulePlan { Rule = rule };
            if (rule.Kind == RuleKind.RecycleBin)
            {
                rp.TotalBytes = recycleBin?.QuerySize() ?? 0;
                plan.Rules.Add(rp);
                continue;
            }

            foreach (var root in rule.Paths.SelectMany(PathExpander.Expand))
            {
                if (IsDangerousRoot(root)) continue;
                rp.Roots.Add(root);
                foreach (var f in SafeFileWalker.EnumerateFiles(root, rule.Recursive, null, ct))
                {
                    if (!IsEligible(rule, f, clock.UtcNow)) continue;
                    rp.Files.Add(new CleanupCandidate(rule.Id, root, f.Path, f.Size, f.LastWriteUtc));
                    rp.TotalBytes += f.Size;
                }
            }
            plan.Rules.Add(rp);
        }
        return plan;
    }

    internal static bool IsEligible(CleanupRule rule, FileEntry f, DateTime nowUtc)
    {
        var name = Path.GetFileName(f.Path);
        if (!rule.Patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
            return false;
        if (rule.ExcludePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
            return false;
        // Lấy mốc mới nhất giữa ghi và tạo: thận trọng, file vừa được copy vào cũng không bị xoá ngay.
        var newest = f.LastWriteUtc > f.CreationUtc ? f.LastWriteUtc : f.CreationUtc;
        return (nowUtc - newest).TotalDays >= rule.MinAgeDays;
    }

    /// <summary>Chặn luật cấu hình sai trỏ vào gốc ổ đĩa, thư mục Windows hay hồ sơ người dùng.</summary>
    internal static bool IsDangerousRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(full) is { } pr && Path.TrimEndingDirectorySeparator(pr) == full) return true;
        var cmp = StringComparison.OrdinalIgnoreCase;
        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        return forbidden.Where(f => !string.IsNullOrEmpty(f))
            .Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), full, cmp));
    }
}

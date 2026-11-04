using System.Globalization;
using System.Text.RegularExpressions;

namespace ManagerWindows.Core.Monitoring;

/// <summary>Một dòng của <c>ps -axo pid=,ppid=,uid=,%cpu=,rss=,comm=</c> trên macOS.</summary>
public sealed record MacProc(int Pid, int ParentPid, int Uid, double CpuPercent, long RssBytes, string Path);

/// <summary>
/// Đọc và gộp tiến trình macOS. Thuần xử lý chuỗi nên kiểm thử được trên Linux.
/// Trên macOS cột <c>comm</c> là đường dẫn đầy đủ của file chạy (vd. /Applications/Safari.app/Contents/MacOS/Safari).
/// </summary>
public static partial class MacProcesses
{
    public const string PsArguments = "-axo pid=,ppid=,uid=,%cpu=,rss=,comm=";

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\d+)\s+([\d.,]+)\s+(\d+)\s+(.+?)\s*$")]
    private static partial Regex PsLine();

    public static List<MacProc> ParsePs(string output)
    {
        var list = new List<MacProc>();
        foreach (var line in output.Split('\n'))
        {
            var m = PsLine().Match(line);
            if (!m.Success) continue;
            list.Add(new MacProc(
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                double.Parse(m.Groups[4].Value.Replace(',', '.'), CultureInfo.InvariantCulture),
                long.Parse(m.Groups[5].Value) * 1024, // rss tính bằng KB
                m.Groups[6].Value));
        }
        return list;
    }

    /// <summary>
    /// Gói .app chứa file chạy (lấy gói NGOÀI CÙNG, nên các "Helper.app" lồng bên trong Chrome/Electron gộp vào app chính).
    /// Trả về (đường dẫn gói, tên hiển thị) hoặc null nếu không thuộc gói .app nào.
    /// </summary>
    public static (string BundlePath, string Name)? BundleOf(string path)
    {
        var i = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? (path, NameOf(path)) : null;
        var bundle = path[..(i + 4)];
        return (bundle, NameOf(bundle));
    }

    private static string NameOf(string bundle)
    {
        var slash = bundle.LastIndexOf('/');
        var file = slash >= 0 ? bundle[(slash + 1)..] : bundle;
        return file.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    private static readonly string[] SystemPrefixes =
        ["/System/", "/usr/", "/bin/", "/sbin/", "/Library/Apple/", "/private/", "/Library/PrivilegedHelperTools/"];

    /// <summary>
    /// Gộp theo app: tiến trình thuộc gói .app gộp theo gói; còn lại gộp theo tên file chạy.
    /// Ứng dụng = gói .app của người dùng hiện tại; Hệ thống = của user khác (root…) hoặc nằm trong thư mục hệ thống;
    /// còn lại là Chạy ngầm (node, python, agent trong ~/Library…).
    /// </summary>
    public static List<AppGroup> Group(IReadOnlyList<MacProc> procs, int currentUid, int selfPid = -1)
    {
        return procs
            .Where(p => p.Pid > 1 && p.Pid != selfPid)
            .Select(p =>
            {
                var bundle = BundleOf(p.Path);
                var key = bundle?.Name ?? FileName(p.Path);
                var isSystem = p.Uid != currentUid || SystemPrefixes.Any(s => p.Path.StartsWith(s, StringComparison.Ordinal));
                var kind = isSystem ? ProcessKind.System : bundle != null ? ProcessKind.App : ProcessKind.Background;
                return (p, key, bundlePath: bundle?.BundlePath ?? p.Path, kind);
            })
            .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // App của người dùng có tiến trình hệ thống cùng tên (hiếm) → lấy loại "nhẹ" nhất để vẫn quản lý được.
                var kind = g.Any(x => x.kind == ProcessKind.App) ? ProcessKind.App
                    : g.All(x => x.kind == ProcessKind.System) ? ProcessKind.System : ProcessKind.Background;
                return new AppGroup(g.Key, g.First().bundlePath, kind, g.Count(), g.Sum(x => x.p.CpuPercent),
                    g.Sum(x => x.p.RssBytes), g.Select(x => x.p.Pid).ToList());
            })
            .ToList();
    }

    private static string FileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSize();

    [GeneratedRegex(@"^(.+?):\s+(\d+)\.?\s*$", RegexOptions.Multiline)]
    private static partial Regex VmLine();

    /// <summary>
    /// RAM đã dùng từ <c>vm_stat</c>, gần với "Memory Used" của Activity Monitor:
    /// (active + wired + bị nén) × kích thước trang. (inactive/speculative/purgeable là RAM macOS lấy lại được ngay.)
    /// </summary>
    public static long UsedMemoryFromVmStat(string vmStat)
    {
        var pageSize = PageSize().Match(vmStat) is { Success: true } m ? long.Parse(m.Groups[1].Value) : 4096;
        var v = VmLine().Matches(vmStat).ToDictionary(x => x.Groups[1].Value.Trim(), x => long.Parse(x.Groups[2].Value));
        long Get(string k) => v.TryGetValue(k, out var n) ? n : 0;
        return (Get("Pages active") + Get("Pages wired down") + Get("Pages occupied by compressor")) * pageSize;
    }
}

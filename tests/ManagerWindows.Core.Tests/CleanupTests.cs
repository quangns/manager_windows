using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;

namespace ManagerWindows.Core.Tests;

public class CleanupTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static CleanupRule Rule(string root, int minAge = 2, params string[] patterns) => new()
    {
        Id = "t",
        Paths = [root],
        Patterns = patterns.Length > 0 ? patterns.ToList() : ["*"],
        MinAgeDays = minAge,
    };

    [Fact]
    public void Plan_only_takes_old_files_matching_pattern()
    {
        using var t = new TempDir();
        var root = t.Dir("temp");
        t.File("temp/old.tmp", 100, Now.AddDays(-10));
        t.File("temp/new.tmp", 100, Now.AddHours(-1));
        t.File("temp/old.keep", 100, Now.AddDays(-10));
        t.File("temp/sub/old2.tmp", 50, Now.AddDays(-5));

        var plan = new CleanupPlanner(new FakeClock(Now)).Plan([Rule(root, 2, "*.tmp")]);

        var files = plan.Rules[0].Files.Select(f => Path.GetFileName(f.Path)).OrderBy(x => x).ToList();
        Assert.Equal(["old.tmp", "old2.tmp"], files);
        Assert.Equal(150, plan.TotalBytes);
    }

    [Fact]
    public void Plan_does_not_follow_symlink_into_other_folder()
    {
        using var t = new TempDir();
        var root = t.Dir("temp");
        var precious = t.Dir("precious");
        t.File("precious/data.db", 100, Now.AddDays(-100));
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), precious);

        var plan = new CleanupPlanner(new FakeClock(Now)).Plan([Rule(root, 1)]);
        new CleanupExecutor(new PermanentDeleter(), new FakeClock(Now)).Execute(plan);

        Assert.Empty(plan.Rules[0].Files);
        Assert.True(File.Exists(Path.Combine(precious, "data.db")));
    }

    [Fact]
    public void Execute_deletes_files_and_old_empty_dirs_but_keeps_root()
    {
        using var t = new TempDir();
        var root = t.Dir("temp");
        var f1 = t.File("temp/a/b/x.log", 10, Now.AddDays(-10));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "a", "b"), Now.AddDays(-10));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "a"), Now.AddDays(-10));

        var clock = new FakeClock(Now);
        var plan = new CleanupPlanner(clock).Plan([Rule(root, 2)]);
        var result = new CleanupExecutor(new PermanentDeleter(), clock).Execute(plan);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(10, result.FreedBytes);
        Assert.False(File.Exists(f1));
        Assert.True(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.Combine(root, "a", "b")));
    }

    [Fact]
    public void Execute_rechecks_age_before_deleting()
    {
        using var t = new TempDir();
        var root = t.Dir("temp");
        var f = t.File("temp/x.tmp", 10, Now.AddDays(-10));
        var clock = new FakeClock(Now);
        var plan = new CleanupPlanner(clock).Plan([Rule(root, 2)]);

        // File được ghi lại sau khi chạy thử → không được xoá.
        File.SetLastWriteTimeUtc(f, Now);
        var result = new CleanupExecutor(new PermanentDeleter(), clock).Execute(plan);

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Excluded_files_are_kept_when_executing()
    {
        using var t = new TempDir();
        var root = t.Dir("cache");
        var keep = t.File("cache/keep.db", 100, Now.AddDays(-10));
        var drop = t.File("cache/drop.tmp", 50, Now.AddDays(-10));
        var clock = new FakeClock(Now);
        var plan = new CleanupPlanner(clock).Plan([Rule(root, 2)]);

        var filtered = plan.Without(new HashSet<string>([keep], StringComparer.OrdinalIgnoreCase));
        var result = new CleanupExecutor(new PermanentDeleter(), clock).Execute(filtered);

        Assert.Equal(50, filtered.TotalBytes);
        Assert.Equal(150, plan.TotalBytes); // bản gốc không bị đổi
        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(drop));
    }

    [Fact]
    public void PathExpander_expands_wildcards_per_segment()
    {
        using var t = new TempDir();
        t.Dir("User Data/Default/Cache");
        t.Dir("User Data/Profile 1/Cache");
        t.Dir("User Data/Profile 2/NoCache");

        var found = PathExpander.Expand(Path.Combine(t.Path, "User Data", "*", "Cache")).ToList();

        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.EndsWith("Cache", p));
    }

    [Fact]
    public void PathExpander_skips_unresolved_environment_variables()
    {
        Assert.Empty(PathExpander.Expand("%MW_NOT_DEFINED_VAR%/Temp"));
    }

    [Fact]
    public void Dangerous_roots_are_rejected()
    {
        Assert.True(CleanupPlanner.IsDangerousRoot(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.True(CleanupPlanner.IsDangerousRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(CleanupPlanner.IsDangerousRoot(Path.GetTempPath()));
    }

    [Fact]
    public void Default_rules_file_is_valid()
    {
        var rules = RuleLoader.Load(Path.Combine(AppContext.BaseDirectory, "rules", "cleanup-rules.json"));
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.Kind == RuleKind.RecycleBin);
        Assert.All(rules, r => Assert.True(r.Name.ContainsKey("vi") && r.Name.ContainsKey("en"), r.Id));
        Assert.All(rules.Where(r => r.Kind == RuleKind.Files), r => Assert.NotEmpty(r.Paths));
        // Mục "nên xem lại" phải là phạm vi người dùng: app liệt kê file được mà không cần quyền hệ thống.
        Assert.All(rules.Where(r => r.Risk == RuleRisk.Review), r => Assert.Equal(RuleScope.User, r.Scope));
    }

    [Fact]
    public void Default_app_profiles_are_valid()
    {
        var profiles = ManagerWindows.Core.Relocation.AppProfile.Load(Path.Combine(AppContext.BaseDirectory, "rules", "app-profiles.json"));
        Assert.Contains(profiles, p => p.Id == "zalo");
        Assert.Equal(profiles.Count, profiles.Select(p => p.Id).Distinct().Count());
    }
}

public class SuggestionTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Finds_stale_large_and_duplicate_files()
    {
        using var t = new TempDir();
        t.File("docs/old.pdf", 2 << 20, Now.AddDays(-400), fill: 1);
        t.File("docs/recent.pdf", 2 << 20, Now.AddDays(-5), fill: 2);
        t.File("docs/tiny-old.txt", 100, Now.AddDays(-400));
        var a = t.File("docs/copy1.bin", 3 << 20, Now.AddDays(-10));
        var b = t.File("docs/copy2.bin", 3 << 20, Now.AddDays(-20));
        t.File("AppData/skip.bin", 3 << 20, Now.AddDays(-999));

        var opt = new SuggestionOptions { LargeFileBytes = 10L << 20 };
        var s = new SuggestionScanner(new FakeClock(Now)).Scan([t.Path], opt);

        Assert.Contains(s, x => x.Kind == SuggestionKind.Stale && x.Path.EndsWith("old.pdf"));
        Assert.DoesNotContain(s, x => x.Path.EndsWith("recent.pdf"));
        Assert.DoesNotContain(s, x => x.Path.EndsWith("tiny-old.txt"));
        Assert.DoesNotContain(s, x => x.Path.Contains("AppData"));
        // Hai file giống hệt: giữ bản dùng gần nhất (copy1), gợi ý xoá copy2.
        var dup = Assert.Single(s, x => x.Kind == SuggestionKind.Duplicate);
        Assert.Equal(b, dup.Path);
    }

    [Fact]
    public void Ignores_last_access_when_unreliable()
    {
        var f = new FileEntry("x", 1, Now.AddDays(-100), Now, Now.AddDays(-100));
        Assert.Equal(Now, SuggestionScanner.LastUsed(f, new SuggestionOptions { LastAccessReliable = true }));
        Assert.Equal(Now.AddDays(-100), SuggestionScanner.LastUsed(f, new SuggestionOptions { LastAccessReliable = false }));
    }

    [Fact]
    public void Different_content_same_size_is_not_duplicate()
    {
        using var t = new TempDir();
        var a = t.File("a.bin", 2 << 20);
        var b = t.File("b.bin", 2 << 20);
        var bytes = File.ReadAllBytes(b);
        bytes[^1] = 1; // khác ở cuối file, phần đầu 64 KB giống nhau
        File.WriteAllBytes(b, bytes);

        var groups = DuplicateFinder.Find(SafeFileWalker.EnumerateFiles(t.Path));
        Assert.Empty(groups);
    }
}

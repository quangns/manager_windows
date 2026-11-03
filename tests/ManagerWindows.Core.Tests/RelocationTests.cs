using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Relocation;

namespace ManagerWindows.Core.Tests;

public class RelocationTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture : IDisposable
    {
        public TempDir T { get; } = new();
        public SymlinkApi Links { get; } = new();
        public FakeVolumes Volumes { get; } = new();
        public MemoryStore Store { get; } = new();
        public IDirectoryCopier Copier { get; set; } = new ManagedDirectoryCopier();
        public RelocationEngine Engine => new(Links, Volumes, Copier, Store, new FakeClock(Now));
        public string Source { get; }
        public string TargetRoot { get; }

        public Fixture()
        {
            Source = T.Dir("src/ZaloData");
            T.File("src/ZaloData/msg.db", 1000);
            T.File("src/ZaloData/files/photo.jpg", 2000);
            T.Dir("src/ZaloData/emptydir");
            TargetRoot = T.Dir("dst");
        }

        public void Dispose() => T.Dispose();
    }

    [Fact]
    public void Run_moves_data_and_leaves_link()
    {
        using var f = new Fixture();
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        Assert.Empty(f.Engine.Precheck(r));

        f.Engine.Run(r);

        Assert.Equal(RelocationState.Completed, r.State);
        Assert.Equal(r.Target, f.Links.GetLinkTarget(f.Source));
        Assert.True(File.Exists(Path.Combine(f.Source, "files", "photo.jpg"))); // đọc qua liên kết
        Assert.True(Directory.Exists(Path.Combine(r.Target, "emptydir")));
        Assert.True(Directory.Exists(r.Backup));
        Assert.Equal(RelocationHealth.Healthy, f.Engine.Check(r));
        Assert.Equal(
            [RelocationState.Copying, RelocationState.Copied, RelocationState.SourceRenamed, RelocationState.Linked, RelocationState.Completed],
            f.Store.History);
    }

    [Fact]
    public void Rollback_after_completion_restores_original()
    {
        using var f = new Fixture();
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        f.Engine.Run(r);
        f.Engine.Rollback(r);

        Assert.Equal(RelocationState.RolledBack, r.State);
        Assert.Null(f.Links.GetLinkTarget(f.Source));
        Assert.True(File.Exists(Path.Combine(f.Source, "msg.db")));
        Assert.False(Directory.Exists(r.Backup));
        Assert.False(Directory.Exists(r.Target));
    }

    [Fact]
    public void Rollback_after_backup_deleted_copies_data_back()
    {
        using var f = new Fixture();
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        f.Engine.Run(r);
        File.WriteAllText(Path.Combine(f.Source, "new-after-move.txt"), "hi"); // ghi qua liên kết
        f.Engine.DeleteBackup(r);
        Assert.False(Directory.Exists(r.Backup));

        f.Engine.Rollback(r);

        Assert.Null(f.Links.GetLinkTarget(f.Source));
        Assert.True(File.Exists(Path.Combine(f.Source, "new-after-move.txt")));
        Assert.True(File.Exists(Path.Combine(f.Source, "files", "photo.jpg")));
        Assert.False(Directory.Exists(r.Target));
    }

    private sealed class FailingLinkApi(ILinkApi inner) : ILinkApi
    {
        public bool Fail { get; set; } = true;
        public void CreateDirectoryLink(string l, string t)
        {
            if (Fail) throw new IOException("giả lập lỗi");
            inner.CreateDirectoryLink(l, t);
        }
        public string? GetLinkTarget(string p) => inner.GetLinkTarget(p);
        public void RemoveLink(string l) => inner.RemoveLink(l);
    }

    [Fact]
    public void Failure_while_linking_can_resume_or_roll_back()
    {
        using var f = new Fixture();
        var failing = new FailingLinkApi(f.Links);
        var engine = new RelocationEngine(failing, f.Volumes, f.Copier, f.Store, new FakeClock(Now));
        var r = engine.Create(f.Source, f.TargetRoot, "zalo");

        Assert.Throws<IOException>(() => engine.Run(r));
        Assert.Equal(RelocationState.Failed, r.State);
        Assert.Equal(RelocationState.SourceRenamed, r.FailedAt);
        Assert.False(Directory.Exists(f.Source));

        // Làm tiếp sau khi hết lỗi.
        failing.Fail = false;
        engine.Run(r);
        Assert.Equal(RelocationState.Completed, r.State);
    }

    private sealed class LockedRenameStore(MemoryStore inner, string source) : IRelocationStore
    {
        public void Save(RelocationRecord r)
        {
            inner.Save(r);
            // Giả lập app ghi thêm file ngay sau khi copy xong và giữ khoá thư mục → đổi tên thất bại.
            if (r.State == RelocationState.Copied && !File.Exists(Path.Combine(source, "late.txt")))
                File.WriteAllText(Path.Combine(source, "late.txt"), "written after copy");
        }
    }

    [Fact]
    public void Resume_after_rename_failure_recopies_changed_data()
    {
        using var f = new Fixture();
        var engine = new RelocationEngine(f.Links, f.Volumes, f.Copier, new LockedRenameStore(f.Store, f.Source), new FakeClock(Now));
        var r = engine.Create(f.Source, f.TargetRoot, "zalo");
        Directory.CreateDirectory(r.Backup); // chiếm chỗ backup → bước đổi tên lỗi

        Assert.Throws<IOException>(() => engine.Run(r));
        Assert.Equal(RelocationState.Copied, r.FailedAt);

        Directory.Delete(r.Backup);
        engine.Run(r);

        Assert.Equal(RelocationState.Completed, r.State);
        Assert.True(File.Exists(Path.Combine(r.Target, "late.txt")));
    }

    [Fact]
    public void Failure_while_linking_then_rollback_restores_source()
    {
        using var f = new Fixture();
        var engine = new RelocationEngine(new FailingLinkApi(f.Links), f.Volumes, f.Copier, f.Store, new FakeClock(Now));
        var r = engine.Create(f.Source, f.TargetRoot, "zalo");
        Assert.Throws<IOException>(() => engine.Run(r));

        engine.Rollback(r);

        Assert.True(File.Exists(Path.Combine(f.Source, "msg.db")));
        Assert.False(Directory.Exists(r.Target));
        Assert.Equal(RelocationState.RolledBack, r.State);
    }

    private sealed class PartialCopier : IDirectoryCopier
    {
        public void Copy(string s, string d, CancellationToken ct)
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "partial"), "x");
        }
    }

    [Fact]
    public void Incomplete_copy_fails_verification_and_source_is_untouched()
    {
        using var f = new Fixture();
        f.Copier = new PartialCopier();
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");

        Assert.Throws<IOException>(() => f.Engine.Run(r));
        Assert.Equal(RelocationState.Copying, r.FailedAt);
        Assert.True(File.Exists(Path.Combine(f.Source, "msg.db")));
        Assert.Null(f.Links.GetLinkTarget(f.Source));

        f.Engine.Rollback(r);
        Assert.False(Directory.Exists(r.Target));
    }

    [Fact]
    public void Precheck_reports_problems()
    {
        using var f = new Fixture();
        f.Volumes.Resolver = p => p.Contains("dst")
            ? new VolumeInfo(IsNtfs: false, IsFixed: false, FreeBytes: 10, Root: "E:")
            : new VolumeInfo(true, true, long.MaxValue / 2, "C:");
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");

        var problems = f.Engine.Precheck(r);

        Assert.Contains(PrecheckProblem.TargetNotNtfs, problems);
        Assert.Contains(PrecheckProblem.TargetNotFixed, problems);
        Assert.Contains(PrecheckProblem.NotEnoughSpace, problems);
    }

    [Fact]
    public void Precheck_rejects_same_volume_and_already_linked()
    {
        using var f = new Fixture();
        f.Volumes.Resolver = _ => new VolumeInfo(true, true, long.MaxValue / 2, "C:");
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        Assert.Contains(PrecheckProblem.SameVolume, f.Engine.Precheck(r));

        f.Volumes.Resolver = new FakeVolumes().Resolver;
        f.Engine.Run(r);
        var again = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        Assert.Equal([PrecheckProblem.SourceAlreadyLinked], f.Engine.Precheck(again));
    }

    [Fact]
    public void Health_check_detects_missing_target()
    {
        using var f = new Fixture();
        var r = f.Engine.Create(f.Source, f.TargetRoot, "zalo");
        f.Engine.Run(r);
        Directory.Move(r.Target, r.Target + "-gone");

        Assert.Equal(RelocationHealth.TargetMissing, f.Engine.Check(r));
        Assert.Throws<InvalidOperationException>(() => f.Engine.DeleteBackup(r));
    }

    [Fact]
    public void DeleteTree_does_not_delete_link_target_contents()
    {
        using var t = new TempDir();
        var keep = t.Dir("keep");
        t.File("keep/important.txt");
        var trash = t.Dir("trash");
        Directory.CreateSymbolicLink(Path.Combine(trash, "link"), keep);

        SafeFileWalker.DeleteTree(trash);

        Assert.False(Directory.Exists(trash));
        Assert.True(File.Exists(Path.Combine(keep, "important.txt")));
    }
}

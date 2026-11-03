using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Relocation;

public enum RelocationState
{
    Planned,
    Copying,
    Copied,
    SourceRenamed,
    Linked,
    /// <summary>Đã xong, còn giữ bản backup *.mwbak ở chỗ cũ.</summary>
    Completed,
    /// <summary>Đã xong và đã xoá backup.</summary>
    BackupDeleted,
    RolledBack,
    Failed,
}

public sealed class RelocationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? AppId { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Backup { get; set; } = "";
    public RelocationState State { get; set; } = RelocationState.Planned;
    /// <summary>Bước đang làm khi bị lỗi, để biết cách làm tiếp/hoàn tác.</summary>
    public RelocationState? FailedAt { get; set; }
    public long Bytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? Error { get; set; }
}

public interface IRelocationStore
{
    void Save(RelocationRecord record);
}

public enum PrecheckProblem
{
    SourceMissing,
    SourceAlreadyLinked,
    SameVolume,
    TargetVolumeMissing,
    TargetNotNtfs,
    TargetNotFixed,
    NotEnoughSpace,
    TargetNotEmpty,
    TargetInsideSource,
}

public enum RelocationHealth { Healthy, TargetMissing, LinkMissing, LinkWrongTarget, NotApplicable }

/// <summary>
/// Chuyển thư mục data sang ổ khác và để lại junction ở chỗ cũ. Mỗi bước được lưu trạng thái để có thể
/// làm tiếp hoặc hoàn tác nếu bị ngắt giữa chừng (mất điện, app bị kill…).
/// Trình tự: copy → kiểm tra → đổi tên nguồn thành *.mwbak → tạo junction → xác minh.
/// </summary>
public sealed class RelocationEngine(
    ILinkApi links, IVolumeApi volumes, IDirectoryCopier copier, IRelocationStore store, IClock clock)
{
    public const string BackupSuffix = ".mwbak";
    /// <summary>Chừa thêm 1 GB cho ổ đích sau khi chuyển.</summary>
    public const long SpareBytes = 1L << 30;

    public RelocationRecord Create(string source, string targetRoot, string? appId)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var target = Path.Combine(Path.GetFullPath(targetRoot), appId ?? "Custom", Path.GetFileName(source));
        var now = clock.UtcNow;
        return new RelocationRecord
        {
            AppId = appId,
            Source = source,
            Target = target,
            Backup = source + BackupSuffix,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    public List<PrecheckProblem> Precheck(RelocationRecord r)
    {
        var problems = new List<PrecheckProblem>();
        if (!Directory.Exists(r.Source)) { problems.Add(PrecheckProblem.SourceMissing); return problems; }
        if (links.GetLinkTarget(r.Source) != null) { problems.Add(PrecheckProblem.SourceAlreadyLinked); return problems; }
        if (SafeFileWalker.IsUnder(r.Target, r.Source)) problems.Add(PrecheckProblem.TargetInsideSource);

        var src = volumes.Get(r.Source);
        var dst = volumes.Get(r.Target);
        if (dst == null) { problems.Add(PrecheckProblem.TargetVolumeMissing); return problems; }
        if (src != null && string.Equals(src.Root, dst.Root, StringComparison.OrdinalIgnoreCase))
            problems.Add(PrecheckProblem.SameVolume);
        if (!dst.IsNtfs) problems.Add(PrecheckProblem.TargetNotNtfs);
        if (!dst.IsFixed) problems.Add(PrecheckProblem.TargetNotFixed);

        r.Bytes = SafeFileWalker.Measure(r.Source).Bytes;
        if (dst.FreeBytes < r.Bytes + SpareBytes) problems.Add(PrecheckProblem.NotEnoughSpace);
        if (Directory.Exists(r.Target) && Directory.EnumerateFileSystemEntries(r.Target).Any())
            problems.Add(PrecheckProblem.TargetNotEmpty);
        return problems;
    }

    /// <summary>Chạy (hoặc chạy tiếp) tới khi Completed. Ném lỗi thì record ở trạng thái Failed, gọi Rollback để quay lại.</summary>
    public void Run(RelocationRecord r, CancellationToken ct = default)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                switch (r.State)
                {
                    case RelocationState.Planned:
                    case RelocationState.Copying:
                        Set(r, RelocationState.Copying);
                        // Bị ngắt giữa lúc copy: xoá bản dở dang ở đích rồi copy lại từ đầu.
                        if (Directory.Exists(r.Target)) SafeFileWalker.DeleteTree(r.Target);
                        Directory.CreateDirectory(Path.GetDirectoryName(r.Target)!);
                        copier.Copy(r.Source, r.Target, ct);
                        Verify(r.Source, r.Target, ct);
                        Set(r, RelocationState.Copied);
                        break;

                    case RelocationState.Copied:
                        if (Directory.Exists(r.Backup))
                            throw new IOException($"Đã có thư mục backup {r.Backup}");
                        // Đổi tên cùng ổ: nếu app còn mở file bên trong thì sẽ lỗi ở đây, chưa mất gì.
                        Directory.Move(r.Source, r.Backup);
                        Set(r, RelocationState.SourceRenamed);
                        break;

                    case RelocationState.SourceRenamed:
                        if (!Directory.Exists(r.Source))
                            links.CreateDirectoryLink(r.Source, r.Target);
                        Set(r, RelocationState.Linked);
                        break;

                    case RelocationState.Linked:
                        if (Check(r) != RelocationHealth.Healthy)
                            throw new IOException("Junction không trỏ đúng thư mục đích");
                        Set(r, RelocationState.Completed);
                        return;

                    case RelocationState.Failed:
                        // Làm tiếp từ bước bị lỗi. Lỗi lúc đổi tên nguồn (app còn mở file) nghĩa là data có thể
                        // đã thay đổi sau khi copy → copy lại từ đầu cho chắc.
                        r.State = r.FailedAt is null or RelocationState.Copied ? RelocationState.Planned : r.FailedAt.Value;
                        r.Error = null;
                        break;

                    default:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r.FailedAt = r.State;
            r.Error = ex.Message;
            Set(r, RelocationState.Failed);
            throw;
        }
    }

    /// <summary>Đưa data về chỗ cũ, gỡ junction. Dựa vào hiện trạng ổ đĩa nên an toàn với mọi trạng thái.</summary>
    public void Rollback(RelocationRecord r, CancellationToken ct = default)
    {
        if (links.GetLinkTarget(r.Source) != null) links.RemoveLink(r.Source);

        if (Directory.Exists(r.Backup))
        {
            // Còn backup: chỉ cần đổi tên lại, bản ở đích bỏ đi.
            if (Directory.Exists(r.Source))
                throw new IOException($"Chỗ cũ {r.Source} đã có thư mục thật, không ghi đè. Hãy kiểm tra thủ công.");
            Directory.Move(r.Backup, r.Source);
            if (Directory.Exists(r.Target)) SafeFileWalker.DeleteTree(r.Target);
        }
        else if (!Directory.Exists(r.Source))
        {
            // Backup đã xoá: data chỉ còn ở đích → copy về, kiểm tra khớp rồi mới xoá đích.
            if (!Directory.Exists(r.Target)) throw new IOException($"Không tìm thấy data ở {r.Target}");
            copier.Copy(r.Target, r.Source, ct);
            Verify(r.Target, r.Source, ct);
            SafeFileWalker.DeleteTree(r.Target);
        }
        else if (Directory.Exists(r.Target))
        {
            // Nguồn chưa bị đổi tên (lỗi lúc copy): bản ở đích chỉ là bản copy dở.
            SafeFileWalker.DeleteTree(r.Target);
        }

        r.Error = null;
        r.FailedAt = null;
        Set(r, RelocationState.RolledBack);
    }

    public void DeleteBackup(RelocationRecord r)
    {
        if (r.State != RelocationState.Completed) throw new InvalidOperationException("Chỉ xoá backup khi đã chuyển xong");
        if (Check(r) != RelocationHealth.Healthy) throw new InvalidOperationException("Junction đang lỗi, không xoá backup");
        if (Directory.Exists(r.Backup)) SafeFileWalker.DeleteTree(r.Backup);
        Set(r, RelocationState.BackupDeleted);
    }

    public RelocationHealth Check(RelocationRecord r)
    {
        if (r.State is not (RelocationState.Linked or RelocationState.Completed or RelocationState.BackupDeleted))
            return RelocationHealth.NotApplicable;
        if (!Directory.Exists(r.Target)) return RelocationHealth.TargetMissing;
        var target = links.GetLinkTarget(r.Source);
        if (target == null) return RelocationHealth.LinkMissing;
        return SamePath(target, r.Target) ? RelocationHealth.Healthy : RelocationHealth.LinkWrongTarget;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Verify(string source, string target, CancellationToken ct)
    {
        var (sb, sf) = SafeFileWalker.Measure(source, ct);
        var (tb, tf) = SafeFileWalker.Measure(target, ct);
        if (sb != tb || sf != tf)
            throw new IOException($"Bản copy không khớp: nguồn {sf} file/{sb} byte, đích {tf} file/{tb} byte");
    }

    private void Set(RelocationRecord r, RelocationState s)
    {
        r.State = s;
        r.UpdatedUtc = clock.UtcNow;
        store.Save(r);
    }
}

/// <summary>Copy bằng .NET, giữ thời gian sửa đổi và thuộc tính. Bản Windows dùng robocopy để giữ cả ACL.</summary>
public sealed class ManagedDirectoryCopier : IDirectoryCopier
{
    public void Copy(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var f in SafeFileWalker.EnumerateFiles(source, true, null, ct))
        {
            var rel = Path.GetRelativePath(source, f.Path);
            var dst = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f.Path, dst, overwrite: true);
            File.SetLastWriteTimeUtc(dst, f.LastWriteUtc);
        }
        // Thư mục rỗng cũng phải giữ: một số app cần sẵn cấu trúc.
        foreach (var d in EnumerateDirsRecursive(source))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, d)));
    }

    private static IEnumerable<string> EnumerateDirsRecursive(string root)
    {
        foreach (var d in SafeFileWalker.EnumerateDirectories(root))
        {
            yield return d;
            foreach (var x in EnumerateDirsRecursive(d)) yield return x;
        }
    }
}

using ManagerWindows.Core.Abstractions;

namespace ManagerWindows.Core.Tests;

public sealed class FakeClock(DateTime now) : IClock
{
    public DateTime UtcNow { get; set; } = now;
}

/// <summary>Thư mục tạm tự xoá sau test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mwtest-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string rel, int bytes = 10, DateTime? lastWriteUtc = null, byte fill = 0)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        var data = new byte[bytes];
        Array.Fill(data, fill);
        System.IO.File.WriteAllBytes(p, data);
        if (lastWriteUtc != null)
        {
            System.IO.File.SetLastWriteTimeUtc(p, lastWriteUtc.Value);
            System.IO.File.SetCreationTimeUtc(p, lastWriteUtc.Value);
            System.IO.File.SetLastAccessTimeUtc(p, lastWriteUtc.Value);
        }
        return p;
    }

    public string Dir(string rel)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { SafeFileWalker.DeleteTree(Path); } catch { }
    }
}

/// <summary>Trên Linux dùng symlink thay junction để test logic chuyển data.</summary>
public sealed class SymlinkApi : ILinkApi
{
    public void CreateDirectoryLink(string linkPath, string targetPath) => Directory.CreateSymbolicLink(linkPath, targetPath);

    public string? GetLinkTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists && info.LinkTarget == null) return null;
        return info.LinkTarget;
    }

    public void RemoveLink(string linkPath) => Directory.Delete(linkPath, false);
}

public sealed class FakeVolumes : IVolumeApi
{
    public Func<string, VolumeInfo?> Resolver { get; set; } =
        p => new VolumeInfo(true, true, long.MaxValue / 2, p.Contains("dst") ? "D:" : "C:");

    public VolumeInfo? Get(string path) => Resolver(path);
}

public sealed class MemoryStore : ManagerWindows.Core.Relocation.IRelocationStore
{
    public List<ManagerWindows.Core.Relocation.RelocationState> History { get; } = new();
    public void Save(ManagerWindows.Core.Relocation.RelocationRecord record) => History.Add(record.State);
}

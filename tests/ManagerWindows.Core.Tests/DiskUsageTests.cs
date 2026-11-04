using ManagerWindows.Core.DiskUsage;

namespace ManagerWindows.Core.Tests;

public class DiskUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-du-" + Guid.NewGuid().ToString("N"));

    public DiskUsageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void File(string rel, int size)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[size]);
    }

    [Fact]
    public void Sums_tree_sorts_largest_first_and_skips_links()
    {
        File("small/a.txt", 100);
        File("big/b.bin", 5000);
        File("big/deep/c.bin", 3000);
        File("mid/d.bin", 2000);
        File("root.txt", 10);
        // Junction/symlink tới thư mục khác: không được đếm trùng.
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "big"));

        var r = DiskUsageScanner.Scan(_root, topFiles: 2);

        Assert.Equal(10_110, r.Root.Size);
        Assert.Equal(5, r.Root.FileCount);
        Assert.Equal(["big", "mid", "small", "link"], r.Root.Children.Select(c => c.Name));
        var big = r.Root.Children[0];
        Assert.Equal(8000, big.Size);
        Assert.Equal(5000, big.FilesSize);
        Assert.True(r.Root.Children[3].IsLink);
        Assert.Equal(0, r.Root.Children[3].Size);
        Assert.Equal([5000L, 3000L], r.TopFiles.Select(f => f.Size));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void Lists_direct_files_and_subtracts_after_delete()
    {
        File("x/one.bin", 300);
        File("x/two.bin", 700);
        File("x/sub/three.bin", 50);
        var r = DiskUsageScanner.Scan(_root);
        var x = r.Root.Children.Single();

        Assert.Equal([700L, 300L], DiskUsageScanner.ListFiles(x.FullPath).Select(f => f.Size));

        x.Remove(x.Children.Single());
        Assert.Equal(1000, x.Size);
        Assert.Equal(1000, r.Root.Size);
        Assert.Equal(2, r.Root.FileCount);

        x.Subtract(700, 1, 0);
        Assert.Equal(300, r.Root.Size);
    }

    [Fact]
    public void Cancelled_scan_returns_partial_totals()
    {
        File("a/f.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = DiskUsageScanner.Scan(_root, ct: cts.Token);
        Assert.True(r.Cancelled);
        Assert.Equal(0, r.Root.Size);
    }
}

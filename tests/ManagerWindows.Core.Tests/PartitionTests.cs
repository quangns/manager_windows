using ManagerWindows.Core.Partitions;
using static ManagerWindows.Core.Partitions.DiskLayout;

namespace ManagerWindows.Core.Tests;

public class PartitionTests
{
    private const string Efi = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string Msr = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    private const string Data = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const string Rec = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";

    private static PartitionInfo P(uint n, ulong offset, ulong size, string type, char? letter = null, bool boot = false, bool system = false, string? fs = "NTFS") =>
        new(0, n, letter, offset, size, type, 0, boot, system, false, fs, null, null);

    /// <summary>Đĩa GPT 100 GB kiểu Windows: EFI, MSR, C:, Recovery, chừa trống 20 GB ở giữa và cuối.</summary>
    private static DiskInfo SampleDisk() => new(0, "SSD", 100 * GiB, PartitionStyle.Gpt, true, true, false, false, "NVMe",
    [
        P(1, MiB, 100 * MiB, Efi, system: true, fs: "FAT32"),
        P(2, 101 * MiB, 16 * MiB, Msr, fs: null),
        P(3, 117 * MiB, 50 * GiB, Data, 'C', boot: true),
        P(4, 117 * MiB + 70 * GiB, 1 * GiB, Rec),
        P(5, 117 * MiB + 71 * GiB, 10 * GiB, Data, 'D'),
    ]);

    [Fact]
    public void Segments_include_unallocated_gaps_in_disk_order()
    {
        var segs = Segments(SampleDisk());
        Assert.Equal(7, segs.Count);
        Assert.True(segs[3].IsFree);                       // 20 GB giữa C: và Recovery
        Assert.Equal(20 * GiB, segs[3].Size);
        Assert.True(segs[6].IsFree);                       // phần cuối đĩa (trừ 1 MB GPT dự phòng)
        Assert.Equal(100 * GiB - MiB - (117 * MiB + 81 * GiB), segs[6].Size);
        Assert.Equal(segs.OrderBy(s => s.Offset).Select(s => s.Offset), segs.Select(s => s.Offset));
    }

    [Fact]
    public void Raw_disk_is_one_free_segment_that_can_only_be_initialized()
    {
        var raw = new DiskInfo(1, "USB", 32 * GiB, PartitionStyle.Raw, false, false, false, false, "USB", []);
        var seg = Assert.Single(Segments(raw));
        Assert.True(seg.IsFree);
        Assert.True(Check(PartitionOp.Initialize, seg).Allowed);
        Assert.Equal("Part_Deny_RawDisk", Check(PartitionOp.Create, seg).ReasonKey);
    }

    [Fact]
    public void Windows_and_special_partitions_are_protected()
    {
        var segs = Segments(SampleDisk());
        var efi = segs[0]; var msr = segs[1]; var c = segs[2]; var free = segs[3]; var rec = segs[4]; var d = segs[5];

        foreach (var op in new[] { PartitionOp.Delete, PartitionOp.Format, PartitionOp.Resize, PartitionOp.ChangeLetter })
        {
            Assert.False(Check(op, efi).Allowed);
            Assert.False(Check(op, msr).Allowed);
            Assert.False(Check(op, rec).Allowed);
        }
        // C: chỉ được thu nhỏ / mở rộng / đổi nhãn / kiểm tra lỗi.
        Assert.Equal("Part_Deny_Windows", Check(PartitionOp.Delete, c, 'C').ReasonKey);
        Assert.Equal("Part_Deny_Windows", Check(PartitionOp.Format, c, 'C').ReasonKey);
        Assert.Equal("Part_Deny_Windows", Check(PartitionOp.ChangeLetter, c, 'C').ReasonKey);
        Assert.True(Check(PartitionOp.Resize, c, 'C').Allowed);
        Assert.True(Check(PartitionOp.Label, c, 'C').Allowed);
        // D: dữ liệu thường: được hết, trừ khi app đang chạy từ D:.
        Assert.True(Check(PartitionOp.Delete, d, 'C').Allowed);
        Assert.Equal("Part_Deny_App", Check(PartitionOp.Format, d, 'C', appLetter: 'D').ReasonKey);
        // Vùng trống: chỉ tạo phân vùng.
        Assert.True(Check(PartitionOp.Create, free).Allowed);
        Assert.False(Check(PartitionOp.Delete, free).Allowed);
        Assert.False(Check(PartitionOp.Create, d).Allowed);
    }

    [Fact]
    public void Offline_or_readonly_disks_block_changes()
    {
        var ro = SampleDisk() with { IsReadOnly = true };
        var seg = Segments(ro)[5];
        Assert.Equal("Part_Deny_ReadOnly", Check(PartitionOp.Delete, seg).ReasonKey);
        var off = SampleDisk() with { IsOffline = true };
        Assert.Equal("Part_Deny_Offline", Check(PartitionOp.Resize, Segments(off)[5]).ReasonKey);
    }

    [Fact]
    public void Resize_needs_ntfs_or_refs()
    {
        var disk = SampleDisk() with { Partitions = [P(1, MiB, 10 * GiB, Data, 'E', fs: "exFAT")] };
        Assert.Equal("Part_Deny_ResizeFs", Check(PartitionOp.Resize, Segments(disk)[0]).ReasonKey);
    }

    [Fact]
    public void Mbr_disk_with_four_primaries_cannot_create_more()
    {
        var mbr = new DiskInfo(2, "HDD", 500 * GiB, PartitionStyle.Mbr, false, false, false, false, "SATA",
        [
            new(2, 1, 'E', MiB, 10 * GiB, null, 0x07, false, false, false, "NTFS", null, null),
            new(2, 2, 'F', 11 * GiB, 10 * GiB, null, 0x07, false, false, false, "NTFS", null, null),
            new(2, 3, 'G', 21 * GiB, 10 * GiB, null, 0x07, false, false, false, "NTFS", null, null),
            new(2, 4, 'H', 31 * GiB, 10 * GiB, null, 0x07, false, false, false, "NTFS", null, null),
        ]);
        var free = Segments(mbr).Last();
        Assert.True(free.IsFree);
        Assert.Equal("Part_Deny_MbrFull", Check(PartitionOp.Create, free).ReasonKey);
    }

    [Theory]
    [InlineData("120", 120.0 * GiB)]
    [InlineData("120,5", 120.5 * GiB)]
    [InlineData("500 MB", 500.0 * MiB)]
    [InlineData("1.5tb", 1.5 * 1024 * GiB)]
    public void Parses_sizes(string text, double expected) => Assert.Equal((ulong)Math.Round(expected), ParseSize(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    public void Rejects_bad_sizes(string text) => Assert.Null(ParseSize(text));

    [Fact]
    public void Free_letters_skip_used_and_floppy_letters() =>
        Assert.Equal(['E', 'G', 'H'], FreeLetters(['c', 'D', 'F']).Take(3));
}

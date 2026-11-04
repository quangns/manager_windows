using System.Buffers.Binary;
using System.Text;
using ManagerWindows.Core.Partitions;
using static ManagerWindows.Core.Partitions.DiskLayout;

namespace ManagerWindows.Core.Tests;

/// <summary>Ổ đĩa giả lập trong bộ nhớ; có thể "mất điện" sau N lần ghi (ghi dở một nửa đoạn rồi ném lỗi).</summary>
internal sealed class MemoryDisk(int size, int sectorSize = 512) : IBlockDevice
{
    public readonly byte[] Data = new byte[size];
    public int SectorSize { get; } = sectorSize;
    public int FailAfterWrites { get; set; } = -1;
    public bool CorruptWrites { get; set; }
    private int _writes;

    public void Read(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Check(offset, buffer.Length);
        if (FailAfterWrites >= 0 && _writes++ >= FailAfterWrites)
        {
            buffer[..(buffer.Length / 2)].CopyTo(Data.AsSpan((int)offset)); // ghi dở một nửa
            throw new IOException("mất điện (giả lập)");
        }
        buffer.CopyTo(Data.AsSpan((int)offset));
        if (CorruptWrites) Data[(int)offset] ^= 0xFF;
    }

    public void Flush() { }

    private void Check(long offset, int length)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0) throw new ArgumentException("không thẳng hàng sector");
    }
}

public class PartitionMoveTests
{
    private static byte[] Pattern(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(64 * 1024, 16 * 1024, 40 * 1024)]  // sang trái, chồng lấn (khoảng dời < độ dài)
    [InlineData(16 * 1024, 64 * 1024, 40 * 1024)]  // sang phải, chồng lấn
    [InlineData(0, 512, 100 * 1024)]               // dời đúng 1 sector: đoạn = 512
    [InlineData(200 * 1024, 0, 50 * 1024)]         // không chồng lấn
    public void Moves_data_exactly_even_when_ranges_overlap(int from, int to, int length)
    {
        var disk = new MemoryDisk(300 * 1024);
        var data = Pattern(length, 1);
        data.CopyTo(disk.Data, from);
        var chunk = BlockMover.ChunkFor(from, to, 512);
        Assert.True(chunk <= Math.Abs(to - from));

        long last = 0;
        BlockMover.Copy(disk, from, to, length, chunk, 0, d => last = d, CancellationToken.None);

        Assert.Equal(length, last);
        Assert.Equal(data, disk.Data.AsSpan(to, length).ToArray());
    }

    [Theory]
    [InlineData(80 * 1024, 32 * 1024)]
    [InlineData(32 * 1024, 80 * 1024)]
    public void Resumes_after_power_loss_in_the_middle_of_a_chunk(int from, int to)
    {
        const int length = 120 * 1024;
        var disk = new MemoryDisk(300 * 1024);
        var data = Pattern(length, 7);
        data.CopyTo(disk.Data, from);
        var chunk = BlockMover.ChunkFor(from, to, 512);

        // Lần 1: "mất điện" ở lần ghi thứ 3 (đoạn đó chỉ ghi được một nửa). Nhật ký chỉ lưu đoạn đã xong.
        long journal = 0;
        disk.FailAfterWrites = 2;
        Assert.Throws<IOException>(() => BlockMover.Copy(disk, from, to, length, chunk, 0, d => journal = d, CancellationToken.None));
        Assert.Equal(2L * chunk, journal);

        // Lần 2: tiếp tục từ nhật ký → dữ liệu đúng hoàn toàn.
        disk.FailAfterWrites = -1;
        BlockMover.Copy(disk, from, to, length, chunk, journal, d => journal = d, CancellationToken.None);
        Assert.Equal(data, disk.Data.AsSpan(to, length).ToArray());
    }

    [Fact]
    public void Verification_stops_on_bad_write()
    {
        var disk = new MemoryDisk(64 * 1024) { CorruptWrites = true };
        Assert.Throws<IOException>(() => BlockMover.Copy(disk, 8192, 0, 16384, 4096, 0, _ => { }, CancellationToken.None));
    }

    [Fact]
    public void Rejects_unsafe_chunk_and_misaligned_ranges()
    {
        var disk = new MemoryDisk(64 * 1024);
        Assert.Throws<ArgumentException>(() => BlockMover.Copy(disk, 4096, 0, 8192, 8192, 0, _ => { }, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => BlockMover.Copy(disk, 100, 0, 8192, 512, 0, _ => { }, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => BlockMover.ChunkFor(0, 0, 512));
        Assert.Equal(BlockMover.MaxChunk, BlockMover.ChunkFor(0, 10L << 30, 512));
    }

    private static byte[] Layout(int style, params (uint Number, long Offset, long Length)[] parts)
    {
        var b = new byte[LayoutPatch.HeaderSize + parts.Length * LayoutPatch.EntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(b, style);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var e = b.AsSpan(LayoutPatch.HeaderSize + i * LayoutPatch.EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(e, style);
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], parts[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e[16..], parts[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], parts[i].Number);
        }
        return b;
    }

    [Fact]
    public void Patches_partition_offset_in_gpt_and_mbr_layouts()
    {
        var gpt = Layout(LayoutPatch.StyleGpt, (1, (long)MiB, 100 * (long)MiB), (2, 200 * (long)MiB, 50 * (long)MiB));
        Assert.True(LayoutPatch.SetStartingOffset(gpt, 2, 150 * (long)MiB, 512));
        Assert.Equal((150 * (long)MiB, 50 * (long)MiB), LayoutPatch.Find(gpt, 2));
        Assert.Equal(((long)MiB, 100 * (long)MiB), LayoutPatch.Find(gpt, 1));
        Assert.False(LayoutPatch.SetStartingOffset(gpt, 9, 0, 512));

        var mbr = Layout(LayoutPatch.StyleMbr, (1, (long)MiB, 100 * (long)MiB));
        Assert.True(LayoutPatch.SetStartingOffset(mbr, 1, 2 * (long)MiB, 512));
        var e = mbr.AsSpan(LayoutPatch.HeaderSize);
        Assert.Equal(1, e[28]);                                                     // RewritePartition
        Assert.Equal((uint)(2 * MiB / 512), BinaryPrimitives.ReadUInt32LittleEndian(e[36..])); // HiddenSectors
    }

    [Fact]
    public void Patches_ntfs_boot_sector_and_refuses_bitlocker()
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(sector, 3);
        sector[510] = 0x55; sector[511] = 0xAA;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(0x28), 1000);
        Assert.True(LayoutPatch.PatchNtfsHiddenSectors(sector, 4 * (long)MiB, 512));
        Assert.Equal((uint)(4 * MiB / 512), BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x1C)));
        Assert.Equal(1000, LayoutPatch.NtfsTotalSectors(sector));

        var bl = new byte[512];
        Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(bl, 3);
        Assert.True(LayoutPatch.IsBitLocker(bl));
        Assert.False(LayoutPatch.IsNtfsBootSector(bl));
        Assert.False(LayoutPatch.PatchNtfsHiddenSectors(bl, 0, 512));
    }

    [Fact]
    public void Journal_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mw-journal-{Guid.NewGuid():N}.json");
        try
        {
            var j = new MoveJournal(1, 3, 100, 50, 1000, 400, 50, "copy", 'E', @"\\?\Volume{x}\", DateTime.UtcNow);
            j.Save(path);
            Assert.Equal(j, MoveJournal.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ---------- Lập kế hoạch ----------

    private const string Data = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const string Rec = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";

    private static PartitionInfo P(uint n, ulong offset, ulong size, char? letter, string type = Data, bool boot = false, string fs = "NTFS", ulong? free = null) =>
        new(0, n, letter, offset, size, type, 0, boot, false, false, fs, null, free);

    /// <summary>C: | Recovery | D: | 20 GB trống ở cuối. (Kịch bản hay gặp: Recovery chặn việc mở rộng C:.)</summary>
    private static DiskInfo Disk() => new(0, "SSD", 200 * GiB, PartitionStyle.Gpt, true, true, false, false, "NVMe",
    [
        P(1, MiB, 100 * GiB, 'C', boot: true, free: 30 * GiB),
        P(2, MiB + 100 * GiB, GiB, null, Rec),
        P(3, MiB + 101 * GiB, 50 * GiB, 'D', free: 40 * GiB),
    ]);

    private static DiskSegment Seg(DiskInfo d, uint n) => Segments(d).First(s => s.Partition?.Number == n);

    [Fact]
    public void Can_move_data_and_recovery_but_never_windows()
    {
        var d = Disk();
        Assert.Equal("Part_Deny_MoveWindows", MovePlanner.CanMove(Seg(d, 1), 'C').ReasonKey);
        Assert.True(MovePlanner.CanMove(Seg(d, 2), 'C').Allowed);
        Assert.True(MovePlanner.CanMove(Seg(d, 3), 'C').Allowed);
        var exfat = d with { Partitions = [P(1, MiB, 10 * GiB, 'E', fs: "exFAT")] };
        Assert.Equal("Part_Deny_MoveFs", MovePlanner.CanMove(Segments(exfat)[0]).ReasonKey);
    }

    [Fact]
    public void Plans_move_right_then_extend_left_partition()
    {
        var d = Disk();
        // D: dời sang phải sát cuối đĩa → 20 GB trống chuyển lên trước D:.
        var dSeg = Seg(d, 3);
        var (lo, hi) = MovePlanner.Bounds(dSeg);
        Assert.Equal(dSeg.Partition!.Offset, lo);
        var (steps, err) = MovePlanner.MoveResize(dSeg, hi - 50 * GiB, 50 * GiB, 0);
        Assert.Null(err);
        var move = Assert.Single(steps!);
        Assert.Equal(StepKind.Move, move.Kind);
        Assert.Equal(hi - 50 * GiB, move.Offset);
    }

    [Fact]
    public void Shrinks_before_moving_and_extends_after()
    {
        var d = Disk();
        var (steps, err) = MovePlanner.MoveResize(Seg(d, 3), MiB + 111 * GiB, 30 * GiB, 10 * GiB);
        Assert.Null(err);
        Assert.Equal([StepKind.Shrink, StepKind.Move], steps!.Select(s => s.Kind));
        var (steps2, _) = MovePlanner.MoveResize(Seg(d, 3), MiB + 101 * GiB, 60 * GiB, 0);
        Assert.Equal([StepKind.Extend], steps2!.Select(s => s.Kind));
        Assert.Equal("Part_Plan_OutOfRange", MovePlanner.MoveResize(Seg(d, 3), MiB + 101 * GiB, 100 * GiB, 0).Error);
        Assert.Equal("Part_Plan_TooSmall", MovePlanner.MoveResize(Seg(d, 3), MiB + 101 * GiB, 5 * GiB, 10 * GiB).Error);
    }

    [Fact]
    public void Extend_left_moves_to_free_space_start()
    {
        // E: có 10 GB trống phía trước.
        var d = new DiskInfo(1, "HDD", 100 * GiB, PartitionStyle.Gpt, false, false, false, false, "SATA",
            [P(1, MiB + 10 * GiB, 40 * GiB, 'E')]);
        var (steps, err) = MovePlanner.ExtendLeft(Segments(d).First(s => !s.IsFree));
        Assert.Null(err);
        Assert.Equal([StepKind.Move, StepKind.Extend], steps!.Select(s => s.Kind));
        Assert.Equal(MiB, steps![0].Offset);
        Assert.Equal(100 * GiB - MiB - MiB, steps[1].Size);   // tới hết vùng trống phía sau (trừ 1 MB GPT cuối đĩa)
        Assert.Equal("Part_Plan_NoSpaceBefore", MovePlanner.ExtendLeft(Seg(Disk(), 3)).Error);
    }

    [Fact]
    public void Plans_merge_into_left_and_right_neighbors()
    {
        var d = new DiskInfo(1, "HDD", 100 * GiB, PartitionStyle.Gpt, false, false, false, false, "SATA",
        [
            P(1, MiB, 30 * GiB, 'E', free: 25 * GiB),
            P(2, MiB + 30 * GiB, 20 * GiB, 'F', free: 15 * GiB),
        ]);
        // Gộp F (bên phải) vào E: chép → xoá F → mở rộng E.
        var (steps, err) = MovePlanner.Merge(Segments(d)[0], Segments(d)[1], "F-merged");
        Assert.Null(err);
        Assert.Equal([StepKind.CopyFiles, StepKind.Delete, StepKind.Extend], steps!.Select(s => s.Kind));
        Assert.Equal(@"E:\F-merged", steps![0].Target);
        // Gộp E (bên trái) vào F: thêm bước dời F sang trái.
        var (steps2, err2) = MovePlanner.Merge(Segments(d)[1], Segments(d)[0], "E-merged");
        Assert.Null(err2);
        Assert.Equal([StepKind.CopyFiles, StepKind.Delete, StepKind.Move, StepKind.Extend], steps2!.Select(s => s.Kind));
        Assert.Equal(MiB, steps2![2].Offset);
    }

    [Fact]
    public void Merge_rules()
    {
        var d = Disk();
        // C: + D: không liền kề (Recovery ở giữa).
        Assert.Equal("Part_Plan_NotAdjacent", MovePlanner.Merge(Seg(d, 1), Seg(d, 3), "D").Error);
        // Gộp vào C: phân vùng nằm bên trái thì phải dời C: → không cho.
        var d2 = new DiskInfo(0, "SSD", 100 * GiB, PartitionStyle.Gpt, true, true, false, false, "NVMe",
            [P(1, MiB, 10 * GiB, 'D', free: 9 * GiB), P(2, MiB + 10 * GiB, 50 * GiB, 'C', boot: true, free: 40 * GiB)]);
        Assert.Equal("Part_Plan_MergeLeftWindows", MovePlanner.Merge(Segments(d2)[1], Segments(d2)[0], "D").Error);
        // Đích không đủ chỗ cho dữ liệu của nguồn.
        var d3 = new DiskInfo(1, "HDD", 100 * GiB, PartitionStyle.Gpt, false, false, false, false, "SATA",
            [P(1, MiB, 10 * GiB, 'E', free: GiB), P(2, MiB + 10 * GiB, 20 * GiB, 'F', free: 5 * GiB)]);
        Assert.Equal("Part_Plan_NoRoom", MovePlanner.Merge(Segments(d3)[0], Segments(d3)[1], "F").Error);
    }
}

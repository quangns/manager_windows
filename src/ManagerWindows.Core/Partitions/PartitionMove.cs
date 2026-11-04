using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ManagerWindows.Core.Partitions;

/// <summary>Thiết bị đọc/ghi theo byte offset (ổ vật lý thật, hoặc file/bộ nhớ khi test). Offset và độ dài là bội số của SectorSize.</summary>
public interface IBlockDevice
{
    int SectorSize { get; }
    void Read(long offset, Span<byte> buffer);
    void Write(long offset, ReadOnlySpan<byte> buffer);
    void Flush();
}

/// <summary>
/// Nhật ký di chuyển phân vùng: ghi sau mỗi đoạn đã chép xong (và đã đọc lại kiểm tra), để mất điện / lỗi giữa chừng
/// vẫn tiếp tục được đúng chỗ. Stage: "copy" → "table" (đã chép xong, chưa sửa bảng phân vùng) → xoá file khi xong.
/// </summary>
public sealed record MoveJournal(
    uint Disk,
    uint Partition,
    long OldOffset,
    long NewOffset,
    long Length,
    long Done,
    int Chunk,
    string Stage,
    char? Letter,
    string? VolumePath,
    DateTime StartedUtc)
{
    public static MoveJournal? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<MoveJournal>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Ghi an toàn: file tạm + flush xuống đĩa + đổi tên (không để lại nhật ký ghi dở).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(fs, this);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Chép một dải sector sang vị trí mới trên cùng thiết bị, an toàn khi hai vùng chồng lên nhau và tiếp tục được sau sự cố.
/// - Dời sang trái (đích &lt; nguồn): chép từ đầu về cuối; dời sang phải: chép từ cuối về đầu.
/// - Mỗi đoạn không lớn hơn khoảng dời, nên ghi một đoạn không bao giờ đè lên phần nguồn chưa chép
///   (kể cả đoạn đang chép): mất điện giữa đoạn thì chép lại đoạn đó từ nguồn vẫn còn nguyên.
/// </summary>
public static class BlockMover
{
    public const int MaxChunk = 4 << 20;

    /// <summary>Kích thước đoạn: ≤ 4 MB, ≤ khoảng dời, bội số của sector.</summary>
    public static int ChunkFor(long from, long to, int sectorSize)
    {
        var distance = Math.Abs(to - from);
        if (distance == 0 || distance % sectorSize != 0) throw new ArgumentException("Khoảng dời phải khác 0 và là bội số của sector");
        var c = (int)Math.Min(MaxChunk, distance);
        return c / sectorSize * sectorSize;
    }

    /// <param name="done">Số byte đã chép xong theo chiều chép (tiếp tục từ nhật ký).</param>
    /// <param name="onChunk">Gọi sau mỗi đoạn đã ghi + đọc lại khớp, với tổng byte đã xong (để ghi nhật ký, báo tiến độ).</param>
    public static void Copy(IBlockDevice dev, long from, long to, long length, int chunk, long done,
        Action<long> onChunk, CancellationToken ct, bool verify = true)
    {
        var s = dev.SectorSize;
        if (from % s != 0 || to % s != 0 || length % s != 0 || chunk % s != 0 || chunk <= 0)
            throw new ArgumentException("Vị trí, độ dài và đoạn phải là bội số của sector");
        if (chunk > Math.Abs(to - from)) throw new ArgumentException("Đoạn lớn hơn khoảng dời: không an toàn khi bị gián đoạn");
        if (done < 0 || done > length || done % s != 0) throw new ArgumentException("Tiến độ không hợp lệ");

        var forward = to < from;
        var buf = new byte[chunk];
        var check = verify ? new byte[chunk] : null;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(chunk, length - done);
            // Vị trí tương đối của đoạn: từ đầu (sang trái) hoặc từ cuối (sang phải).
            var rel = forward ? done : length - done - n;
            var span = buf.AsSpan(0, n);
            dev.Read(from + rel, span);
            dev.Write(to + rel, span);
            if (check != null)
            {
                dev.Flush();
                var c = check.AsSpan(0, n);
                dev.Read(to + rel, c);
                if (!c.SequenceEqual(span))
                    throw new IOException($"Dữ liệu đọc lại không khớp tại byte {to + rel} — dừng để tránh hỏng thêm");
            }
            done += n;
            dev.Flush();
            onChunk(done);
        }
    }
}

/// <summary>Sửa bảng phân vùng (buffer DRIVE_LAYOUT_INFORMATION_EX của Windows) và boot sector NTFS sau khi dời phân vùng.</summary>
public static class LayoutPatch
{
    // Bố cục x64 của winioctl.h:
    //   DRIVE_LAYOUT_INFORMATION_EX: PartitionStyle(4) PartitionCount(4) union(40) PartitionEntry[] tại 48
    //   PARTITION_INFORMATION_EX (144 byte): PartitionStyle(4) pad(4) StartingOffset(8)@8 PartitionLength(8)@16
    //     PartitionNumber(4)@24 RewritePartition(1)@28 IsServicePartition(1)@29 pad union@32
    //     MBR: PartitionType@32 BootIndicator@33 Recognized@34 HiddenSectors(4)@36
    public const int HeaderSize = 48;
    public const int EntrySize = 144;
    public const int StyleMbr = 0, StyleGpt = 1;

    /// <summary>Đổi vị trí bắt đầu của phân vùng trong buffer bố cục; trả false nếu không tìm thấy hoặc buffer lạ.</summary>
    public static bool SetStartingOffset(byte[] layout, uint partitionNumber, long newOffset, int sectorSize)
    {
        if (layout.Length < HeaderSize) return false;
        var style = BinaryPrimitives.ReadInt32LittleEndian(layout);
        var count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
        if (style is not (StyleMbr or StyleGpt) || count <= 0 || layout.Length < HeaderSize + count * EntrySize) return false;
        var found = false;
        for (var i = 0; i < count; i++)
        {
            var e = layout.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            // MBR: Windows chỉ ghi lại mục có RewritePartition = TRUE.
            if (style == StyleMbr) e[28] = 1;
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[24..]) != partitionNumber) continue;
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            if (length <= 0) return false;
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], newOffset);
            if (style == StyleMbr)
            {
                var hidden = newOffset / sectorSize;
                if (hidden > uint.MaxValue) return false; // MBR không biểu diễn được (> 2 TB với sector 512)
                BinaryPrimitives.WriteUInt32LittleEndian(e[36..], (uint)hidden);
            }
            found = true;
        }
        return found;
    }

    /// <summary>Đọc (offset, độ dài) của một phân vùng trong buffer bố cục.</summary>
    public static (long Offset, long Length)? Find(byte[] layout, uint partitionNumber)
    {
        if (layout.Length < HeaderSize) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
        for (var i = 0; i < count && HeaderSize + (i + 1) * EntrySize <= layout.Length; i++)
        {
            var e = layout.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[24..]) == partitionNumber)
                return (BinaryPrimitives.ReadInt64LittleEndian(e[8..]), BinaryPrimitives.ReadInt64LittleEndian(e[16..]));
        }
        return null;
    }

    /// <summary>Boot sector NTFS thật (không phải BitLocker "-FVE-FS-", không phải FAT/exFAT).</summary>
    public static bool IsNtfsBootSector(ReadOnlySpan<byte> sector) =>
        sector.Length >= 512 && Encoding.ASCII.GetString(sector.Slice(3, 8)) == "NTFS    " && sector[510] == 0x55 && sector[511] == 0xAA;

    public static bool IsBitLocker(ReadOnlySpan<byte> sector) =>
        sector.Length >= 11 && Encoding.ASCII.GetString(sector.Slice(3, 8)) == "-FVE-FS-";

    /// <summary>Tổng số sector của volume NTFS (bản sao boot sector nằm ở sector cuối này).</summary>
    public static long NtfsTotalSectors(ReadOnlySpan<byte> sector) => BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);

    /// <summary>Ghi "hidden sectors" (vị trí phân vùng tính bằng sector) vào boot sector NTFS; không vừa 32 bit thì bỏ qua.</summary>
    public static bool PatchNtfsHiddenSectors(Span<byte> sector, long newOffset, int sectorSize)
    {
        if (!IsNtfsBootSector(sector)) return false;
        var hidden = newOffset / sectorSize;
        if (hidden > uint.MaxValue) return false;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x1C..], (uint)hidden);
        return true;
    }
}

public enum StepKind { Shrink, Move, Extend, CopyFiles, Delete }

/// <summary>Một bước của thao tác ghép (thu nhỏ → dời → mở rộng; hoặc gộp: chép file → xoá → dời → mở rộng).</summary>
public sealed record PlanStep(StepKind Kind, uint Disk, uint Partition, ulong Offset, ulong Size, string? Source = null, string? Target = null);

/// <summary>Lập kế hoạch dời / đổi kích thước / mở rộng sang trái / gộp phân vùng trên sơ đồ đĩa.</summary>
public static class MovePlanner
{
    /// <summary>Phân vùng có thể dời (chép sector) không: chỉ NTFS dữ liệu hoặc Recovery, không phải Windows/khởi động/app.</summary>
    public static OpCheck CanMove(DiskSegment seg, char? windowsLetter = null, char? appLetter = null)
    {
        if (seg.Partition is not { } p) return OpCheck.Deny("Part_Deny_Unallocated");
        if (seg.Disk.IsOffline) return OpCheck.Deny("Part_Deny_Offline");
        if (seg.Disk.IsReadOnly) return OpCheck.Deny("Part_Deny_ReadOnly");
        if (p.IsWindowsCritical || (windowsLetter != null && p.Letter == windowsLetter)) return OpCheck.Deny("Part_Deny_MoveWindows");
        if (appLetter != null && p.Letter == appLetter) return OpCheck.Deny("Part_Deny_App");
        if (p.Kind is not (PartitionKind.Data or PartitionKind.Recovery)) return OpCheck.Deny("Part_Deny_Special");
        if (p.FileSystem != "NTFS") return OpCheck.Deny("Part_Deny_MoveFs");
        if (seg.Disk.Style == PartitionStyle.Mbr && seg.Disk.Partitions.Any(x => x.Kind == PartitionKind.Extended && p.Offset >= x.Offset && p.End <= x.End))
            return OpCheck.Deny("Part_Deny_Logical");
        return OpCheck.Ok;
    }

    /// <summary>Khoảng trống quanh phân vùng: [đầu vùng trống phía trước, cuối vùng trống phía sau].</summary>
    public static (ulong Lo, ulong Hi) Bounds(DiskSegment seg)
    {
        var segs = DiskLayout.Segments(seg.Disk);
        var i = segs.FindIndex(s => s.Partition != null && s.Partition.Number == seg.Partition!.Number);
        var p = seg.Partition!;
        var lo = i > 0 && segs[i - 1].IsFree ? segs[i - 1].Offset : p.Offset;
        var hi = i >= 0 && i + 1 < segs.Count && segs[i + 1].IsFree ? segs[i + 1].Offset + segs[i + 1].Size : p.End;
        return (lo, hi);
    }

    private static ulong AlignUp(ulong v) => (v + DiskLayout.MiB - 1) / DiskLayout.MiB * DiskLayout.MiB;

    /// <summary>
    /// Dời và/hoặc đổi kích thước: phân vùng mới bắt đầu ở <paramref name="newStart"/>, dài <paramref name="newSize"/>.
    /// Thứ tự: thu nhỏ trước (chép ít hơn) → dời → mở rộng về phía sau.
    /// </summary>
    public static (List<PlanStep>? Steps, string? Error) MoveResize(DiskSegment seg, ulong newStart, ulong newSize, ulong minSize)
    {
        if (seg.Partition is not { } p) return (null, "Part_Deny_Unallocated");
        var (lo, hi) = Bounds(seg);
        newStart = AlignUp(newStart);
        newSize = newSize / DiskLayout.MiB * DiskLayout.MiB;
        if (newStart < lo || newStart + newSize > hi) return (null, "Part_Plan_OutOfRange");
        if (newSize < minSize) return (null, "Part_Plan_TooSmall");
        var steps = new List<PlanStep>();
        var size = p.Size;
        if (newSize < size)
        {
            steps.Add(new PlanStep(StepKind.Shrink, p.Disk, p.Number, p.Offset, newSize));
            size = newSize;
        }
        if (newStart != p.Offset)
        {
            if (CanMove(seg) is { Allowed: false } c) return (null, c.ReasonKey);
            steps.Add(new PlanStep(StepKind.Move, p.Disk, p.Number, newStart, size));
        }
        if (newSize > size) steps.Add(new PlanStep(StepKind.Extend, p.Disk, p.Number, newStart, newSize));
        return steps.Count == 0 ? (null, "Part_Plan_NoChange") : (steps, null);
    }

    /// <summary>Mở rộng sang trái: dời phân vùng về đầu vùng trống phía trước rồi mở rộng phía sau tới chỗ cũ (cả vùng trống sau nếu có).</summary>
    public static (List<PlanStep>? Steps, string? Error) ExtendLeft(DiskSegment seg)
    {
        if (seg.Partition is not { } p) return (null, "Part_Deny_Unallocated");
        var (lo, hi) = Bounds(seg);
        var start = AlignUp(lo);
        if (start >= p.Offset) return (null, "Part_Plan_NoSpaceBefore");
        return MoveResize(seg, start, hi - start, 0);
    }

    /// <summary>
    /// Gộp <paramref name="source"/> vào <paramref name="target"/> (hai phân vùng liền kề, có thể cách nhau vùng trống):
    /// chép file của nguồn vào thư mục <paramref name="folder"/> trên đích → xoá nguồn → (nguồn ở trước thì dời đích sang trái)
    /// → mở rộng đích chiếm cả chỗ của nguồn.
    /// </summary>
    public static (List<PlanStep>? Steps, string? Error) Merge(DiskSegment target, DiskSegment source, string folder)
    {
        if (target.Partition is not { } t || source.Partition is not { } s) return (null, "Part_Deny_Unallocated");
        if (t.Disk != s.Disk || t.Number == s.Number) return (null, "Part_Plan_NotAdjacent");
        if (t.Letter == null || s.Letter == null) return (null, "Part_NeedLetter");
        if (s.IsWindowsCritical || s.Kind != PartitionKind.Data) return (null, "Part_Deny_MergeSource");
        if (t.Kind != PartitionKind.Data || t.FileSystem is not ("NTFS" or "ReFS")) return (null, "Part_Deny_ResizeFs");
        // Liền kề: giữa hai phân vùng chỉ có vùng trống.
        var segs = DiskLayout.Segments(target.Disk);
        int ti = segs.FindIndex(x => x.Partition?.Number == t.Number), si = segs.FindIndex(x => x.Partition?.Number == s.Number);
        var between = segs.Skip(Math.Min(ti, si) + 1).Take(Math.Abs(ti - si) - 1);
        if (between.Any(x => !x.IsFree)) return (null, "Part_Plan_NotAdjacent");
        var sourceUsed = s.Size - (s.FreeBytes ?? 0);
        if (t.FreeBytes is { } free && free < sourceUsed + 512 * DiskLayout.MiB) return (null, "Part_Plan_NoRoom");

        var steps = new List<PlanStep>
        {
            new(StepKind.CopyFiles, t.Disk, t.Number, 0, sourceUsed, $"{s.Letter}:\\", $"{t.Letter}:\\{folder}"),
            new(StepKind.Delete, s.Disk, s.Number, s.Offset, s.Size),
        };
        // Sau khi xoá nguồn: vùng trống liền với đích. Nguồn ở trước → phải dời đích sang trái.
        var (lo, _) = Bounds(target);
        var start = Math.Min(t.Offset, AlignUp(Math.Min(lo, s.Offset)));
        if (si < ti)
        {
            if (CanMove(target) is { Allowed: false } c) return (null, c.ReasonKey == "Part_Deny_MoveWindows" ? "Part_Plan_MergeLeftWindows" : c.ReasonKey);
            steps.Add(new PlanStep(StepKind.Move, t.Disk, t.Number, start, t.Size));
        }
        else start = t.Offset;
        // GPT giữ 1 MB cuối đĩa; Windows tự giới hạn kích thước tối đa nên để "Extend" lấy hết (Size = 0).
        steps.Add(new PlanStep(StepKind.Extend, t.Disk, t.Number, start, 0));
        return (steps, null);
    }
}

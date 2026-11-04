using System.Globalization;

namespace ManagerWindows.Core.Partitions;

public enum PartitionStyle { Raw, Mbr, Gpt }

/// <summary>Loại phân vùng theo GPT type / MBR type.</summary>
public enum PartitionKind { Data, Efi, Msr, Recovery, Extended, Other }

public enum PartitionOp { Resize, Create, Delete, Format, ChangeLetter, Label, Check, Initialize }

public sealed record PartitionInfo(
    uint Disk,
    uint Number,
    char? Letter,
    ulong Offset,
    ulong Size,
    string? GptType,
    ushort MbrType,
    bool IsBoot,
    bool IsSystem,
    bool IsHidden,
    string? FileSystem,
    string? Label,
    ulong? FreeBytes,
    string? VolumePath = null)
{
    public ulong End => Offset + Size;

    public PartitionKind Kind => DiskLayout.KindOf(GptType, MbrType);

    /// <summary>Phân vùng chứa Windows / khởi động (C:) hoặc phân vùng hệ thống (EFI, System Reserved).</summary>
    public bool IsWindowsCritical => IsBoot || IsSystem;
}

public sealed record DiskInfo(
    uint Number,
    string Name,
    ulong Size,
    PartitionStyle Style,
    bool IsBoot,
    bool IsSystem,
    bool IsOffline,
    bool IsReadOnly,
    string Bus,
    IReadOnlyList<PartitionInfo> Partitions);

/// <summary>Một đoạn trên sơ đồ đĩa: phân vùng hoặc vùng chưa phân bổ.</summary>
public sealed record DiskSegment(DiskInfo Disk, PartitionInfo? Partition, ulong Offset, ulong Size)
{
    public bool IsFree => Partition == null;
}

/// <summary>Kết quả kiểm tra một thao tác: được phép hay không, và lý do (khoá chuỗi ngôn ngữ).</summary>
public readonly record struct OpCheck(bool Allowed, string? ReasonKey)
{
    public static readonly OpCheck Ok = new(true, null);
    public static OpCheck Deny(string key) => new(false, key);
}

/// <summary>
/// Sơ đồ đĩa và luật an toàn cho trình quản lý phân vùng. Không gọi Windows: dữ liệu đĩa đưa vào từ Platform,
/// nên kiểm thử được trên Linux.
/// </summary>
public static class DiskLayout
{
    public const ulong MiB = 1UL << 20;
    public const ulong GiB = 1UL << 30;

    /// <summary>Vùng trống nhỏ hơn mức này (căn lề 1 MB, header GPT…) không hiện, không tạo phân vùng được.</summary>
    public const ulong MinFree = 16 * MiB;

    private const string EfiGuid = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    private const string MsrGuid = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    private const string RecoveryGuid = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    private const string BasicDataGuid = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    public static PartitionKind KindOf(string? gptType, ushort mbrType)
    {
        if (!string.IsNullOrEmpty(gptType))
        {
            var g = gptType.Trim().ToLowerInvariant();
            if (!g.StartsWith('{')) g = "{" + g + "}";
            return g switch
            {
                EfiGuid => PartitionKind.Efi,
                MsrGuid => PartitionKind.Msr,
                RecoveryGuid => PartitionKind.Recovery,
                BasicDataGuid => PartitionKind.Data,
                _ => PartitionKind.Other,
            };
        }
        return mbrType switch
        {
            0x05 or 0x0F => PartitionKind.Extended,
            0x27 => PartitionKind.Recovery, // Windows RE ẩn trên MBR
            0x07 or 0x0B or 0x0C or 0x0E or 0x06 or 0x01 or 0x04 => PartitionKind.Data,
            0 => PartitionKind.Data,       // không rõ (vd. đĩa ảo): coi như dữ liệu
            _ => PartitionKind.Other,
        };
    }

    /// <summary>Các đoạn của đĩa theo thứ tự trên đĩa, gồm cả vùng chưa phân bổ ≥ <see cref="MinFree"/>.</summary>
    public static List<DiskSegment> Segments(DiskInfo disk)
    {
        var list = new List<DiskSegment>();
        if (disk.Style == PartitionStyle.Raw)
        {
            list.Add(new DiskSegment(disk, null, 0, disk.Size));
            return list;
        }
        // Phân vùng logic nằm trong phân vùng mở rộng (MBR): chỉ vẽ phân vùng logic, vùng trống trong container bỏ qua.
        var parts = disk.Partitions.OrderBy(p => p.Offset).ToList();
        var containers = parts.Where(p => p.Kind == PartitionKind.Extended).ToList();
        var visible = parts.Where(p => p.Kind != PartitionKind.Extended).ToList();
        var cursor = MiB; // phân vùng đầu tiên luôn bắt đầu từ 1 MB
        foreach (var p in visible)
        {
            if (p.Offset > cursor && p.Offset - cursor >= MinFree && !containers.Any(c => cursor >= c.Offset && cursor < c.End))
                list.Add(new DiskSegment(disk, null, cursor, p.Offset - cursor));
            list.Add(new DiskSegment(disk, p, p.Offset, p.Size));
            cursor = Math.Max(cursor, p.End);
        }
        foreach (var c in containers) cursor = Math.Max(cursor, c.End);
        // GPT giữ ~1 MB cuối đĩa cho bảng phân vùng dự phòng.
        var usableEnd = disk.Size > MiB ? disk.Size - MiB : disk.Size;
        if (usableEnd > cursor && usableEnd - cursor >= MinFree)
            list.Add(new DiskSegment(disk, null, cursor, usableEnd - cursor));
        return list;
    }

    /// <summary>
    /// Thao tác có được phép trên đoạn này không. Chặn mọi thao tác phá dữ liệu trên phân vùng của Windows, EFI, MSR,
    /// Recovery và phân vùng đang chứa chính app; chỉ cho thu nhỏ/mở rộng/đổi nhãn/kiểm tra phân vùng Windows (C:).
    /// </summary>
    public static OpCheck Check(PartitionOp op, DiskSegment seg, char? windowsLetter = null, char? appLetter = null)
    {
        var disk = seg.Disk;
        if (disk.IsOffline) return OpCheck.Deny("Part_Deny_Offline");
        if (disk.IsReadOnly && op != PartitionOp.Check) return OpCheck.Deny("Part_Deny_ReadOnly");

        if (op == PartitionOp.Initialize)
            return disk.Style != PartitionStyle.Raw ? OpCheck.Deny("Part_Deny_NotRaw")
                : disk.IsBoot || disk.IsSystem ? OpCheck.Deny("Part_Deny_System") : OpCheck.Ok;

        if (seg.Partition is not { } p)
        {
            if (op != PartitionOp.Create) return OpCheck.Deny("Part_Deny_Unallocated");
            if (disk.Style == PartitionStyle.Raw) return OpCheck.Deny("Part_Deny_RawDisk");
            // MBR: tối đa 4 phân vùng chính (không tạo phân vùng logic).
            if (disk.Style == PartitionStyle.Mbr && disk.Partitions.Count(x => x.Number > 0 && x.Number <= 4 && x.Kind != PartitionKind.Extended) +
                disk.Partitions.Count(x => x.Kind == PartitionKind.Extended) >= 4)
                return OpCheck.Deny("Part_Deny_MbrFull");
            return OpCheck.Ok;
        }

        if (op == PartitionOp.Create) return OpCheck.Deny("Part_Deny_NotFree");
        if (p.Kind is PartitionKind.Efi or PartitionKind.Msr or PartitionKind.Recovery or PartitionKind.Extended or PartitionKind.Other)
            return OpCheck.Deny("Part_Deny_Special");

        var isWindows = p.IsWindowsCritical || (windowsLetter != null && p.Letter == windowsLetter);
        var hostsApp = appLetter != null && p.Letter == appLetter;
        var hasFs = !string.IsNullOrEmpty(p.FileSystem);
        return op switch
        {
            PartitionOp.Delete or PartitionOp.Format when isWindows => OpCheck.Deny("Part_Deny_Windows"),
            PartitionOp.Delete or PartitionOp.Format when hostsApp => OpCheck.Deny("Part_Deny_App"),
            PartitionOp.ChangeLetter when isWindows => OpCheck.Deny("Part_Deny_Windows"),
            PartitionOp.ChangeLetter when hostsApp => OpCheck.Deny("Part_Deny_App"),
            PartitionOp.Resize when !hasFs => OpCheck.Deny("Part_Deny_NoFs"),
            PartitionOp.Resize when p.FileSystem is not ("NTFS" or "ReFS") => OpCheck.Deny("Part_Deny_ResizeFs"),
            PartitionOp.Label or PartitionOp.Check when !hasFs => OpCheck.Deny("Part_Deny_NoFs"),
            _ => OpCheck.Ok,
        };
    }

    /// <summary>"120", "120.5", "120,5 GB", "500 MB", "1 TB" → byte (mặc định GB). Null nếu không đọc được.</summary>
    public static ulong? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        ulong unit = GiB;
        foreach (var (suffix, mult) in new[] { ("TB", 1024 * GiB), ("GB", GiB), ("MB", MiB), ("T", 1024 * GiB), ("G", GiB), ("M", MiB) })
        {
            if (!t.EndsWith(suffix)) continue;
            unit = mult;
            t = t[..^suffix.Length];
            break;
        }
        t = t.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0 || double.IsInfinity(v)) return null;
        return (ulong)Math.Round(v * unit);
    }

    /// <summary>Ký tự ổ còn trống để gán (D–Z, bỏ A/B/C).</summary>
    public static List<char> FreeLetters(IEnumerable<char> used)
    {
        var taken = new HashSet<char>(used.Select(char.ToUpperInvariant));
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Where(c => !taken.Contains(c)).ToList();
    }
}

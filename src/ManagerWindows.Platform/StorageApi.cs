using System.Management;
using System.Runtime.Versioning;
using ManagerWindows.Core.Partitions;

namespace ManagerWindows.Platform;

/// <summary>Lỗi từ Windows Storage API (mã trả về + thông báo của Windows nếu có).</summary>
public sealed class StorageException(uint code, string message) : Exception(message)
{
    public uint Code { get; } = code;
}

/// <summary>
/// Quản lý phân vùng qua Windows Storage Management API (WMI root\Microsoft\Windows\Storage — cùng API với
/// Disk Management và PowerShell Get-Partition/Resize-Partition). Thao tác thay đổi cần quyền Administrator.
/// Không di chuyển dữ liệu kiểu sector: chỉ những gì Windows hỗ trợ an toàn (thu nhỏ/mở rộng về phía sau, tạo, xoá, format…).
/// </summary>
[SupportedOSPlatform("windows")]
public static class StorageApi
{
    private static ManagementScope Scope()
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        return scope;
    }

    private static List<ManagementObject> Query(ManagementScope scope, string wql)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        return s.Get().Cast<ManagementObject>().ToList();
    }

    public static List<DiskInfo> Load()
    {
        var scope = Scope();
        var disks = new List<DiskInfo>();
        var parts = Query(scope, "SELECT * FROM MSFT_Partition");
        foreach (var d in Query(scope, "SELECT * FROM MSFT_Disk"))
        {
            using (d)
            {
                var number = Convert.ToUInt32(d["Number"]);
                var style = Convert.ToUInt16(d["PartitionStyle"]) switch { 1 => PartitionStyle.Mbr, 2 => PartitionStyle.Gpt, _ => PartitionStyle.Raw };
                var list = parts.Where(p => Convert.ToUInt32(p["DiskNumber"]) == number).Select(ToPartition).ToList();
                disks.Add(new DiskInfo(
                    number,
                    (d["FriendlyName"] as string ?? d["Model"] as string ?? $"Disk {number}").Trim(),
                    Convert.ToUInt64(d["Size"]),
                    style,
                    d["IsBoot"] is true,
                    d["IsSystem"] is true,
                    d["IsOffline"] is true,
                    d["IsReadOnly"] is true,
                    BusName(Convert.ToUInt16(d["BusType"] ?? 0)),
                    list));
            }
        }
        foreach (var p in parts) p.Dispose();
        return disks.OrderBy(d => d.Number).ToList();
    }

    private static PartitionInfo ToPartition(ManagementObject p)
    {
        string? fs = null, label = null;
        ulong? free = null;
        foreach (ManagementObject v in p.GetRelated("MSFT_Volume"))
            using (v)
            {
                fs = v["FileSystem"] as string;
                label = v["FileSystemLabel"] as string;
                free = v["SizeRemaining"] is { } r ? Convert.ToUInt64(r) : null;
            }
        var letter = p["DriveLetter"] is char c && char.IsLetter(c) ? char.ToUpperInvariant(c) : (char?)null;
        return new PartitionInfo(
            Convert.ToUInt32(p["DiskNumber"]),
            Convert.ToUInt32(p["PartitionNumber"]),
            letter,
            Convert.ToUInt64(p["Offset"]),
            Convert.ToUInt64(p["Size"]),
            p["GptType"] as string,
            Convert.ToUInt16(p["MbrType"] ?? 0),
            p["IsBoot"] is true,
            p["IsSystem"] is true,
            p["IsHidden"] is true,
            string.IsNullOrEmpty(fs) ? null : fs,
            string.IsNullOrEmpty(label) ? null : label,
            free);
    }

    private static string BusName(ushort bus) => bus switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        15 => "Virtual", 17 => "NVMe", _ => "",
    };

    private static ManagementObject Partition(ManagementScope scope, uint disk, uint number) =>
        Query(scope, $"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk} AND PartitionNumber={number}").FirstOrDefault()
        ?? throw new StorageException(0, $"Không tìm thấy phân vùng {number} trên đĩa {disk}");

    private static ManagementObject Volume(ManagementObject partition) =>
        partition.GetRelated("MSFT_Volume").Cast<ManagementObject>().FirstOrDefault()
        ?? throw new StorageException(0, "Phân vùng chưa có volume (chưa format)");

    /// <summary>Gọi phương thức WMI; mã khác 0 → StorageException kèm thông báo chi tiết của Windows.</summary>
    private static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? fill = null)
    {
        var inParams = target.GetMethodParameters(method);
        fill?.Invoke(inParams);
        var outParams = target.InvokeMethod(method, inParams, null)!;
        var code = Convert.ToUInt32(outParams["ReturnValue"] ?? 0u);
        if (code == 0) return outParams;
        var detail = (outParams.Properties.Cast<PropertyData>().Any(p => p.Name == "ExtendedStatus") ? outParams["ExtendedStatus"] : null) switch
        {
            ManagementBaseObject ext => ext["Message"] as string,
            string s => s,
            _ => null,
        };
        throw new StorageException(code, string.IsNullOrWhiteSpace(detail) ? $"{method}: mã lỗi {code}" : detail.Trim());
    }

    /// <summary>Kích thước nhỏ nhất / lớn nhất có thể đổi (nhỏ nhất bị giới hạn bởi file không di chuyển được như pagefile).</summary>
    public static (ulong Min, ulong Max) SupportedSize(uint disk, uint number)
    {
        using var p = Partition(Scope(), disk, number);
        var o = Invoke(p, "GetSupportedSize");
        return (Convert.ToUInt64(o["SizeMin"]), Convert.ToUInt64(o["SizeMax"]));
    }

    public static void Resize(uint disk, uint number, ulong size)
    {
        using var p = Partition(Scope(), disk, number);
        Invoke(p, "Resize", i => i["Size"] = size);
    }

    public static void Delete(uint disk, uint number)
    {
        using var p = Partition(Scope(), disk, number);
        Invoke(p, "DeleteObject");
    }

    /// <summary>Tạo phân vùng tại vùng trống rồi format; trả số phân vùng mới.</summary>
    public static uint Create(uint diskNumber, ulong offset, ulong size, char? letter, string fileSystem, string label)
    {
        var scope = Scope();
        using var disk = Query(scope, $"SELECT * FROM MSFT_Disk WHERE Number={diskNumber}").FirstOrDefault()
                         ?? throw new StorageException(0, $"Không tìm thấy đĩa {diskNumber}");
        var o = Invoke(disk, "CreatePartition", i =>
        {
            i["Offset"] = offset;
            i["Size"] = size;
            if (letter is { } l) i["DriveLetter"] = l;
            else i["AssignDriveLetter"] = false;
        });
        var created = o["CreatedPartition"] as ManagementBaseObject;
        var number = created != null ? Convert.ToUInt32(created["PartitionNumber"]) : 0u;
        if (number == 0)
        {
            // Một số bản Windows không trả CreatedPartition: tìm phân vùng ở offset vừa tạo.
            number = Query(scope, $"SELECT * FROM MSFT_Partition WHERE DiskNumber={diskNumber}")
                .Where(p => Convert.ToUInt64(p["Offset"]) >= offset).OrderBy(p => Convert.ToUInt64(p["Offset"]))
                .Select(p => Convert.ToUInt32(p["PartitionNumber"])).FirstOrDefault();
        }
        if (number != 0) Format(diskNumber, number, fileSystem, label);
        return number;
    }

    /// <summary>Format nhanh (xoá sạch dữ liệu của phân vùng).</summary>
    public static void Format(uint disk, uint number, string fileSystem, string label)
    {
        using var p = Partition(Scope(), disk, number);
        using var v = Volume(p);
        Invoke(v, "Format", i =>
        {
            i["FileSystem"] = fileSystem;
            i["FileSystemLabel"] = label;
            i["Full"] = false;
            i["Force"] = true;
        });
    }

    /// <summary>Đổi / gỡ ký tự ổ (newLetter null = gỡ).</summary>
    public static void SetLetter(uint disk, uint number, char? oldLetter, char? newLetter)
    {
        using var p = Partition(Scope(), disk, number);
        if (oldLetter is { } o) Invoke(p, "RemoveAccessPath", i => i["AccessPath"] = $"{o}:\\");
        if (newLetter is { } n) Invoke(p, "AddAccessPath", i => i["AccessPath"] = $"{n}:\\");
    }

    public static void SetLabel(uint disk, uint number, string label)
    {
        using var p = Partition(Scope(), disk, number);
        using var v = Volume(p);
        Invoke(v, "SetFileSystemLabel", i => i["FileSystemLabel"] = label);
    }

    /// <summary>Khởi tạo đĩa mới (chưa có bảng phân vùng).</summary>
    public static void Initialize(uint diskNumber, PartitionStyle style)
    {
        using var disk = Query(Scope(), $"SELECT * FROM MSFT_Disk WHERE Number={diskNumber}").FirstOrDefault()
                         ?? throw new StorageException(0, $"Không tìm thấy đĩa {diskNumber}");
        Invoke(disk, "Initialize", i => i["PartitionStyle"] = (ushort)(style == PartitionStyle.Mbr ? 1 : 2));
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ManagerWindows.Core.Partitions;
using Microsoft.Win32.SafeHandles;

namespace ManagerWindows.Platform;

/// <summary>
/// Đọc/ghi trực tiếp ổ vật lý \\.\PhysicalDriveN (cần quyền Administrator). Mở không đệm + ghi xuyên (write-through):
/// dữ liệu đã ghi là đã xuống đĩa. Mọi I/O đi qua bộ đệm căn lề 4 KB của lớp này.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class PhysicalDisk : IBlockDevice, IDisposable
{
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0, IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x70050;
    private const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x7C054, IOCTL_DISK_UPDATE_PROPERTIES = 0x70140;
    private const int BufferSize = BlockMover.MaxChunk;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte* buf, int toRead, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte* buf, int toWrite, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);

    private readonly SafeFileHandle _h;
    private readonly byte* _buf;

    public uint Number { get; }
    public int SectorSize { get; }
    public long Size { get; }

    public PhysicalDisk(uint number)
    {
        Number = number;
        _h = CreateFile($@"\\.\PhysicalDrive{number}", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không mở được ổ đĩa {number}");
        var geo = new byte[256];
        if (!DeviceIoControl(_h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geo, geo.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không đọc được thông số ổ đĩa");
        // DISK_GEOMETRY_EX: DISK_GEOMETRY(24: Cylinders 8, MediaType 4, Tracks 4, Sectors 4, BytesPerSector 4) + DiskSize 8
        SectorSize = BitConverter.ToInt32(geo, 20);
        Size = BitConverter.ToInt64(geo, 24);
        if (SectorSize is not (512 or 1024 or 2048 or 4096)) throw new IOException($"Kích thước sector lạ: {SectorSize}");
        _buf = (byte*)NativeMemory.AlignedAlloc(BufferSize, 4096);
    }

    private void Seek(long offset)
    {
        if (!SetFilePointerEx(_h, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không đến được vị trí {offset}");
    }

    public void Read(long offset, Span<byte> buffer)
    {
        for (var pos = 0; pos < buffer.Length;)
        {
            var n = Math.Min(BufferSize, buffer.Length - pos);
            Seek(offset + pos);
            if (!ReadFile(_h, _buf, n, out var got, IntPtr.Zero) || got != n)
                throw new IOException($"Đọc ổ đĩa lỗi tại byte {offset + pos}", new Win32Exception(Marshal.GetLastWin32Error()));
            new Span<byte>(_buf, n).CopyTo(buffer[pos..]);
            pos += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        for (var pos = 0; pos < buffer.Length;)
        {
            var n = Math.Min(BufferSize, buffer.Length - pos);
            buffer.Slice(pos, n).CopyTo(new Span<byte>(_buf, n));
            Seek(offset + pos);
            if (!WriteFile(_h, _buf, n, out var put, IntPtr.Zero) || put != n)
                throw new IOException($"Ghi ổ đĩa lỗi tại byte {offset + pos}", new Win32Exception(Marshal.GetLastWin32Error()));
            pos += n;
        }
    }

    public void Flush() => FlushFileBuffers(_h);

    /// <summary>Bảng phân vùng thô (DRIVE_LAYOUT_INFORMATION_EX).</summary>
    public byte[] GetLayout()
    {
        for (var size = 4096; size <= 1 << 20; size *= 2)
        {
            var buf = new byte[size];
            if (DeviceIoControl(_h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, buf, size, out var got, IntPtr.Zero)) return buf[..got];
            if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) break;
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Không đọc được bảng phân vùng");
    }

    public void SetLayout(byte[] layout)
    {
        if (!DeviceIoControl(_h, IOCTL_DISK_SET_DRIVE_LAYOUT_EX, layout, layout.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không ghi được bảng phân vùng");
        DeviceIoControl(_h, IOCTL_DISK_UPDATE_PROPERTIES, null, 0, null, 0, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_buf);
        _h.Dispose();
    }
}

/// <summary>
/// Khoá + tháo (dismount) một volume trong lúc dời: Windows chỉ cho ghi sector của volume khi volume đã bị khoá,
/// và không chương trình nào khác đọc/ghi được nó. Khoá thất bại nghĩa là đang có app mở file trên ổ.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeLock : IDisposable
{
    private const uint FSCTL_LOCK_VOLUME = 0x90018, FSCTL_UNLOCK_VOLUME = 0x9001C, FSCTL_DISMOUNT_VOLUME = 0x90020;
    private readonly SafeFileHandle _h;

    public VolumeLock(string volumePath)
    {
        // "\\?\Volume{guid}\" → mở volume phải bỏ dấu "\" cuối.
        var path = volumePath.TrimEnd('\\');
        _h = PhysicalDisk.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không mở được volume");
        var locked = false;
        for (var i = 0; i < 10 && !locked; i++)
        {
            locked = PhysicalDisk.DeviceIoControl(_h, FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
            if (!locked) Thread.Sleep(500);
        }
        if (!locked)
        {
            _h.Dispose();
            throw new IOException("Ổ đang được chương trình khác sử dụng (cửa sổ Explorer, file đang mở, pagefile…). Hãy đóng chúng rồi thử lại.");
        }
        PhysicalDisk.DeviceIoControl(_h, FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        PhysicalDisk.DeviceIoControl(_h, FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
        _h.Dispose();
    }
}

/// <summary>Giữ máy không ngủ / không tắt màn hình làm gián đoạn trong lúc dời phân vùng.</summary>
[SupportedOSPlatform("windows")]
public sealed class KeepAwake : IDisposable
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1;

    public KeepAwake() => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
    public void Dispose() => SetThreadExecutionState(ES_CONTINUOUS);
}

public sealed record PlanProgress(int Step, int Steps, StepKind Kind, long Done, long Total);

/// <summary>
/// Thực hiện kế hoạch phân vùng (thu nhỏ / dời / mở rộng / chép file / xoá). Bước dời chép sector có nhật ký:
/// lỗi hay mất điện giữa chừng thì <see cref="Resume"/> tiếp tục đúng chỗ.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PartitionMover
{
    /// <summary>Nhật ký dời phân vùng: luôn trên ổ Windows (ổ C:), không bao giờ nằm trên phân vùng đang dời.</summary>
    public static string JournalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ManagerWindows", "partition-move.json");

    public static MoveJournal? PendingMove() => MoveJournal.Load(JournalPath);

    public static void Run(IReadOnlyList<PlanStep> steps, IProgress<PlanProgress> progress, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = steps[i];
            var step = i + 1;
            void Report(long done, long total) => progress.Report(new PlanProgress(step, steps.Count, s.Kind, done, total));
            Report(0, 1);
            switch (s.Kind)
            {
                case StepKind.Shrink:
                    StorageApi.Resize(s.Disk, s.Partition, s.Size);
                    break;
                case StepKind.Extend:
                    // Kẹp theo mức Windows cho phép (căn lề, 1 MB cuối đĩa GPT…); Size = 0 nghĩa là lấy tối đa.
                    var max = StorageApi.SupportedSize(s.Disk, s.Partition).Max;
                    var size = s.Size == 0 ? max : Math.Min(s.Size, max);
                    StorageApi.Resize(s.Disk, s.Partition, size);
                    break;
                case StepKind.Delete:
                    StorageApi.Delete(s.Disk, s.Partition);
                    break;
                case StepKind.CopyFiles:
                    CopyFiles(s.Source!, s.Target!, ct);
                    break;
                case StepKind.Move:
                    Move(s.Disk, s.Partition, (long)s.Offset, Report, ct);
                    break;
            }
            Report(1, 1);
        }
    }

    /// <summary>Tiếp tục bước dời đang dở (sau lỗi / mất điện / bấm Dừng).</summary>
    public static void Resume(IProgress<PlanProgress> progress, CancellationToken ct)
    {
        var j = PendingMove() ?? throw new InvalidOperationException("Không có thao tác dời phân vùng nào đang dở");
        Move(j.Disk, j.Partition, j.NewOffset, (d, t) => progress.Report(new PlanProgress(1, 1, StepKind.Move, d, t)), ct);
    }

    /// <summary>Chép file của phân vùng nguồn sang thư mục trên phân vùng đích (gộp phân vùng).</summary>
    private static void CopyFiles(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var psi = new ProcessStartInfo("robocopy.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { source, target, "/E", "/COPY:DAT", "/DCOPY:DAT", "/XJ", "/R:1", "/W:1", "/MT:8", "/NFL", "/NDL", "/NP", "/NJH",
                     "/XD", "System Volume Information", "$RECYCLE.BIN", "/XF", "pagefile.sys", "swapfile.sys", "hiberfil.sys" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("Không chạy được robocopy");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode >= 8) throw new IOException($"Chép file lỗi (robocopy mã {p.ExitCode}): {output.Result.Trim()}");
    }

    private static PartitionInfo Find(uint disk, uint number) =>
        StorageApi.Load().FirstOrDefault(d => d.Number == disk)?.Partitions.FirstOrDefault(p => p.Number == number)
        ?? throw new IOException($"Không tìm thấy phân vùng {number} trên đĩa {disk}");

    private static void Move(uint diskNo, uint number, long newOffset, Action<long, long> report, CancellationToken ct)
    {
        var journal = PendingMove();
        if (journal != null && (journal.Disk != diskNo || journal.Partition != number))
            throw new InvalidOperationException($"Đang có thao tác dời phân vùng {journal.Partition} (đĩa {journal.Disk}) chưa xong — hãy tiếp tục nó trước");
        var p = Find(diskNo, number);
        if (SystemInfo.OnBattery()) throw new IOException("Máy đang chạy pin: hãy cắm sạc trước khi dời phân vùng");

        using var awake = new KeepAwake();
        using var disk = new PhysicalDisk(diskNo);
        var ss = disk.SectorSize;
        if (newOffset % ss != 0 || (long)p.Offset % ss != 0) throw new IOException("Vị trí không thẳng hàng sector");

        if (journal == null)
        {
            if (p.VolumePath == null) throw new IOException("Không tìm thấy volume của phân vùng");
            // Chỉ NTFS thật: BitLocker mã hoá theo số sector nên chép sang chỗ khác sẽ hỏng dữ liệu.
            var boot = new byte[ss];
            disk.Read((long)p.Offset, boot);
            if (LayoutPatch.IsBitLocker(boot)) throw new IOException("Phân vùng đang mã hoá BitLocker: hãy tắt BitLocker trước khi dời");
            if (!LayoutPatch.IsNtfsBootSector(boot)) throw new IOException("Chỉ dời được phân vùng NTFS");
            if (newOffset == (long)p.Offset) return;
            var chunk = BlockMover.ChunkFor((long)p.Offset, newOffset, ss);
            journal = new MoveJournal(diskNo, number, (long)p.Offset, newOffset, (long)p.Size, 0, chunk, "copy", p.Letter, p.VolumePath, DateTime.UtcNow);
            if (p.Kind == PartitionKind.Recovery) Reagent("/disable");
            journal.Save(JournalPath);
        }
        else if ((long)p.Offset == journal.NewOffset)
        {
            journal = journal with { Stage = "done" }; // bảng phân vùng đã sửa xong ở lần trước
        }

        if (journal.Stage == "copy")
        {
            using (new VolumeLock(journal.VolumePath ?? p.VolumePath ?? throw new IOException("Không tìm thấy volume")))
            {
                var saved = journal.Done;
                var j = journal;
                BlockMover.Copy(disk, j.OldOffset, j.NewOffset, j.Length, j.Chunk, j.Done, done =>
                {
                    report(done, j.Length);
                    // Ghi nhật ký mỗi 64 MB (và khi xong): chép lại vài đoạn sau sự cố là an toàn.
                    if (done - saved >= 64L << 20 || done == j.Length)
                    {
                        (j with { Done = done }).Save(JournalPath);
                        saved = done;
                    }
                }, ct);
                PatchBootSectors(disk, j.NewOffset, j.Length);
                journal = j with { Done = j.Length, Stage = "table" };
                journal.Save(JournalPath);

                var layout = disk.GetLayout();
                if (!LayoutPatch.SetStartingOffset(layout, number, journal.NewOffset, ss))
                    throw new IOException("Không sửa được bảng phân vùng (không tìm thấy phân vùng)");
                disk.SetLayout(layout);
            }
        }
        else if (journal.Stage == "table")
        {
            // Dữ liệu đã chép xong; volume ở vị trí cũ có thể đã bị Windows mount lại sau khi khởi động: khoá nếu được.
            using var lockOld = TryLock(journal.VolumePath ?? p.VolumePath);
            var layout = disk.GetLayout();
            if (!LayoutPatch.SetStartingOffset(layout, number, journal.NewOffset, ss))
                throw new IOException("Không sửa được bảng phân vùng");
            disk.SetLayout(layout);
        }

        File.Delete(JournalPath);
        Thread.Sleep(2000); // chờ Windows nhận lại volume ở vị trí mới
        var after = Find(diskNo, number);
        if (after.Letter == null && journal.Letter is { } l)
            try { StorageApi.SetLetter(diskNo, number, null, l); } catch (StorageException) { }
        if (after.Kind == PartitionKind.Recovery) Reagent("/enable");
    }

    private static VolumeLock? TryLock(string? path)
    {
        if (path == null) return null;
        try { return new VolumeLock(path); }
        catch (Exception ex) when (ex is IOException or Win32Exception) { return null; }
    }

    /// <summary>Sửa "hidden sectors" trong boot sector NTFS và bản sao ở cuối volume theo vị trí mới.</summary>
    private static void PatchBootSectors(PhysicalDisk disk, long offset, long length)
    {
        var ss = disk.SectorSize;
        var sector = new byte[ss];
        disk.Read(offset, sector);
        if (!LayoutPatch.PatchNtfsHiddenSectors(sector, offset, ss)) return;
        disk.Write(offset, sector);
        var backup = offset + LayoutPatch.NtfsTotalSectors(sector) * ss;
        if (backup > offset && backup + ss <= offset + length)
        {
            disk.Read(backup, sector);
            if (LayoutPatch.PatchNtfsHiddenSectors(sector, offset, ss)) disk.Write(backup, sector);
        }
        disk.Flush();
    }

    /// <summary>reagentc /disable trước khi dời phân vùng Recovery, /enable sau đó (WinRE đăng ký theo vị trí phân vùng).</summary>
    private static void Reagent(string arg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("reagentc.exe", arg) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(120_000);
        }
        catch (Win32Exception) { }
    }
}

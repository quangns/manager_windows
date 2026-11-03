using System.Runtime.Versioning;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Cleaning;
using static ManagerWindows.Platform.NativeMethods;

namespace ManagerWindows.Platform;

[SupportedOSPlatform("windows")]
public sealed class RecycleBinDeleter : IFileDeleter
{
    public void Delete(string path) => File.Delete(path);

    public void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
            throw new IOException($"Không chuyển được vào Thùng rác (mã {rc}): {path}");
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public long QuerySize()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    public void Empty()
    {
        var rc = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // S_OK = 0; Thùng rác đã rỗng có thể trả về E_UNEXPECTED (0x8000FFFF).
        if (rc != 0 && rc != unchecked((int)0x8000FFFF))
            throw new IOException($"Không dọn được Thùng rác (mã 0x{rc:X8})");
    }
}

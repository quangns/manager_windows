using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ManagerWindows.Platform;

internal static partial class NativeMethods
{
    // ---- user32: cửa sổ đang dùng, thời gian rảnh ----
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ---- kernel32: process ----
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    internal const uint IDLE_PRIORITY_CLASS = 0x40;
    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    internal const uint NORMAL_PRIORITY_CLASS = 0x20;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    internal const uint HIGH_PRIORITY_CLASS = 0x80;

    internal enum PROCESS_INFORMATION_CLASS
    {
        ProcessMemoryPriority = 0,
        ProcessPowerThrottling = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    internal const uint MEMORY_PRIORITY_LOW = 2;
    internal const uint MEMORY_PRIORITY_NORMAL = 5;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(SafeProcessHandle process, PROCESS_INFORMATION_CLASS cls, IntPtr info, uint size);

    // ---- nguồn điện, bộ nhớ ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // ---- junction ----
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

    // ---- shell32: Thùng rác ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    internal const uint FO_DELETE = 3;
    internal const ushort FOF_SILENT = 0x4;
    internal const ushort FOF_NOCONFIRMATION = 0x10;
    internal const ushort FOF_ALLOWUNDO = 0x40;
    internal const ushort FOF_NOERRORUI = 0x400;
    /// <summary>Hỏi lại nếu mục quá lớn cho Thùng rác và sẽ bị xoá vĩnh viễn (thay vì xoá âm thầm).</summary>
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential)] // x64: shellapi.h dùng pack 8
    internal struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    internal const uint SHERB_NOCONFIRMATION = 0x1;
    internal const uint SHERB_NOPROGRESSUI = 0x2;
    internal const uint SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}

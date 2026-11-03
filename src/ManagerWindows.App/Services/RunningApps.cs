using System.Diagnostics;
using ManagerWindows.Core.Priority;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>Một app đang chạy, gộp mọi tiến trình cùng tên (Chrome có thể vài chục tiến trình con).</summary>
public sealed record RunningApp(string Name, string DisplayName, int Processes, long WorkingSetBytes, bool HasWindow);

/// <summary>
/// Danh sách app để người dùng chọn ưu tiên: chỉ app của phiên đăng nhập hiện tại, có cửa sổ hoặc dùng nhiều RAM.
/// Bỏ tiến trình hệ thống, dịch vụ trong thư mục Windows và tiến trình được bảo vệ.
/// </summary>
public static class RunningApps
{
    private const long BigMemory = 150L << 20;

    public static List<RunningApp> Snapshot()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var groups = new Dictionary<string, (string? Path, int Count, long Ws, bool Window)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4 || p.SessionId != session || ProtectedProcesses.IsProtected(p.ProcessName)) continue;
                    var hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    var ws = p.WorkingSet64;
                    groups.TryGetValue(p.ProcessName, out var g);
                    g.Path ??= SystemInfo.ProcessPath(p.Id);
                    g.Count++;
                    g.Ws += ws;
                    g.Window |= hasWindow;
                    groups[p.ProcessName] = g;
                }
                catch (InvalidOperationException) { } // tiến trình vừa thoát
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        var result = new List<RunningApp>();
        foreach (var (name, g) in groups)
        {
            var inWindowsDir = g.Path != null && g.Path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
            // Giữ app có cửa sổ, hoặc app nền ngoài thư mục Windows mà dùng nhiều RAM (OneDrive, Teams…).
            if (!g.Window && (inWindowsDir || g.Ws < BigMemory)) continue;
            result.Add(new RunningApp(name, Describe(name, g.Path), g.Count, g.Ws, g.Window));
        }
        return result.OrderByDescending(a => a.HasWindow).ThenByDescending(a => a.WorkingSetBytes).ToList();
    }

    /// <summary>Tên dễ đọc từ thông tin phiên bản của exe ("Google Chrome", "Microsoft Excel").</summary>
    private static string Describe(string name, string? path)
    {
        if (path == null) return name;
        try
        {
            var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(d) ? name : d;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return name;
        }
    }
}

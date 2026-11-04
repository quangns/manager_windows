using System.Diagnostics;
using System.Management;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Ipc;

namespace ManagerWindows.App.Services;

public sealed record RestartResult(int Closed, int Killed, int Started, string? Error);

/// <summary>
/// Khởi động lại một app như Task Manager: đóng nhẹ (như bấm X) để app tự lưu, quá thời gian mới buộc dừng,
/// rồi mở lại bằng đúng lệnh khởi động của tiến trình gốc.
/// </summary>
public static class AppRestarter
{
    /// <summary>Lệnh khởi động của các tiến trình trong app, đọc qua WMI (đọc được tiến trình cùng user).</summary>
    public static List<ProcessLaunchInfo> LaunchInfo(IReadOnlyCollection<int> pids)
    {
        var result = new List<ProcessLaunchInfo>();
        if (pids.Count == 0) return result;
        var where = string.Join(" OR ", pids.Select(p => $"ProcessId = {p}"));
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE {where}");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                    result.Add(new ProcessLaunchInfo(
                        Convert.ToInt32(o["ProcessId"]), Convert.ToInt32(o["ParentProcessId"]),
                        o["CommandLine"] as string, o["ExecutablePath"] as string));
        }
        catch (ManagementException ex) { Log.Error("restart", ex); }
        return result;
    }

    public static async Task<RestartResult> RestartAsync(string name, IReadOnlyList<int> pids, IpcClient service)
    {
        var plan = RestartPlanner.Plan(LaunchInfo(pids));
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) && plan.Count == 0)
            plan = [new LaunchCommand("explorer.exe", "")];
        if (plan.Count == 0) return new RestartResult(0, 0, 0, Loc.Instance["Restart_NoCommand"]);

        // 1. Đóng nhẹ: gửi lệnh đóng cửa sổ chính, chờ tối đa 10 giây.
        int closed = 0, killed = 0;
        var procs = pids.Select(TryGet).Where(p => p != null).Cast<Process>().ToList();
        try
        {
            foreach (var p in procs)
                try { if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) closed++; } catch (InvalidOperationException) { }
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && procs.Any(Alive)) await Task.Delay(300);

            // 2. Còn sót (tiến trình nền, app hỏi "Lưu không?" bị bỏ qua…) thì buộc dừng.
            foreach (var p in procs.Where(Alive))
            {
                var ok = false;
                try { p.Kill(); ok = true; } catch (Exception) { }
                if (!ok) ok = (await service.SendAsync(IpcCommands.KillProcess, new KillProcessRequest(p.Id))).Ok;
                if (ok) killed++;
            }
            var wait = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < wait && procs.Any(Alive)) await Task.Delay(200);
            if (procs.Any(Alive)) return new RestartResult(closed, killed, 0, Loc.Instance["Restart_StillRunning"]);
        }
        finally { foreach (var p in procs) p.Dispose(); }

        // 3. Mở lại bằng lệnh cũ. Explorer bị dừng thì Windows thường tự mở lại thanh taskbar:
        //    chờ một chút, đã có thì thôi (mở thêm sẽ bật thừa một cửa sổ File Explorer).
        await Task.Delay(500);
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(2500);
            if (Process.GetProcessesByName("explorer") is { Length: > 0 } running)
            {
                foreach (var p in running) p.Dispose();
                Log.Info("restart", "explorer: Windows đã tự mở lại");
                return new RestartResult(closed, killed, 1, null);
            }
        }
        var started = 0;
        foreach (var cmd in plan)
        {
            try
            {
                var dir = Path.IsPathRooted(cmd.FileName) ? Path.GetDirectoryName(cmd.FileName) : null;
                Process.Start(new ProcessStartInfo(cmd.FileName, cmd.Arguments)
                {
                    UseShellExecute = true, // mở như người dùng nhấp đúp (đường dẫn tương đối, file liên kết…)
                    WorkingDirectory = dir ?? "",
                })?.Dispose();
                started++;
            }
            catch (Exception ex) { Log.Error("restart", ex); }
        }
        Log.Info("restart", $"{name}: closed {closed}, killed {killed}, started {started}/{plan.Count}");
        return new RestartResult(closed, killed, started, started == 0 ? Loc.Instance["Restart_StartFailed"] : null);
    }

    private static Process? TryGet(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static bool Alive(Process p)
    {
        try { return !p.HasExited; }
        catch (Exception) { return false; }
    }
}

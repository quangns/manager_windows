namespace ManagerWindows.Core.Monitoring;

/// <summary>Thông tin một tiến trình để biết cách mở lại app (lấy từ WMI Win32_Process).</summary>
public sealed record ProcessLaunchInfo(int Pid, int ParentPid, string? CommandLine, string? ExecutablePath);

/// <summary>Lệnh mở lại app: file exe và tham số.</summary>
public sealed record LaunchCommand(string FileName, string Arguments);

/// <summary>
/// Từ các tiến trình của một app, tìm tiến trình gốc (cha không thuộc app, vd. tiến trình chính của Chrome chứ không
/// phải các tiến trình con renderer) và lệnh đã dùng để mở nó, để khởi động lại đúng như cũ.
/// </summary>
public static class RestartPlanner
{
    public static List<LaunchCommand> Plan(IReadOnlyList<ProcessLaunchInfo> processes)
    {
        var pids = processes.Select(p => p.Pid).ToHashSet();
        var roots = processes.Where(p => !pids.Contains(p.ParentPid)).ToList();
        if (roots.Count == 0) roots = processes.Take(1).ToList();

        var result = new List<LaunchCommand>();
        foreach (var r in roots)
        {
            var cmd = !string.IsNullOrWhiteSpace(r.CommandLine)
                ? Split(r.CommandLine!)
                : r.ExecutablePath != null ? new LaunchCommand(r.ExecutablePath, "") : null;
            if (cmd == null) continue;
            // Nhiều tiến trình gốc mở cùng một lệnh (vd. hai cửa sổ Notepad) thì mở lại đủ số lượng;
            // cùng exe nhưng khác tham số cũng giữ nguyên.
            result.Add(cmd);
        }
        return result;
    }

    /// <summary>Tách dòng lệnh Windows thành exe + phần tham số còn lại (giữ nguyên dấu ngoặc của tham số).</summary>
    public static LaunchCommand Split(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return new LaunchCommand(s[1..end], s[(end + 1)..].Trim());
            return new LaunchCommand(s.Trim('"'), "");
        }
        // Không có ngoặc: exe kết thúc ở ".exe" đầu tiên (đường dẫn có thể chứa dấu cách), không thì ở dấu cách đầu tiên.
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && (exe + 4 == s.Length || s[exe + 4] == ' '))
            return new LaunchCommand(s[..(exe + 4)], s[(exe + 4)..].Trim());
        var space = s.IndexOf(' ');
        return space < 0 ? new LaunchCommand(s, "") : new LaunchCommand(s[..space], s[(space + 1)..].Trim());
    }
}

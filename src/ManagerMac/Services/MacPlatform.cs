using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagerWindows.Core.Abstractions;
using ManagerWindows.Core.Monitoring;

namespace ManagerMac.Services;

/// <summary>Gọi hệ điều hành macOS: tiến trình (ps), bộ nhớ (vm_stat, sysctl), tín hiệu, Finder, Thùng rác, tự chạy.</summary>
public static class MacPlatform
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc")] private static extern uint getuid();

    public const int SIGTERM = 15, SIGKILL = 9;

    private static readonly Lazy<int> UidLazy = new(() => (int)getuid());
    public static int Uid => UidLazy.Value;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Chạy lệnh, trả về stdout (rỗng nếu lỗi/quá thời gian).</summary>
    public static string Run(string file, IEnumerable<string> args, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return output.Result;
        }
        catch (Exception ex)
        {
            Log.Error("run " + file, ex);
            return "";
        }
    }

    public static List<MacProc> Processes() =>
        MacProcesses.ParsePs(Run("/bin/ps", MacProcesses.PsArguments.Split(' ')));

    public static List<AppGroup> AppGroups() => MacProcesses.Group(Processes(), Uid, Environment.ProcessId);

    public static long TotalMemory() =>
        long.TryParse(Run("/usr/sbin/sysctl", ["-n", "hw.memsize"]).Trim(), out var b) ? b : 0;

    public static long UsedMemory() => MacProcesses.UsedMemoryFromVmStat(Run("/usr/bin/vm_stat", []));

    public static bool Signal(int pid, int sig) => kill(pid, sig) == 0;

    public static bool Alive(int pid) => kill(pid, 0) == 0;

    /// <summary>Thoát app như Cmd+Q (app có thể hỏi lưu). macOS sẽ hỏi quyền "điều khiển" app đó ở lần đầu.</summary>
    public static bool QuitApp(string appName)
    {
        var escaped = appName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        Run("/usr/bin/osascript", ["-e", $"tell application \"{escaped}\" to quit"], 15000);
        return true;
    }

    public static void Open(string path) => Run("/usr/bin/open", [path]);

    public static void Reveal(string path) => Run("/usr/bin/open", ["-R", path]);

    /// <summary>Xả bộ nhớ đệm (lệnh purge) — macOS hỏi mật khẩu quản trị.</summary>
    public static bool Purge() =>
        Run("/usr/bin/osascript", ["-e", "do shell script \"/usr/sbin/purge\" with administrator privileges"], 120000) is not null;
}

/// <summary>"Chuyển vào Thùng rác" trên macOS: chuyển file vào ~/.Trash (đổi tên nếu trùng). Xoá hẳn thì File.Delete.</summary>
public sealed class MacTrashDeleter : IFileDeleter
{
    public void Delete(string path) => File.Delete(path);

    public void Recycle(string path)
    {
        var trash = Path.Combine(MacPlatform.Home, ".Trash");
        Directory.CreateDirectory(trash);
        var name = Path.GetFileName(path);
        var dest = Path.Combine(trash, name);
        if (File.Exists(dest) || Directory.Exists(dest))
            dest = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {DateTime.Now:HH.mm.ss}{Path.GetExtension(name)}");
        File.Move(path, dest);
    }
}

/// <summary>Tự chạy khi đăng nhập bằng LaunchAgent của người dùng (~/Library/LaunchAgents).</summary>
public static class LaunchAgent
{
    private const string Label = "vn.quangns.managermac";
    private static string PlistPath => Path.Combine(MacPlatform.Home, "Library", "LaunchAgents", Label + ".plist");

    public static bool IsEnabled => File.Exists(PlistPath);

    public static void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(PlistPath)) File.Delete(PlistPath);
                return;
            }
            var exe = System.Security.SecurityElement.Escape(Environment.ProcessPath ?? "");
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{Label}</string>
                  <key>ProgramArguments</key><array><string>{exe}</string><string>--tray</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>ProcessType</key><string>Interactive</string>
                </dict>
                </plist>
                """);
        }
        catch (Exception ex) { Log.Error("launch-agent", ex); }
    }
}

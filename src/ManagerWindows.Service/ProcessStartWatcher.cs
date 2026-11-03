using System.Collections.Concurrent;
using System.Management;
using ManagerWindows.Storage;

namespace ManagerWindows.Service;

/// <summary>
/// Đếm số lần mở ứng dụng qua sự kiện WMI Win32_ProcessStartTrace (cần quyền admin nên đặt trong service).
/// Ghi gộp xuống DB mỗi 30 giây.
/// </summary>
public sealed class ProcessStartWatcher(UsageRepository usage, ILogger<ProcessStartWatcher> log) : BackgroundService
{
    /// <summary>Process hệ thống sinh liên tục, không có ý nghĩa thống kê.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "svchost", "backgroundTaskHost", "RuntimeBroker", "dllhost", "WmiPrvSE", "SearchProtocolHost",
        "SearchFilterHost", "taskhostw", "smartscreen", "consent", "WerFault", "audiodg", "sppsvc", "TrustedInstaller",
        "TiWorker", "MpCmdRun", "CompatTelRunner", "msedgewebview2", "crashpad_handler", "ManagerWindows.App",
    };

    private readonly ConcurrentQueue<(string Name, DateTime Utc)> _queue = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        ManagementEventWatcher? watcher = null;
        try
        {
            watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT ProcessName FROM Win32_ProcessStartTrace"));
            watcher.EventArrived += (_, e) =>
            {
                var name = e.NewEvent.Properties["ProcessName"]?.Value as string;
                if (string.IsNullOrEmpty(name)) return;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                if (!Noise.Contains(name)) _queue.Enqueue((name, DateTime.UtcNow));
            };
            watcher.Start();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Không bật được theo dõi khởi động process (WMI)");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Flush();
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { watcher?.Stop(); } catch { }
            watcher?.Dispose();
            Flush();
        }
    }

    private void Flush()
    {
        try
        {
            while (_queue.TryDequeue(out var item)) usage.AddProcessStart(item.Name, item.Utc);
        }
        catch (Exception ex) { log.LogError(ex, "Không ghi được số lần mở app"); }
    }
}

using ManagerWindows.Core.Usage;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

/// <summary>Lấy mẫu cửa sổ đang dùng mỗi vài giây, gom thành phiên và ghi xuống DB mỗi phút.</summary>
public sealed class UsageTracker(AppHost host) : BackgroundLoop
{
    private UsageAggregator? _agg;
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime _lastPurge = DateTime.MinValue;

    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, host.Config.Usage.SampleSeconds));

    protected override void Tick(CancellationToken ct)
    {
        var cfg = host.Config.Usage;
        _agg ??= new UsageAggregator(Interval);
        var now = DateTime.UtcNow;
        if (!cfg.Enabled)
        {
            _agg.Close();
        }
        else
        {
            var (_, exe) = SystemInfo.ForegroundProcess();
            var idle = SystemInfo.IdleSeconds() >= cfg.IdleSeconds;
            _agg.Sample(now, exe, idle);
        }

        if ((now - _lastFlush).TotalSeconds >= 60) Flush();
        if ((now - _lastPurge).TotalHours >= 24)
        {
            host.Usage.Purge(now.AddDays(-Math.Max(7, cfg.RetentionDays)));
            _lastPurge = now;
        }
    }

    /// <summary>Ghi các phiên đã kết thúc (gọi cả khi UI cần số liệu mới nhất).</summary>
    public void Flush()
    {
        if (_agg == null) return;
        var sessions = _agg.Drain();
        if (sessions.Count > 0) host.Usage.AddSessions(sessions);
        _lastFlush = DateTime.UtcNow;
    }

    protected override void OnStop()
    {
        _agg?.Close();
        Flush();
    }
}

using Dapper;
using ManagerWindows.Core.Relocation;
using ManagerWindows.Core.Usage;
using static ManagerWindows.Storage.Database;

namespace ManagerWindows.Storage;

public sealed record CleanupRunRow(long Id, DateTime StartedUtc, DateTime FinishedUtc, string Trigger, long FreedBytes, int Deleted, int Failed);

public sealed class CleanupRunRepository(Database db)
{
    public void Add(DateTime startedUtc, DateTime finishedUtc, string trigger, long freed, int deleted, int failed)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO cleanup_runs(started_utc, finished_utc, trigger, freed_bytes, deleted, failed) VALUES (@s, @f, @t, @b, @d, @x)",
            new { s = ToUnix(startedUtc), f = ToUnix(finishedUtc), t = trigger, b = freed, d = deleted, x = failed });
    }

    public List<CleanupRunRow> Recent(int limit)
    {
        using var c = db.Open();
        return c.Query<(long id, long s, long f, string t, long b, long d, long x)>(
                "SELECT id, started_utc, finished_utc, trigger, freed_bytes, deleted, failed FROM cleanup_runs ORDER BY id DESC LIMIT @limit",
                new { limit })
            .Select(r => new CleanupRunRow(r.id, FromUnix(r.s), FromUnix(r.f), r.t, r.b, (int)r.d, (int)r.x))
            .ToList();
    }

    public long TotalFreed()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(SUM(freed_bytes), 0) FROM cleanup_runs");
    }
}

public sealed class RelocationRepository(Database db) : IRelocationStore
{
    public void Save(RelocationRecord r)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO relocations(id, app_id, source, target, backup, state, failed_at, bytes, created_utc, updated_utc, error)
            VALUES (@Id, @AppId, @Source, @Target, @Backup, @State, @FailedAt, @Bytes, @Created, @Updated, @Error)
            ON CONFLICT(id) DO UPDATE SET state = excluded.state, failed_at = excluded.failed_at, bytes = excluded.bytes,
                updated_utc = excluded.updated_utc, error = excluded.error
            """,
            new
            {
                r.Id, r.AppId, r.Source, r.Target, r.Backup,
                State = r.State.ToString(), FailedAt = r.FailedAt?.ToString(), r.Bytes,
                Created = ToUnix(r.CreatedUtc), Updated = ToUnix(r.UpdatedUtc), r.Error,
            });
    }

    public List<RelocationRecord> All()
    {
        using var c = db.Open();
        return c.Query<(string id, string? app, string src, string dst, string bak, string state, string? failedAt, long bytes, long created, long updated, string? error)>(
                "SELECT id, app_id, source, target, backup, state, failed_at, bytes, created_utc, updated_utc, error FROM relocations ORDER BY created_utc DESC")
            .Select(x => new RelocationRecord
            {
                Id = x.id, AppId = x.app, Source = x.src, Target = x.dst, Backup = x.bak,
                State = Enum.Parse<RelocationState>(x.state),
                FailedAt = x.failedAt == null ? null : Enum.Parse<RelocationState>(x.failedAt),
                Bytes = x.bytes, CreatedUtc = FromUnix(x.created), UpdatedUtc = FromUnix(x.updated), Error = x.error,
            })
            .ToList();
    }

    /// <summary>Các lần chuyển còn hiệu lực (junction đang dùng hoặc đang dở dang).</summary>
    public List<RelocationRecord> Active() =>
        All().Where(r => r.State is not RelocationState.RolledBack).ToList();
}

public sealed class UsageRepository(Database db)
{
    public void AddSessions(IEnumerable<UsageSession> sessions)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var s in sessions)
            c.Execute("INSERT INTO usage_sessions(exe, start_utc, end_utc) VALUES (@e, @s, @f)",
                new { e = s.Exe, s = ToUnix(s.StartUtc), f = ToUnix(s.EndUtc) }, tx);
        tx.Commit();
    }

    public void AddProcessStart(string name, DateTime utc)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO process_starts(name, start_utc) VALUES (@n, @s)", new { n = name, s = ToUnix(utc) });
    }

    /// <summary>Số lần mở theo tên process (không có .exe) trong khoảng thời gian.</summary>
    public Dictionary<string, int> LaunchCounts(DateTime fromUtc, DateTime toUtc)
    {
        using var c = db.Open();
        return c.Query<(string name, long n)>(
                "SELECT name, COUNT(*) FROM process_starts WHERE start_utc >= @a AND start_utc < @b GROUP BY name",
                new { a = ToUnix(fromUtc), b = ToUnix(toUtc) })
            .ToDictionary(x => x.name, x => (int)x.n, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Top app theo thời gian dùng thực tế, kèm số lần mở (nếu có dữ liệu từ service).</summary>
    public List<AppUsageSummary> TopApps(DateTime fromUtc, DateTime toUtc, int limit, IReadOnlyDictionary<string, int>? launches = null)
    {
        using var c = db.Open();
        var rows = c.Query<(string exe, long secs, long n)>("""
            SELECT exe, SUM(MIN(end_utc, @b) - MAX(start_utc, @a)), COUNT(*)
            FROM usage_sessions WHERE end_utc > @a AND start_utc < @b
            GROUP BY exe ORDER BY 2 DESC LIMIT @limit
            """, new { a = ToUnix(fromUtc), b = ToUnix(toUtc), limit });
        launches ??= LaunchCounts(fromUtc, toUtc);
        return rows.Select(r => new AppUsageSummary(r.exe, r.secs, 
                launches.TryGetValue(ExeName.Of(r.exe), out var l) ? l : 0, (int)r.n))
            .ToList();
    }

    /// <summary>Số giây dùng theo từng giờ trong ngày (0–23, giờ máy) để vẽ biểu đồ.</summary>
    public double[] HourlyActiveSeconds(DateTime fromUtc, DateTime toUtc)
    {
        var hours = new double[24];
        using var c = db.Open();
        foreach (var (s, e) in c.Query<(long s, long e)>(
                     "SELECT start_utc, end_utc FROM usage_sessions WHERE end_utc > @a AND start_utc < @b",
                     new { a = ToUnix(fromUtc), b = ToUnix(toUtc) }))
        {
            var start = FromUnix(Math.Max(s, ToUnix(fromUtc))).ToLocalTime();
            var end = FromUnix(Math.Min(e, ToUnix(toUtc))).ToLocalTime();
            while (start < end)
            {
                var nextHour = start.Date.AddHours(start.Hour + 1);
                var chunkEnd = nextHour < end ? nextHour : end;
                hours[start.Hour] += (chunkEnd - start).TotalSeconds;
                start = chunkEnd;
            }
        }
        return hours;
    }

    public void Purge(DateTime olderThanUtc)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM usage_sessions WHERE end_utc < @t; DELETE FROM process_starts WHERE start_utc < @t;",
            new { t = ToUnix(olderThanUtc) });
    }
}

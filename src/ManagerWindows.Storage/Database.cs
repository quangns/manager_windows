using Dapper;
using Microsoft.Data.Sqlite;

namespace ManagerWindows.Storage;

/// <summary>
/// SQLite cục bộ. App dùng %LOCALAPPDATA%\ManagerWindows\data.db (riêng từng user),
/// service dùng %ProgramData%\ManagerWindows\service.db. Thời gian lưu dạng Unix giây (UTC).
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA busy_timeout = 5000;");
        return c;
    }

    /// <summary>
    /// Chép toàn bộ cơ sở dữ liệu sang file khác bằng SQLite online backup: bản chép nhất quán kể cả khi app
    /// đang ghi (khác với chép file thô có thể bắt được trạng thái ghi dở). File đích bị ghi đè.
    /// </summary>
    public void BackupTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE cleanup_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_utc INTEGER NOT NULL,
            finished_utc INTEGER NOT NULL,
            trigger TEXT NOT NULL,
            freed_bytes INTEGER NOT NULL,
            deleted INTEGER NOT NULL,
            failed INTEGER NOT NULL
        );
        CREATE TABLE relocations (
            id TEXT PRIMARY KEY,
            app_id TEXT,
            source TEXT NOT NULL,
            target TEXT NOT NULL,
            backup TEXT NOT NULL,
            state TEXT NOT NULL,
            failed_at TEXT,
            bytes INTEGER NOT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            error TEXT
        );
        CREATE TABLE usage_sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            exe TEXT NOT NULL,
            start_utc INTEGER NOT NULL,
            end_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_usage_start ON usage_sessions(start_utc);
        CREATE TABLE process_starts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            start_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_starts_start ON process_starts(start_utc);
        """,
    ];

    private void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode = WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Execute(Migrations[i], transaction: tx);
            c.Execute($"PRAGMA user_version = {i + 1};", transaction: tx);
            tx.Commit();
        }
    }

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
    public static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
}

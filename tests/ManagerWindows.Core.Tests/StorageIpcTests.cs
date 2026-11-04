using System.IO.Pipes;
using ManagerWindows.Core.Priority;
using ManagerWindows.Core.Relocation;
using ManagerWindows.Core.Usage;
using ManagerWindows.Ipc;
using ManagerWindows.Storage;

namespace ManagerWindows.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Backup_copies_database_while_it_is_open()
    {
        using var t = new TempDir();
        var db = new Database(Path.Combine(t.Path, "data.db"));
        var repo = new UsageRepository(db);
        var t0 = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        repo.AddSessions([new UsageSession(@"C:\Office\EXCEL.EXE", t0, t0.AddMinutes(30))]);
        using var keepOpen = db.Open(); // app đang chạy vẫn giữ kết nối

        var copy = Path.Combine(t.Path, "moved", "data.db");
        db.BackupTo(copy);

        var top = new UsageRepository(new Database(copy)).TopApps(t0.AddDays(-1), t0.AddDays(1), 10);
        Assert.Equal("EXCEL", Assert.Single(top).Name);
        Assert.Equal(30 * 60, top[0].ActiveSeconds);
    }

    [Fact]
    public void Usage_top_apps_and_launch_counts()
    {
        using var t = new TempDir();
        var db = new Database(Path.Combine(t.Path, "data.db"));
        var repo = new UsageRepository(db);
        var t0 = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);
        repo.AddSessions([
            new UsageSession(@"C:\Office\EXCEL.EXE", t0, t0.AddMinutes(30)),
            new UsageSession(@"C:\Office\EXCEL.EXE", t0.AddHours(1), t0.AddHours(1).AddMinutes(10)),
            new UsageSession(@"C:\Zalo\Zalo.exe", t0, t0.AddMinutes(5)),
        ]);
        repo.AddProcessStart("EXCEL", t0);
        repo.AddProcessStart("EXCEL", t0.AddHours(1));

        var top = repo.TopApps(t0.AddDays(-1), t0.AddDays(1), 10);

        Assert.Equal("EXCEL", top[0].Name);
        Assert.Equal(40 * 60, top[0].ActiveSeconds);
        Assert.Equal(2, top[0].Launches);
        Assert.Equal(2, top[0].Sessions);
        Assert.Equal(300, top[1].ActiveSeconds);

        // Cắt theo khoảng thời gian: chỉ tính phần nằm trong khoảng.
        var partial = repo.TopApps(t0.AddMinutes(20), t0.AddMinutes(25), 10);
        Assert.Equal(300, partial.Single(x => x.Name == "EXCEL").ActiveSeconds);

        repo.Purge(t0.AddDays(1));
        Assert.Empty(repo.TopApps(t0.AddDays(-1), t0.AddDays(1), 10));
    }

    [Fact]
    public void Relocation_upsert_and_reload()
    {
        using var t = new TempDir();
        var repo = new RelocationRepository(new Database(Path.Combine(t.Path, "data.db")));
        var r = new RelocationRecord { AppId = "zalo", Source = "C:\\a", Target = "D:\\a", Backup = "C:\\a.mwbak", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        repo.Save(r);
        r.State = RelocationState.Failed;
        r.FailedAt = RelocationState.SourceRenamed;
        r.Error = "x";
        repo.Save(r);

        var loaded = Assert.Single(repo.All());
        Assert.Equal(RelocationState.Failed, loaded.State);
        Assert.Equal(RelocationState.SourceRenamed, loaded.FailedAt);
        Assert.Equal("zalo", loaded.AppId);
    }

    [Fact]
    public void Cleanup_runs_total()
    {
        using var t = new TempDir();
        var repo = new CleanupRunRepository(new Database(Path.Combine(t.Path, "data.db")));
        repo.Add(DateTime.UtcNow, DateTime.UtcNow, "manual", 100, 2, 0);
        repo.Add(DateTime.UtcNow, DateTime.UtcNow, "schedule", 50, 1, 1);
        Assert.Equal(150, repo.TotalFreed());
        Assert.Equal("schedule", repo.Recent(1)[0].Trigger);
    }
}

public class IpcTests
{
    [Fact]
    public async Task Roundtrip_over_named_pipe()
    {
        var name = "mwtest-" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cts.Token);
            await IpcServerProtocol.HandleAsync(pipe, req =>
            {
                var p = IpcServerProtocol.Payload<SetPriorityRequest>(req);
                return Task.FromResult(IpcResponse.Success(new { echo = p.Pid, level = p.Level }));
            }, cts.Token);
        });

        var client = new IpcClient(name, 5000);
        var resp = await client.SendAsync(IpcCommands.SetPriority, new SetPriorityRequest(42, PriorityLevel.BelowNormal, true), cts.Token);
        await server;

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal(42, resp.Data!.Value.GetProperty("echo").GetInt32());
        Assert.Equal("BelowNormal", resp.Data!.Value.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Client_reports_missing_service()
    {
        var resp = await new IpcClient("mw-not-running-" + Guid.NewGuid().ToString("N")[..8], 200).SendAsync(IpcCommands.Ping);
        Assert.False(resp.Ok);
    }
}

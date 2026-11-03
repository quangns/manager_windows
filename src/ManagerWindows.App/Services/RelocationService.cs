using System.Diagnostics;
using ManagerWindows.Core.Relocation;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Services;

public sealed class PrecheckFailedException(List<PrecheckProblem> problems)
    : Exception(string.Join(", ", problems))
{
    public List<PrecheckProblem> Problems { get; } = problems;
}

/// <summary>Bọc <see cref="RelocationEngine"/> cho UI: dò thư mục, đóng app, chuyển, hoàn tác, kiểm tra định kỳ.</summary>
public sealed class RelocationService(AppHost host)
{
    private CancellationTokenSource? _cts;
    /// <summary>Mỗi vấn đề chỉ nhắc một lần trong một phiên chạy app.</summary>
    private readonly HashSet<string> _notified = new();

    public List<DetectedFolder> Detect(CancellationToken ct = default) =>
        new AppDataDetector(new JunctionApi()).Detect(host.Profiles, (long)host.Config.Relocation.UnknownFolderMinMb << 20, ct);

    public static List<Process> Running(AppProfile? profile) =>
        profile == null ? [] : profile.ProcessNames.SelectMany(Process.GetProcessesByName).ToList();

    /// <summary>Đóng nhẹ (như bấm X) trước, quá thời gian mới buộc dừng.</summary>
    public static bool CloseAll(AppProfile profile, TimeSpan timeout)
    {
        foreach (var p in Running(profile)) using (p) try { p.CloseMainWindow(); } catch { }
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && Running(profile).Count > 0) Thread.Sleep(500);
        foreach (var p in Running(profile))
            using (p)
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
        Thread.Sleep(1000); // đợi Windows nhả khoá file
        return Running(profile).Count == 0;
    }

    /// <summary>Thư mục đích riêng cho từng user: D:\AppDataMoved\&lt;user&gt;.</summary>
    public string UserTargetRoot(string root) => Path.Combine(root, Environment.UserName);

    public RelocationRecord Move(DetectedFolder folder, string targetRoot, CancellationToken ct = default)
    {
        var engine = host.CreateEngine();
        var userRoot = UserTargetRoot(targetRoot);
        var record = engine.Create(folder.Path, userRoot, folder.Profile?.Id ?? "custom");
        var problems = engine.Precheck(record);
        if (problems.Count > 0) throw new PrecheckFailedException(problems);
        PrivateFolder.Ensure(userRoot);
        host.Relocations.Save(record);
        engine.Run(record, ct);
        Log.Info("relocation", $"{record.Source} -> {record.Target}: {record.State}");
        return record;
    }

    public void Resume(RelocationRecord r, CancellationToken ct = default) => host.CreateEngine().Run(r, ct);

    public void Rollback(RelocationRecord r, CancellationToken ct = default)
    {
        host.CreateEngine().Rollback(r, ct);
        Log.Info("relocation", $"rollback {r.Source}");
    }

    public void DeleteBackup(RelocationRecord r) => host.CreateEngine().DeleteBackup(r);

    public RelocationHealth Check(RelocationRecord r) => host.CreateEngine().Check(r);

    /// <summary>Hoàn tác tất cả (dùng khi gỡ cài đặt: ManagerWindows.App.exe --rollback-all).</summary>
    public (int Ok, int Failed) RollbackAll()
    {
        int ok = 0, failed = 0;
        foreach (var r in host.Relocations.Active())
        {
            try
            {
                var profile = host.Profiles.FirstOrDefault(p => p.Id == r.AppId);
                if (profile != null) CloseAll(profile, TimeSpan.FromSeconds(10));
                Rollback(r);
                ok++;
            }
            catch (Exception ex)
            {
                Log.Error("rollback-all", ex);
                failed++;
            }
        }
        return (ok, failed);
    }

    /// <summary>Mỗi giờ kiểm tra junction còn trỏ đúng và nhắc xoá backup khi đã quá hạn giữ.</summary>
    public void StartHealthChecks()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try { CheckAll(); }
                catch (Exception ex) { Log.Error("relocation-health", ex); }
            } while (await timer.WaitForNextTickAsync(ct));
        }, ct);
    }

    private void CheckAll()
    {
        foreach (var r in host.Relocations.Active())
        {
            var h = Check(r);
            if (!_notified.Add($"{r.Id}:{h}:{r.State}")) continue;
            if (h is RelocationHealth.TargetMissing or RelocationHealth.LinkMissing or RelocationHealth.LinkWrongTarget)
                host.RaiseNotify(Loc.Instance["Notify_Title"],
                    Loc.F("Reloc_HealthIssue", ("path", r.Source), ("problem", Loc.Instance["Health_" + h])), "relocation");
            else if (r.State == RelocationState.Completed &&
                     (DateTime.UtcNow - r.UpdatedUtc).TotalDays >= host.Config.Relocation.BackupKeepDays)
                host.RaiseNotify(Loc.Instance["Notify_Title"],
                    Loc.F("Reloc_BackupDue", ("path", r.Backup), ("days", host.Config.Relocation.BackupKeepDays)), "relocation");
        }
    }

    public void Stop() => _cts?.Cancel();
}

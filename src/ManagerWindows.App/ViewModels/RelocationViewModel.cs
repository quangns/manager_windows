using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Relocation;

namespace ManagerWindows.App.ViewModels;

public sealed class FolderRow(DetectedFolder f)
{
    public DetectedFolder Folder { get; } = f;
    public string AppName => Folder.Profile?.Name ?? Loc.Instance["Reloc_Unknown"];
    public string Path => Folder.Path;
    public string SizeText => Fmt.Bytes(Folder.Bytes);
    public bool IsManual => Folder.Profile?.Method == RelocationMethod.Manual;
    public bool CanMove => !Folder.IsLinked && !IsManual;
    public bool IsLinked => Folder.IsLinked;
    public string StatusText => Folder.IsLinked
        ? Loc.F("Reloc_Linked", ("target", Folder.LinkTarget))
        : IsManual ? Loc.Instance["Reloc_Manual"] : "";
    public string Note => Folder.Profile?.Note(Loc.Instance.Lang) ?? "";
}

public sealed class RecordRow(RelocationRecord r, RelocationHealth health)
{
    public RelocationRecord Record { get; } = r;
    public string Source => Record.Source;
    public string Target => Record.Target;
    public string SizeText => Fmt.Bytes(Record.Bytes);
    public string StateText => Loc.Instance["State_" + Record.State] + (Record.Error != null ? $" — {Record.Error}" : "");
    public string HealthText => health == RelocationHealth.NotApplicable ? "" : Loc.Instance["Health_" + health];
    public bool HealthOk => health == RelocationHealth.Healthy;
    public bool HealthBad => health is RelocationHealth.TargetMissing or RelocationHealth.LinkMissing or RelocationHealth.LinkWrongTarget;
    public bool CanRollback => Record.State is not RelocationState.RolledBack;
    public bool CanDeleteBackup => Record.State == RelocationState.Completed && health == RelocationHealth.Healthy;
    public bool CanResume => Record.State == RelocationState.Failed;
    public string Time => Record.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

public sealed partial class RelocationViewModel : ViewModelBase
{
    public ObservableCollection<FolderRow> Folders { get; } = new();
    public ObservableCollection<RecordRow> Records { get; } = new();

    [ObservableProperty] private string _targetRoot;
    [ObservableProperty] private FolderRow? _selectedFolder;

    public RelocationViewModel() => _targetRoot = Host.Config.Relocation.DefaultTargetRoot;

    public void LoadRecords()
    {
        Records.Clear();
        foreach (var r in Host.Relocations.All().Take(50))
            Records.Add(new RecordRow(r, Host.Relocation.Check(r)));
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(TargetRoot) ? TargetRoot : null };
        if (dlg.ShowDialog() == true) TargetRoot = dlg.FolderName;
    }

    [RelayCommand]
    private Task DetectAsync() => RunBusyAsync(async () =>
    {
        SaveTargetRoot();
        await DetectAsyncInner();
        Status = Loc.F("Reloc_DetectResult", ("count", Folders.Count),
            ("size", Fmt.Bytes(Folders.Where(f => !f.Folder.IsLinked).Sum(f => f.Folder.Bytes))));
    });

    private void SaveTargetRoot()
    {
        if (Host.Config.Relocation.DefaultTargetRoot == TargetRoot) return;
        Host.Config.Relocation.DefaultTargetRoot = TargetRoot;
        Host.SaveConfig();
    }

    [RelayCommand]
    private Task MoveAsync(FolderRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !row.CanMove) { Status = ""; return; }
        SaveTargetRoot();
        var profile = row.Folder.Profile;
        var apps = profile == null ? "-" : string.Join(", ", profile.ProcessNames);
        if (!Confirm(Loc.F("Reloc_ConfirmMove", ("path", row.Path), ("size", row.SizeText),
                ("target", Host.Relocation.UserTargetRoot(TargetRoot)), ("apps", apps))))
        {
            Status = "";
            return;
        }

        if (profile != null && RelocationService.Running(profile).Count > 0)
        {
            if (!Confirm(Loc.F("Reloc_AppsRunning", ("apps", apps)))) { Status = ""; return; }
            Status = Loc.Instance["Reloc_Closing"];
            var closed = await Task.Run(() => RelocationService.CloseAll(profile, TimeSpan.FromSeconds(15)));
            if (!closed) { Status = Loc.F("Reloc_CloseFailed", ("apps", apps)); return; }
        }

        Status = Loc.Instance["Reloc_Copying"];
        try
        {
            await Task.Run(() => Host.Relocation.Move(row.Folder, TargetRoot));
            Status = Loc.F("Reloc_Done", ("path", row.Path));
            Info(Loc.F("Reloc_Done", ("path", row.Path)) + "\n\n" + Loc.Instance["Reloc_DoneHint"]);
        }
        catch (PrecheckFailedException ex)
        {
            Status = string.Join(" ", ex.Problems.Select(p => Loc.Instance["Problem_" + p]));
            Info(Status);
        }
        catch (Exception ex)
        {
            Log.Error("relocation", ex);
            Status = Loc.F("Reloc_Failed", ("error", ex.Message));
            Info(Status);
        }
        await DetectAsyncInner();
    });

    private async Task DetectAsyncInner()
    {
        var found = await Task.Run(() => Host.Relocation.Detect());
        Folders.Clear();
        foreach (var f in found) Folders.Add(new FolderRow(f));
        LoadRecords();
    }

    private async Task<bool> CloseAppFor(RelocationRecord r)
    {
        var profile = Host.Profiles.FirstOrDefault(p => p.Id == r.AppId);
        if (profile == null || RelocationService.Running(profile).Count == 0) return true;
        var apps = string.Join(", ", profile.ProcessNames);
        if (!Confirm(Loc.F("Reloc_AppsRunning", ("apps", apps)))) return false;
        return await Task.Run(() => RelocationService.CloseAll(profile, TimeSpan.FromSeconds(15)));
    }

    [RelayCommand]
    private Task RollbackAsync(RecordRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !Confirm(Loc.F("Reloc_ConfirmRollback", ("path", row.Source)))) { Status = ""; return; }
        if (!await CloseAppFor(row.Record)) { Status = ""; return; }
        await Task.Run(() => Host.Relocation.Rollback(row.Record));
        Status = Loc.F("Reloc_RolledBack", ("path", row.Source));
        await DetectAsyncInner();
    });

    [RelayCommand]
    private Task ResumeAsync(RecordRow? row) => RunBusyAsync(async () =>
    {
        if (row == null) return;
        if (!await CloseAppFor(row.Record)) { Status = ""; return; }
        await Task.Run(() => Host.Relocation.Resume(row.Record));
        Status = Loc.F("Reloc_Done", ("path", row.Source));
        await DetectAsyncInner();
    });

    [RelayCommand]
    private Task DeleteBackupAsync(RecordRow? row) => RunBusyAsync(async () =>
    {
        if (row == null || !Confirm(Loc.F("Reloc_ConfirmDeleteBackup", ("path", row.Record.Backup)))) { Status = ""; return; }
        await Task.Run(() => Host.Relocation.DeleteBackup(row.Record));
        Status = Loc.Instance["Done"];
        LoadRecords();
    });
}

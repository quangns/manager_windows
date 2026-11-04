using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Ipc;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

public sealed partial class LeftoverRow(LeftoverGroup g, string displayName) : ObservableObject
{
    public LeftoverGroup Group { get; } = g;
    public string Name => Group.Name;
    public string DisplayName { get; } = displayName;
    public int Count => Group.Pids.Count;
    public long Ram => Group.WorkingSetBytes;
    public string RamText => Fmt.Bytes(Group.WorkingSetBytes);
    public bool IsHung => Group.Reasons.HasFlag(LeftoverReason.NotResponding);
    public bool IsOrphan => Group.Reasons.HasFlag(LeftoverReason.Orphan);
    public bool IsIdle => Group.Reasons.HasFlag(LeftoverReason.IdleBackground);
    public string AgeText => Group.OldestStartUtc is { } s ? Fmt.Age(DateTime.UtcNow - s) : "?";
    public string Pids => string.Join(", ", Group.Pids);

    [ObservableProperty] private bool _isChecked;
}

/// <summary>Cửa sổ "Tiến trình sót": quét, chọn, dừng; không bao giờ tự dừng.</summary>
public sealed partial class LeftoversViewModel : ViewModelBase
{
    public ObservableCollection<LeftoverRow> Items { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;

    public static List<LeftoverGroup> Scan(AppHost host) =>
        LeftoverDetector.Detect(ProcessTree.Snapshot(), Process.GetCurrentProcess().SessionId, Environment.ProcessId,
            DateTime.UtcNow, host.Config.Leftovers, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async () =>
    {
        var groups = await Task.Run(() => Scan(Host));
        foreach (var i in Items) i.PropertyChanged -= OnRowChanged;
        Items.Clear();
        foreach (var g in groups)
        {
            var row = new LeftoverRow(g, RunningApps.Describe(g.Name, g.Path));
            row.PropertyChanged += OnRowChanged;
            Items.Add(row);
        }
        IsEmpty = Items.Count == 0;
        UpdateSummary();
        Status = IsEmpty ? Loc.Instance["Left_NoneFound"] : Loc.F("Left_Found", ("count", Items.Count), ("ram", Fmt.Bytes(groups.Sum(g => g.WorkingSetBytes))));
    });

    private void OnRowChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverRow.IsChecked)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var sel = Items.Where(i => i.IsChecked).ToList();
        Summary = Loc.F("Left_Selected", ("count", sel.Count), ("procs", sel.Sum(s => s.Count)), ("ram", Fmt.Bytes(sel.Sum(s => s.Ram))));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsChecked = true;
    }

    [RelayCommand]
    private Task StopSelectedAsync() => StopAsync(Items.Where(i => i.IsChecked).ToList());

    [RelayCommand]
    private Task StopOneAsync(LeftoverRow? row) => row == null ? Task.CompletedTask : StopAsync([row]);

    private Task StopAsync(List<LeftoverRow> rows) => RunBusyAsync(async () =>
    {
        if (rows.Count == 0) { Status = ""; return; }
        var names = string.Join("\n• ", rows.Select(r => $"{r.DisplayName} ({r.Count})").Prepend(""));
        if (!Confirm(Loc.F("Left_ConfirmStop", ("apps", names))))
        {
            Status = "";
            return;
        }
        int ok = 0, failed = 0;
        foreach (var pid in rows.SelectMany(r => r.Group.Pids))
        {
            var done = await Task.Run(() =>
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                    return true;
                }
                catch (ArgumentException) { return true; } // đã tự thoát
                catch (Exception) { return false; }
            });
            if (!done) done = (await Host.Service.SendAsync(IpcCommands.KillProcess, new KillProcessRequest(pid))).Ok;
            if (done) ok++; else failed++;
        }
        Log.Info("leftovers", $"stopped {ok}, failed {failed}: {string.Join(",", rows.Select(r => r.Name))}");
        var freed = rows.Sum(r => r.Ram);
        await ScanAsync();
        Status = Loc.F(failed == 0 ? "Left_Stopped" : "Left_StoppedPartial", ("count", ok), ("failed", failed), ("ram", Fmt.Bytes(freed)));
    });

    /// <summary>Không đưa app này vào danh sách tiến trình sót nữa.</summary>
    [RelayCommand]
    private void Ignore(LeftoverRow? row)
    {
        if (row == null) return;
        var opt = Host.Config.Leftovers;
        if (!opt.Ignored.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
        {
            opt.Ignored = [.. opt.Ignored, row.Name];
            Host.SaveConfig();
        }
        row.PropertyChanged -= OnRowChanged;
        Items.Remove(row);
        IsEmpty = Items.Count == 0;
        UpdateSummary();
        Status = Loc.F("Left_Ignored", ("app", row.DisplayName));
    }

    [RelayCommand]
    private static void OpenLocation(LeftoverRow? row)
    {
        if (row?.Group.Path is { } path)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}

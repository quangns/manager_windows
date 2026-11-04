using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerMac.Services;
using ManagerWindows.Core.Cleaning;

namespace ManagerMac.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    [ObservableProperty] private string _diskFree = "";
    [ObservableProperty] private string _diskTotal = "";
    [ObservableProperty] private double _diskUsed;
    [ObservableProperty] private bool _diskLow;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private double _ramUsed;
    [ObservableProperty] private string _totalFreed = "";
    [ObservableProperty] private string _lastCleanup = "";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var (disk, total, used, freed, last) = await Task.Run(() =>
        {
            var d = new DriveInfo("/");
            return (d, MacPlatform.TotalMemory(), MacPlatform.UsedMemory(), Host.Runs.TotalFreed(), Host.Runs.Recent(1).FirstOrDefault());
        });
        // APFS: AvailableFreeSpace chưa gồm phần "purgeable" mà Finder có thể hiển thị thêm.
        DiskFree = Fmt.Bytes(disk.AvailableFreeSpace);
        DiskTotal = Fmt.Bytes(disk.TotalSize);
        DiskUsed = disk.TotalSize == 0 ? 0 : 1 - (double)disk.AvailableFreeSpace / disk.TotalSize;
        DiskLow = DiskUsed > 0.9;
        RamUsed = total == 0 ? 0 : (double)used / total;
        RamText = $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}";
        TotalFreed = freed > 0 ? Fmt.Bytes(freed) : Loc.Instance["Dash_NoData"];
        LastCleanup = last == null ? Loc.Instance["Dash_Never"]
            : $"{last.FinishedUtc.ToLocalTime():dd/MM HH:mm} · {Fmt.Bytes(last.FreedBytes)}";
    }

    /// <summary>Dọn nhanh các luật đang bật, bỏ qua mục "nên xem lại" (phải xem file trước ở trang Dọn rác).</summary>
    [RelayCommand]
    private Task QuickCleanAsync() => RunBusyAsync(async () =>
    {
        var rules = Host.Rules.Where(r => r.Risk != RuleRisk.Review && Host.IsEnabled(r)).ToList();
        var result = await Task.Run(() => Host.Execute(Host.Plan(rules), "manual"));
        Status = Loc.F("Clean_Result", ("size", Fmt.Bytes(result.FreedBytes)), ("deleted", result.Deleted), ("failed", result.Failed));
        await RefreshAsync();
    });
}

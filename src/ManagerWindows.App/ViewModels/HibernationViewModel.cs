using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Monitoring;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một app trong danh sách không bao giờ ngủ đông.</summary>
public sealed record ExcludedRow(string Name, string DisplayName, bool IsNew = false);

public sealed record SleepingRow(string Name, string DisplayName, string SinceText, string RamText, int Count);

/// <summary>Một app trong danh sách theo dõi: đã không dùng bao lâu và vì sao có/không được cho ngủ.</summary>
public sealed record WatchRow(string Name, string DisplayName, string IdleText, string StatusText, string Level, bool CanSleepNow, bool CanExclude);

public sealed partial class HibernationViewModel : ViewModelBase
{
    public ObservableCollection<SleepingRow> Sleeping { get; } = new();
    public ObservableCollection<WatchRow> Watch { get; } = new();
    public ObservableCollection<ExcludedRow> Excluded { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _idleMinutes;
    [ObservableProperty] private bool _onlyMinimized;
    [ObservableProperty] private int _minRamMb;
    [ObservableProperty] private string _newExcludedName = "";
    /// <summary>Đặt true để đưa con trỏ nhập vào ô tên app (sau khi thả tâm ngắm).</summary>
    [ObservableProperty] private bool _focusNameBox;
    [ObservableProperty] private ExcludedRow? _selectedExcluded;
    /// <summary>Dòng chữ cạnh tâm ngắm: đang trỏ vào app nào / vừa thêm app nào.</summary>
    [ObservableProperty] private string _pickerText = "";
    [ObservableProperty] private bool _hasSleeping;
    [ObservableProperty] private string _sleepingSummary = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public HibernationViewModel()
    {
        var o = Host.Config.Hibernation;
        (_enabled, _idleMinutes, _onlyMinimized, _minRamMb) = (o.Enabled, o.IdleMinutes, o.OnlyMinimized, o.MinRamMb);
        LoadExcluded();
        LoadTabSleep();
        _pickerText = Loc.Instance["Hib_PickerHint"];
        _timer.Tick += (_, _) => Refresh();
        Host.Hibernation.Changed += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
    }

    public void Start()
    {
        _justAdded.Clear(); // mở lại tab: danh sách đã lưu, sắp xếp lại theo tên
        LoadExcluded(); // có thể vừa thêm từ tab Tiến trình
        RefreshTabSleepState();
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Refresh()
    {
        var asleep = Host.Hibernation.Asleep();
        Sleeping.Clear();
        foreach (var a in asleep.OrderBy(a => a.SinceUtc))
            Sleeping.Add(new SleepingRow(a.Name, RunningApps.Describe(a.Name, null), a.SinceUtc.ToLocalTime().ToString("HH:mm"),
                Fmt.Bytes(a.RamBeforeBytes), a.Processes.Count));
        HasSleeping = Sleeping.Count > 0;
        SleepingSummary = Loc.F("Hib_SleepingSummary", ("count", asleep.Count), ("ram", Fmt.Bytes(asleep.Sum(a => a.RamBeforeBytes))));

        var opt = Host.Config.Hibernation;
        Watch.Clear();
        foreach (var d in Host.Hibernation.LastDecisions
                     .Where(d => d.Verdict is not (HibernationVerdict.NoWindow or HibernationVerdict.Protected or HibernationVerdict.AlreadyAsleep))
                     .OrderBy(d => d.Verdict == HibernationVerdict.InUse ? 1 : 0).ThenByDescending(d => d.Idle))
        {
            var left = TimeSpan.FromMinutes(opt.IdleMinutes) - d.Idle;
            var status = d.Verdict == HibernationVerdict.InUse
                ? (d.Idle < TimeSpan.FromMinutes(1) ? Loc.Instance["Hib_V_Active"] : Loc.F("Hib_V_SleepIn", ("time", Fmt.Duration(Math.Max(60, left.TotalSeconds)))))
                : Loc.Instance["Hib_V_" + d.Verdict];
            var level = d.Verdict switch
            {
                HibernationVerdict.Hibernate => "ok",
                HibernationVerdict.Excluded or HibernationVerdict.PlayingAudio or HibernationVerdict.OwnsClipboard or HibernationVerdict.Busy => "warn",
                _ => "info",
            };
            Watch.Add(new WatchRow(d.Name, RunningApps.Describe(d.Name, null), Fmt.Duration(d.Idle.TotalSeconds), status, level,
                d.Verdict is HibernationVerdict.Hibernate or HibernationVerdict.InUse or HibernationVerdict.NotMinimized or HibernationVerdict.TooSmall,
                d.Verdict != HibernationVerdict.Excluded));
        }
    }

    [RelayCommand]
    private void Wake(SleepingRow? row)
    {
        if (row == null) return;
        Host.Hibernation.Wake(row.Name, "manual");
        Status = Loc.F("Hib_Woken", ("app", row.DisplayName));
    }

    [RelayCommand]
    private void WakeAll()
    {
        Host.Hibernation.WakeAll("manual");
        Status = Loc.Instance["Hib_WokenAll"];
    }

    [RelayCommand]
    private Task SleepNowAsync(WatchRow? row) => RunBusyAsync(async () =>
    {
        if (row == null) return;
        var v = await Task.Run(() => Host.Hibernation.HibernateNow(row.Name));
        Status = v == HibernationVerdict.Hibernate
            ? Loc.F("Hib_Slept", ("app", row.DisplayName))
            : Loc.F("Hib_CannotSleep", ("app", row.DisplayName), ("reason", Loc.Instance["Hib_V_" + v]));
        Refresh();
    });

    /// <summary>App vừa thêm (mới nhất trước, đã lưu ngay), hiện trên đầu danh sách với nhãn "Mới thêm" cho tới khi mở lại tab.</summary>
    private readonly List<string> _justAdded = new();

    private void LoadExcluded()
    {
        var list = Host.Config.Hibernation.Excluded;
        var fresh = _justAdded.Where(n => list.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        Excluded.Clear();
        foreach (var n in fresh)
            Excluded.Add(new ExcludedRow(n, RunningApps.Describe(n, ProcessPathOf(n)), IsNew: true));
        foreach (var n in list.Where(n => !fresh.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Excluded.Add(new ExcludedRow(n, RunningApps.Describe(n, ProcessPathOf(n))));
    }

    private static string? ProcessPathOf(string name)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(name);
        try { return procs.Length > 0 ? ManagerWindows.Platform.SystemInfo.ProcessPath(procs[0].Id) : null; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Thêm app vào danh sách không bao giờ ngủ (lưu ngay) và đánh thức nếu nó đang ngủ.</summary>
    private ExcludeResult AddExcluded(string name, string display)
    {
        var r = Host.Hibernation.Exclude(name);
        if (r == ExcludeResult.Added)
        {
            var normalized = HibernationOptions.NormalizeName(name);
            _justAdded.RemoveAll(n => n.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _justAdded.Insert(0, normalized);
        }
        Status = r switch
        {
            ExcludeResult.Added => Loc.F("Hib_Excluded", ("app", display)),
            ExcludeResult.AlreadyListed => Loc.F("Hib_AlreadyExcluded", ("app", display)),
            _ => Loc.Instance["Hib_PickerInvalid"],
        };
        LoadExcluded();
        // Đã có sẵn: chọn dòng đó để người dùng thấy ngay.
        SelectedExcluded = Excluded.FirstOrDefault(e => e.Name.Equals(HibernationOptions.NormalizeName(name), StringComparison.OrdinalIgnoreCase));
        return r;
    }

    [RelayCommand]
    private void Exclude(WatchRow? row)
    {
        if (row != null) AddExcluded(row.Name, row.DisplayName);
    }

    /// <summary>Nút "Thêm" / Enter: thêm vào danh sách và lưu luôn (không cần bấm Lưu).</summary>
    [RelayCommand]
    private void AddExcludedByName()
    {
        var name = HibernationOptions.NormalizeName(NewExcludedName.Trim());
        if (name.Length == 0) return;
        var display = _pickedDisplay is { } p && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? p.Display : name;
        var r = AddExcluded(name, display);
        if (r == ExcludeResult.NotAllowed) return; // giữ chữ trong ô để sửa
        NewExcludedName = "";
        _pickedDisplay = null;
        PickerText = Loc.Instance["Hib_PickerHint"];
        if (r == ExcludeResult.Added) Status = Loc.F("Hib_AddedSaved", ("app", display));
    }

    [RelayCommand]
    private void RemoveExcluded(ExcludedRow? row)
    {
        if (row == null) return;
        var o = Host.Config.Hibernation;
        o.Excluded = o.Excluded.Where(n => !n.Equals(row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        Host.SaveConfig();
        LoadExcluded();
        Status = Loc.F("Hib_Unexcluded", ("app", row.DisplayName));
    }

    /// <summary>Tên app của tiến trình (null nếu không đọc được); kèm tên hiển thị.</summary>
    private static (string Name, string Display)? AppOf(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return (p.ProcessName, RunningApps.Describe(p.ProcessName, ManagerWindows.Platform.SystemInfo.ProcessPath(pid)));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Đang kéo tâm ngắm: hiện app dưới con trỏ.</summary>
    [RelayCommand]
    private void PickHover(int pid)
    {
        PickerText = pid == Environment.ProcessId ? Loc.Instance["Hib_PickerSelf"]
            : AppOf(pid) is { } a ? Loc.F(Host.Config.Hibernation.IsExcluded(a.Name) ? "Hib_PickerOverListed" : "Hib_PickerOver", ("app", a.Display), ("name", a.Name))
            : Loc.Instance["Hib_PickerNone"];
    }

    /// <summary>
    /// Thả tâm ngắm lên cửa sổ app: điền tên tiến trình của app vào ô nhập để xác nhận; bấm "Thêm" (hoặc Enter) mới thêm và lưu.
    /// </summary>
    [RelayCommand]
    private void Picked(int pid)
    {
        var a = pid == Environment.ProcessId ? null : AppOf(pid);
        Log.Info("hibernation", $"thả tâm ngắm: pid={pid} app={a?.Name ?? "?"}");
        if (a is not { } app || !HibernationService.CanExclude(app.Name))
        {
            PickerText = pid == Environment.ProcessId ? Loc.Instance["Hib_PickerSelf"] : Loc.Instance["Hib_PickerInvalid"];
            return;
        }
        NewExcludedName = app.Name;
        _pickedDisplay = (app.Name, app.Display);
        PickerText = Host.Config.Hibernation.IsExcluded(app.Name)
            ? Loc.F("Hib_AlreadyExcluded", ("app", app.Display))
            : Loc.F("Hib_PickerConfirm", ("app", app.Display), ("name", app.Name));
        FocusNameBox = true;
        FocusNameBox = false;
    }

    /// <summary>Tên hiển thị của app vừa chọn bằng tâm ngắm (để thông báo đẹp hơn khi bấm Thêm).</summary>
    private (string Name, string Display)? _pickedDisplay;

    [RelayCommand]
    private async Task Save()
    {
        var o = Host.Config.Hibernation;
        o.Enabled = Enabled;
        o.IdleMinutes = Math.Clamp(IdleMinutes, 1, 1440);
        o.OnlyMinimized = OnlyMinimized;
        o.MinRamMb = Math.Clamp(MinRamMb, 0, 100_000);
        Host.SaveConfig();
        IdleMinutes = o.IdleMinutes;
        if (!o.Enabled) Host.Hibernation.WakeAll("disabled");
        Status = Loc.Instance["Saved"];
        await SaveTabSleepAsync();
    }
}

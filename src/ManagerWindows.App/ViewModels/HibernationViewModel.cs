using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Monitoring;

namespace ManagerWindows.App.ViewModels;

public sealed record SleepingRow(string Name, string DisplayName, string SinceText, string RamText, int Count);

/// <summary>Một app trong danh sách theo dõi: đã không dùng bao lâu và vì sao có/không được cho ngủ.</summary>
public sealed record WatchRow(string Name, string DisplayName, string IdleText, string StatusText, string Level, bool CanSleepNow, bool CanExclude);

public sealed partial class HibernationViewModel : ViewModelBase
{
    public ObservableCollection<SleepingRow> Sleeping { get; } = new();
    public ObservableCollection<WatchRow> Watch { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _idleMinutes;
    [ObservableProperty] private bool _onlyMinimized;
    [ObservableProperty] private int _minRamMb;
    [ObservableProperty] private string _excludedText;
    [ObservableProperty] private bool _hasSleeping;
    [ObservableProperty] private string _sleepingSummary = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public HibernationViewModel()
    {
        var o = Host.Config.Hibernation;
        (_enabled, _idleMinutes, _onlyMinimized, _minRamMb) = (o.Enabled, o.IdleMinutes, o.OnlyMinimized, o.MinRamMb);
        _excludedText = TextList.Join(o.Excluded);
        _timer.Tick += (_, _) => Refresh();
        Host.Hibernation.Changed += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
    }

    public void Start()
    {
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

    /// <summary>Thêm app vào danh sách không bao giờ ngủ đông.</summary>
    [RelayCommand]
    private void Exclude(WatchRow? row)
    {
        if (row == null) return;
        var o = Host.Config.Hibernation;
        if (!o.Excluded.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
        {
            o.Excluded = [.. o.Excluded, row.Name];
            Host.SaveConfig();
        }
        ExcludedText = TextList.Join(o.Excluded);
        Status = Loc.F("Hib_Excluded", ("app", row.DisplayName));
    }

    [RelayCommand]
    private void Save()
    {
        var o = Host.Config.Hibernation;
        o.Enabled = Enabled;
        o.IdleMinutes = Math.Clamp(IdleMinutes, 1, 1440);
        o.OnlyMinimized = OnlyMinimized;
        o.MinRamMb = Math.Clamp(MinRamMb, 0, 100_000);
        o.Excluded = TextList.Split(ExcludedText).Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToList();
        Host.SaveConfig();
        IdleMinutes = o.IdleMinutes;
        if (!o.Enabled) Host.Hibernation.WakeAll("disabled");
        Status = Loc.Instance["Saved"];
    }
}

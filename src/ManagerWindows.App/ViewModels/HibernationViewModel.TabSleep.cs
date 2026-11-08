using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Monitoring;
using ManagerWindows.Platform;

namespace ManagerWindows.App.ViewModels;

/// <summary>Một trình duyệt trong thẻ "Ngủ đông tab": có cài không, có chọn không, chính sách đang thế nào, RAM đang dùng.</summary>
public sealed partial class TabSleepBrowserRow(TabSleepBrowser browser, string name, bool installed) : ObservableObject
{
    public TabSleepBrowser Browser { get; } = browser;
    public string Name { get; } = name;
    public bool Installed { get; } = installed;
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _level = "info"; // ok / warn / info: màu huy hiệu trạng thái (style LevelBadge)
    [ObservableProperty] private string _ramText = "";
}

/// <summary>
/// Ngủ đông tab trình duyệt: bật Memory Saver có sẵn của Chrome / Cốc Cốc / Brave bằng chính sách Windows (do dịch vụ nền ghi).
/// Tab nền lâu không dùng được trình duyệt giải phóng; bấm vào tab thì tự tải lại.
/// </summary>
public sealed partial class HibernationViewModel
{
    public ObservableCollection<TabSleepBrowserRow> TabSleepBrowsers { get; } = new();
    [ObservableProperty] private bool _tabSleepEnabled;
    /// <summary>0 = Vừa, 1 = Cân bằng, 2 = Tối đa (đúng giá trị MemorySaverModeSavings).</summary>
    [ObservableProperty] private int _tabSleepSavings;
    [ObservableProperty] private string _tabSleepExceptions = "";
    [ObservableProperty] private string _tabSleepNote = "";

    public string[] TabSleepSavingsChoices { get; } =
        [Loc.Instance["TabSleep_Moderate"], Loc.Instance["TabSleep_Balanced"], Loc.Instance["TabSleep_Maximum"]];

    private void LoadTabSleep()
    {
        var o = Host.Config.TabSleep;
        TabSleepEnabled = o.Enabled;
        TabSleepSavings = (int)o.Savings;
        TabSleepExceptions = string.Join(Environment.NewLine, o.Exceptions);
        TabSleepBrowsers.Clear();
        foreach (var info in BrowserCatalog.All)
        {
            var row = new TabSleepBrowserRow(info.Browser, info.DisplayName, BrowserPolicies.IsInstalled(info.Browser))
            {
                Selected = o.Browsers.Contains(info.Browser),
            };
            TabSleepBrowsers.Add(row);
        }
        RefreshTabSleepState();
    }

    /// <summary>Đọc lại chính sách đang có (HKLM đọc được với quyền người dùng) và RAM của từng trình duyệt.</summary>
    private void RefreshTabSleepState()
    {
        string[] levels = [Loc.Instance["TabSleep_Moderate"], Loc.Instance["TabSleep_Balanced"], Loc.Instance["TabSleep_Maximum"]];
        foreach (var row in TabSleepBrowsers)
        {
            if (!row.Installed)
            {
                (row.State, row.Level, row.RamText) = (Loc.Instance["TabSleep_NotInstalled"], "info", "");
                continue;
            }
            var p = BrowserPolicies.Read(row.Browser);
            (row.State, row.Level) = p.Enabled switch
            {
                1 => (Loc.F("TabSleep_StateOn", ("level", levels[Math.Clamp(p.Savings ?? 1, 0, 2)])), "ok"),
                0 => (Loc.Instance["TabSleep_StateBlocked"], "warn"),
                _ => (Loc.Instance["TabSleep_StateOff"], "info"),
            };
            row.RamText = Fmt.Bytes(BrowserRam(BrowserCatalog.Get(row.Browser).ProcessName));
        }
    }

    private static long BrowserRam(string processName)
    {
        long total = 0;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                try { total += p.WorkingSet64; }
                catch (InvalidOperationException) { } // tiến trình vừa thoát
            }
        }
        return total;
    }

    /// <summary>Lưu tuỳ chọn rồi nhờ dịch vụ nền ghi chính sách. Gọi từ nút Lưu của tab.</summary>
    private async Task SaveTabSleepAsync()
    {
        var o = Host.Config.TabSleep;
        var wasEnabled = o.Enabled;
        var (valid, invalid) = BrowserPolicyPlanner.ParseExceptions(TabSleepExceptions);
        o.Enabled = TabSleepEnabled;
        o.Savings = (TabSleepSavings)Math.Clamp(TabSleepSavings, 0, 2);
        o.Browsers = TabSleepBrowsers.Where(b => b.Selected).Select(b => b.Browser).ToList();
        o.Exceptions = valid;
        Host.SaveConfig();
        TabSleepExceptions = string.Join(Environment.NewLine, valid);
        if (!o.Enabled && !wasEnabled) { TabSleepNote = ""; return; } // không đổi gì với trình duyệt

        var r = await TabSleepApplier.ApplyAsync(Host.Service,
            new TabSleepRequest(o.Enabled, (int)o.Savings, o.Browsers.Select(b => b.ToString()).ToList(), valid));
        var notes = new List<string>();
        if (r.Cancelled) notes.Add(Loc.Instance["TabSleep_UacCancelled"]);
        else if (r.Error != null)
        {
            notes.Add(Loc.F("TabSleep_Failed", ("error", r.Error)));
            Log.Info("tab-sleep", $"Không áp dụng được: {r.Error}");
        }
        else
        {
            var conflicted = r.Results.Where(x => x.Conflicts.Count > 0)
                .Select(x => BrowserCatalog.All.FirstOrDefault(b => b.Browser.ToString() == x.Browser)?.DisplayName ?? x.Browser).ToList();
            notes.Add(Loc.Instance[o.Enabled ? "TabSleep_Applied" : "TabSleep_Removed"]);
            if (conflicted.Count > 0) notes.Add(Loc.F("TabSleep_Conflict", ("browsers", string.Join(", ", conflicted))));
        }
        if (invalid.Count > 0) notes.Add(Loc.F("TabSleep_InvalidSkipped", ("items", string.Join(", ", invalid))));
        TabSleepNote = string.Join(" ", notes);
        RefreshTabSleepState();
    }
}

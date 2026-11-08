using ManagerWindows.Core.Monitoring;

namespace ManagerWindows.Core.Tests;

public class BrowserTabSleepTests
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private static readonly HashSet<string> None = [];

    [Fact]
    public void Enable_on_clean_machine_sets_all_three_and_owns_them()
    {
        var plan = BrowserPolicyPlanner.Build(TabSleepBrowser.Chrome, true, TabSleepSavings.Maximum, ["chat.zalo.me", "meet.google.com"], PolicySnapshot.Empty, None);
        Assert.Empty(plan.Conflicts);
        Assert.Contains(new SetDwordOp(Chrome, "HighEfficiencyModeEnabled", 1), plan.Ops);
        Assert.Contains(new SetDwordOp(Chrome, "MemorySaverModeSavings", 2), plan.Ops);
        Assert.Contains(new SetStringOp(Chrome + @"\TabDiscardingExceptions", "1", "chat.zalo.me"), plan.Ops);
        Assert.Contains(new SetStringOp(Chrome + @"\TabDiscardingExceptions", "2", "meet.google.com"), plan.Ops);
        Assert.Equal(["HighEfficiencyModeEnabled", "MemorySaverModeSavings", "TabDiscardingExceptions"], plan.Owned.Order());
        // Máy sạch: không có khoá cũ nào cần xoá.
        Assert.DoesNotContain(plan.Ops, o => o is DeleteKeyOp or DeleteValueOp);
    }

    [Fact]
    public void Never_overwrites_policies_set_by_an_administrator()
    {
        var adminSet = new PolicySnapshot(Enabled: 0, Savings: null, HasExceptions: true);
        var plan = BrowserPolicyPlanner.Build(TabSleepBrowser.Chrome, true, TabSleepSavings.Balanced, ["a.com"], adminSet, None);
        Assert.Equal(["HighEfficiencyModeEnabled", "TabDiscardingExceptions"], plan.Conflicts);
        Assert.Equal([new SetDwordOp(Chrome, "MemorySaverModeSavings", 1)], plan.Ops);
        Assert.Equal(["MemorySaverModeSavings"], plan.Owned);
    }

    [Fact]
    public void Updating_rewrites_own_exception_list()
    {
        HashSet<string> owned = ["HighEfficiencyModeEnabled", "MemorySaverModeSavings", "TabDiscardingExceptions"];
        var plan = BrowserPolicyPlanner.Build(TabSleepBrowser.Chrome, true, TabSleepSavings.Moderate, ["x.com", "X.com ", "y.com"], new PolicySnapshot(1, 1, true), owned);
        Assert.Empty(plan.Conflicts);
        var ex = Chrome + @"\TabDiscardingExceptions";
        Assert.Equal(new DeleteKeyOp(ex), plan.Ops.OfType<DeleteKeyOp>().Single());
        Assert.Equal(["x.com", "y.com"], plan.Ops.OfType<SetStringOp>().Select(o => o.Value)); // bỏ trùng, không phân biệt hoa thường
        Assert.True(plan.Ops.IndexOf(new DeleteKeyOp(ex)) < plan.Ops.FindIndex(o => o is SetStringOp)); // xoá trước, ghi sau
    }

    [Fact]
    public void Disable_removes_only_what_the_app_set()
    {
        var plan = BrowserPolicyPlanner.Build(TabSleepBrowser.Brave, false, TabSleepSavings.Balanced, [], new PolicySnapshot(1, 2, true), new HashSet<string> { "HighEfficiencyModeEnabled" });
        Assert.Equal([new DeleteValueOp(@"SOFTWARE\Policies\BraveSoftware\Brave", "HighEfficiencyModeEnabled")], plan.Ops);
        Assert.Empty(plan.Owned);
        Assert.Empty(BrowserPolicyPlanner.Build(TabSleepBrowser.CocCoc, false, TabSleepSavings.Balanced, [], new PolicySnapshot(1, 1, true), None).Ops);
    }

    [Theory]
    [InlineData("chat.zalo.me", true)]
    [InlineData("https://meet.google.com/*", true)]
    [InlineData("[*.]youtube.com", true)]
    [InlineData("", false)]
    [InlineData("has space.com", false)]
    [InlineData("tab\tchar", false)]
    [InlineData("quote\".com", false)]
    public void Validates_url_patterns(string pattern, bool ok) => Assert.Equal(ok, BrowserPolicyPlanner.IsValidException(pattern));

    [Fact]
    public void Parses_exception_box_lines_and_commas()
    {
        var (valid, invalid) = BrowserPolicyPlanner.ParseExceptions("chat.zalo.me\r\nmeet.google.com, bad url\n\nCHAT.zalo.me");
        Assert.Equal(["chat.zalo.me", "meet.google.com"], valid);
        Assert.Equal(["bad url"], invalid);
        var (many, over) = BrowserPolicyPlanner.ParseExceptions(string.Join("\n", Enumerable.Range(0, 105).Select(i => $"s{i}.com")));
        Assert.Equal(100, many.Count);
        Assert.Equal(5, over.Count);
    }

    [Fact]
    public void Defaults_are_off_and_keep_chat_sites_awake()
    {
        var o = new BrowserTabSleepOptions();
        Assert.False(o.Enabled);
        Assert.Equal(TabSleepSavings.Balanced, o.Savings);
        Assert.Contains("chat.zalo.me", o.Exceptions);
        Assert.All(o.Exceptions, e => Assert.True(BrowserPolicyPlanner.IsValidException(e)));
        Assert.Equal(3, BrowserCatalog.All.Length);
    }
}

using System.IO.Compression;
using System.Text;
using ManagerWindows.Core.Excel;
using ManagerWindows.Core.Priority;
using ManagerWindows.Core.Scheduling;
using ManagerWindows.Core.Usage;

namespace ManagerWindows.Core.Tests;

public class UsageTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Consecutive_samples_form_sessions_and_idle_closes_them()
    {
        var agg = new UsageAggregator(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++) agg.Sample(T0.AddSeconds(i * 2), @"C:\excel.exe", false);
        agg.Sample(T0.AddSeconds(10), @"C:\zalo.exe", false);
        agg.Sample(T0.AddSeconds(12), @"C:\zalo.exe", false);
        agg.Sample(T0.AddSeconds(14), @"C:\zalo.exe", idle: true);
        agg.Sample(T0.AddSeconds(16), @"C:\zalo.exe", idle: true);

        var s = agg.Drain();
        Assert.Equal(2, s.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), s[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(4), s[1].Duration);
        Assert.Empty(agg.Drain());
    }

    [Fact]
    public void Long_gap_splits_session()
    {
        var agg = new UsageAggregator(TimeSpan.FromSeconds(2));
        agg.Sample(T0, "a.exe", false);
        agg.Sample(T0.AddMinutes(30), "a.exe", false); // máy ngủ 30 phút
        agg.Close();

        var s = agg.Drain();
        Assert.Equal(2, s.Count);
        Assert.All(s, x => Assert.Equal(TimeSpan.FromSeconds(2), x.Duration));
    }
}

public class PriorityTests
{
    private static ProcessSnapshot P(string name, PriorityLevel? cur = PriorityLevel.Normal, bool fg = false) =>
        new(name.GetHashCode(), name, cur, fg, 0, 0);

    [Fact]
    public void Pinned_and_top_used_get_above_normal_background_gets_throttled()
    {
        var opt = new PriorityOptions { Pinned = ["EXCEL"], TopUsedCount = 1, Background = ["OneDrive"] };
        var d = PriorityPolicy.Decide([P("EXCEL"), P("Zalo"), P("chrome"), P("OneDrive"), P("notepad")], ["Zalo", "chrome"], opt, false);

        Assert.Equal(PriorityLevel.AboveNormal, d.Single(x => x.Name == "EXCEL").Target);
        Assert.Equal(PriorityLevel.AboveNormal, d.Single(x => x.Name == "Zalo").Target);
        Assert.DoesNotContain(d, x => x.Name == "chrome"); // ngoài top 1
        var od = d.Single(x => x.Name == "OneDrive");
        Assert.Equal(PriorityLevel.BelowNormal, od.Target);
        Assert.True(od.EcoQoS);
        Assert.DoesNotContain(d, x => x.Name == "notepad");
    }

    [Fact]
    public void Never_touches_protected_or_inaccessible_processes()
    {
        var opt = new PriorityOptions { Pinned = ["lsass", "csrss", "app"], AllowHigh = true };
        var d = PriorityPolicy.Decide([P("lsass"), P("csrss"), P("app", cur: null)], [], opt, false);
        Assert.Empty(d);
    }

    [Fact]
    public void High_requires_opt_in()
    {
        var d1 = PriorityPolicy.Decide([P("EXCEL")], [], new PriorityOptions { Pinned = ["EXCEL"] }, false);
        var d2 = PriorityPolicy.Decide([P("EXCEL")], [], new PriorityOptions { Pinned = ["EXCEL"], AllowHigh = true }, false);
        Assert.Equal(PriorityLevel.AboveNormal, d1[0].Target);
        Assert.Equal(PriorityLevel.High, d2[0].Target);
    }

    [Fact]
    public void Excel_heavy_boosts_excel_and_throttles_browsers_except_foreground()
    {
        var opt = new PriorityOptions { TopUsedCount = 0 };
        var d = PriorityPolicy.Decide([P("EXCEL"), P("chrome"), P("msedge", fg: true)], [], opt, excelHeavy: true);

        Assert.Equal(PriorityLevel.AboveNormal, d.Single(x => x.Name == "EXCEL").Target);
        Assert.Equal(PriorityLevel.BelowNormal, d.Single(x => x.Name == "chrome").Target);
        Assert.DoesNotContain(d, x => x.Name == "msedge");
    }

    [Fact]
    public void Excel_heavy_throttles_even_top_used_browser_but_not_pinned()
    {
        var opt = new PriorityOptions { TopUsedCount = 2, Pinned = ["Zalo"] };
        var d = PriorityPolicy.Decide([P("EXCEL"), P("chrome"), P("Zalo")], ["chrome", "Zalo"], opt, excelHeavy: true);

        Assert.Equal(PriorityLevel.BelowNormal, d.Single(x => x.Name == "chrome").Target);
        Assert.Equal(PriorityLevel.AboveNormal, d.Single(x => x.Name == "Zalo").Target);
    }

    [Fact]
    public void Disabled_policy_does_nothing()
    {
        Assert.Empty(PriorityPolicy.Decide([P("EXCEL")], ["EXCEL"], new PriorityOptions { Enabled = false }, true));
    }
}

public class ScheduleTests
{
    [Fact]
    public void Weekly_schedule_due_after_slot_and_runs_once()
    {
        var s = new Schedule { Frequency = Frequency.Weekly, Day = DayOfWeek.Sunday, Hour = 12, RequireIdleMinutes = 10 };
        var sundayNoon = new DateTime(2026, 11, 1, 12, 0, 0); // Chủ nhật
        Assert.Equal(DayOfWeek.Sunday, sundayNoon.DayOfWeek);

        Assert.False(ScheduleEvaluator.IsDue(s, sundayNoon.AddDays(-7).AddMinutes(5), sundayNoon.AddMinutes(-1), 60, false));
        Assert.True(ScheduleEvaluator.IsDue(s, sundayNoon.AddDays(-7).AddMinutes(5), sundayNoon.AddMinutes(1), 60, false));
        Assert.False(ScheduleEvaluator.IsDue(s, sundayNoon.AddMinutes(1), sundayNoon.AddHours(3), 60, false));
        // Lỡ mốc vì tắt máy: thứ Ba bật máy vẫn chạy bù.
        Assert.True(ScheduleEvaluator.IsDue(s, sundayNoon.AddDays(-7), sundayNoon.AddDays(2), 60, false));
    }

    [Fact]
    public void Requires_idle_and_ac_power()
    {
        var s = new Schedule { Frequency = Frequency.Daily, Hour = 0, RequireIdleMinutes = 10, SkipOnBattery = true };
        var now = new DateTime(2026, 11, 3, 15, 0, 0);
        Assert.False(ScheduleEvaluator.IsDue(s, null, now, 2, false));
        Assert.False(ScheduleEvaluator.IsDue(s, null, now, 20, true));
        Assert.True(ScheduleEvaluator.IsDue(s, null, now, 20, false));
        Assert.False(ScheduleEvaluator.IsDue(new Schedule { Frequency = Frequency.Off }, null, now, 99, false));
    }
}

public class ExcelTests
{
    private static string MakeXlsx(string dir, int formulaRows, string dimension, int styles = 10, bool external = false, string calcMode = "auto")
    {
        var path = Path.Combine(dir, "book.xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(content);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Add("xl/workbook.xml",
            $"<workbook xmlns=\"{ns}\" xmlns:r=\"{rns}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcMode=\"{calcMode}\"/></workbook>");
        Add("xl/_rels/workbook.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"ws\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        var sb = new StringBuilder($"<worksheet xmlns=\"{ns}\"><dimension ref=\"{dimension}\"/><sheetData>");
        for (var i = 1; i <= formulaRows; i++)
            sb.Append($"<row r=\"{i}\"><c r=\"A{i}\"><f>INDIRECT(\"B{i}\")</f><v>1</v></c><c r=\"B{i}\"><v>2</v></c></row>");
        sb.Append("</sheetData><conditionalFormatting sqref=\"A1\"/></worksheet>");
        Add("xl/worksheets/sheet1.xml", sb.ToString());
        Add("xl/styles.xml", $"<styleSheet xmlns=\"{ns}\"><cellXfs count=\"{styles}\"/></styleSheet>");
        if (external) Add("xl/externalLinks/externalLink1.xml", "<externalLink/>");
        return path;
    }

    [Fact]
    public void Counts_cells_formulas_and_volatile_functions()
    {
        using var t = new TempDir();
        var r = XlsxAnalyzer.Analyze(MakeXlsx(t.Path, 150, "A1:B150"));

        Assert.True(r.Parsed, r.ParseError);
        var sh = Assert.Single(r.Sheets);
        Assert.Equal("Data", sh.Name);
        Assert.Equal(300, sh.Cells); // ReadString không được nuốt ô B kế tiếp
        Assert.Equal(150, sh.Formulas);
        Assert.Equal(150, sh.VolatileFormulas);
        Assert.Equal(1, sh.ConditionalFormats);
        Assert.Equal(300, sh.DimensionCells);
        Assert.Contains(r.Suggestions, s => s.Code == "VolatileFormulas");
    }

    [Fact]
    public void Detects_ghost_range_styles_and_external_links()
    {
        using var t = new TempDir();
        var r = XlsxAnalyzer.Analyze(MakeXlsx(t.Path, 2, "A1:XFD1048576", styles: 60000, external: true),
            new ExcelEnvironment { Office64Bit = false, FreeMemoryPercent = 8 });

        var codes = r.Suggestions.Select(s => s.Code).ToList();
        Assert.Contains("GhostUsedRange", codes);
        Assert.Contains("TooManyStyles", codes);
        Assert.Contains("ExternalLinks", codes);
        Assert.Contains("LowMemory", codes);
        Assert.DoesNotContain("Office32Bit", codes); // file nhỏ thì không cần 64-bit
    }

    [Fact]
    public void Binary_formats_get_generic_suggestions()
    {
        using var t = new TempDir();
        var p = t.File("old.xls", 1000);
        var r = XlsxAnalyzer.Analyze(p);
        Assert.False(r.Parsed);
        Assert.Contains(r.Suggestions, s => s.Code == "OldXlsFormat");
    }

    [Theory]
    [InlineData("A1:B2", 4)]
    [InlineData("$A$1:$C$10", 30)]
    [InlineData("A1", 1)]
    [InlineData("A1:XFD1048576", 16384L * 1048576L)]
    public void Range_size(string range, long cells) => Assert.Equal(cells, XlsxAnalyzer.CellsInRange(range));

    [Fact]
    public void Heavy_detector()
    {
        var o = new ExcelOptions();
        Assert.True(ExcelHeavyDetector.IsHeavy(o, 40L << 20, 0, 0));
        Assert.True(ExcelHeavyDetector.IsHeavy(o, null, 2000L << 20, 0));
        Assert.False(ExcelHeavyDetector.IsHeavy(o, 1L << 20, 200L << 20, 90));
        Assert.False(ExcelHeavyDetector.IsHeavy(new ExcelOptions { Enabled = false }, 40L << 20, 0, 0));
    }
}

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;

namespace ManagerWindows.Core.Excel;

public sealed class SheetStats
{
    public string Name { get; set; } = "";
    /// <summary>Vùng dữ liệu Excel ghi nhận (thẻ dimension), vd. A1:XFD1048576.</summary>
    public string? Dimension { get; set; }
    public long DimensionCells { get; set; }
    public long Cells { get; set; }
    public long Formulas { get; set; }
    public long VolatileFormulas { get; set; }
    public int ConditionalFormats { get; set; }
    public long XmlBytes { get; set; }
}

public enum Severity { Info, Warning, Critical }

public sealed record ExcelSuggestion(string Code, Severity Severity, Dictionary<string, string>? Args = null);

public sealed class ExcelReport
{
    public string Path { get; set; } = "";
    public long FileBytes { get; set; }
    /// <summary>false với .xls/.xlsb (định dạng nhị phân, chỉ đưa gợi ý chung).</summary>
    public bool Parsed { get; set; }
    public string? ParseError { get; set; }
    public List<SheetStats> Sheets { get; } = new();
    public int CellStyles { get; set; }
    public int SharedStrings { get; set; }
    public int ExternalLinks { get; set; }
    public int PivotCaches { get; set; }
    public int MediaCount { get; set; }
    public long MediaBytes { get; set; }
    public bool HasVba { get; set; }
    /// <summary>auto | manual | autoNoTable (thuộc tính calcMode trong workbook.xml).</summary>
    public string CalcMode { get; set; } = "auto";
    public List<ExcelSuggestion> Suggestions { get; } = new();
}

public sealed class ExcelEnvironment
{
    public bool? Office64Bit { get; set; }
    public double? FreeMemoryPercent { get; set; }
}

/// <summary>
/// Đọc thẳng file .xlsx/.xlsm (là file zip chứa XML) để tìm nguyên nhân làm file nặng, không cần mở Excel.
/// Dùng XmlReader dạng luồng nên đọc được sheet hàng trăm MB mà không tốn nhiều RAM.
/// </summary>
public static partial class XlsxAnalyzer
{
    public const long LargeFileBytes = 30L << 20;

    [GeneratedRegex(@"\b(INDIRECT|OFFSET|NOW|TODAY|RAND|RANDBETWEEN|CELL|INFO)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex VolatileRegex();

    [GeneratedRegex(@"^([A-Z]+)(\d+)$")]
    private static partial Regex CellRefRegex();

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static ExcelReport Analyze(string path, ExcelEnvironment? env = null, CancellationToken ct = default)
    {
        var report = new ExcelReport { Path = path, FileBytes = new FileInfo(path).Length };
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xlsm")
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                ParsePackage(zip, report, ct);
                report.Parsed = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
            {
                report.ParseError = ex.Message;
            }
        }
        BuildSuggestions(report, env ?? new ExcelEnvironment());
        return report;
    }

    private static void ParsePackage(ZipArchive zip, ExcelReport report, CancellationToken ct)
    {
        var rels = ReadWorkbookRels(zip);
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Thiếu xl/workbook.xml");
        var sheets = new List<(string Name, string Target)>();
        using (var r = XmlReader.Create(workbook.Open(), Settings()))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.NamespaceURI != MainNs) continue;
                if (r.LocalName == "sheet")
                {
                    var rid = r.GetAttribute("id", RelNs);
                    if (rid != null && rels.TryGetValue(rid, out var target))
                        sheets.Add((r.GetAttribute("name") ?? "", target));
                }
                else if (r.LocalName == "calcPr")
                {
                    report.CalcMode = r.GetAttribute("calcMode") ?? "auto";
                }
                else if (r.LocalName == "pivotCache") report.PivotCaches++;
            }
        }

        foreach (var (name, target) in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(target);
            if (entry == null) continue;
            report.Sheets.Add(ParseSheet(name, entry));
        }

        report.CellStyles = CountAttr(zip.GetEntry("xl/styles.xml"), "cellXfs");
        report.SharedStrings = CountAttr(zip.GetEntry("xl/sharedStrings.xml"), "sst", "uniqueCount");
        foreach (var e in zip.Entries)
        {
            if (e.FullName.StartsWith("xl/externalLinks/externalLink", StringComparison.OrdinalIgnoreCase)) report.ExternalLinks++;
            else if (e.FullName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase))
            {
                report.MediaCount++;
                report.MediaBytes += e.Length;
            }
            else if (e.FullName.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase)) report.HasVba = true;
        }
        if (report.PivotCaches == 0)
            report.PivotCaches = zip.Entries.Count(e => e.FullName.StartsWith("xl/pivotCache/pivotCacheDefinition", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ReadWorkbookRels(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var e = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (e == null) return map;
        using var r = XmlReader.Create(e.Open(), Settings());
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
            var id = r.GetAttribute("Id");
            var target = r.GetAttribute("Target");
            if (id == null || target == null) continue;
            // Target tương đối với thư mục xl/, hoặc tuyệt đối bắt đầu bằng "/".
            map[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
        return map;
    }

    private static SheetStats ParseSheet(string name, ZipArchiveEntry entry)
    {
        var s = new SheetStats { Name = name, XmlBytes = entry.Length };
        using var r = XmlReader.Create(entry.Open(), Settings());
        var more = r.Read();
        for (; more; more = r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.NamespaceURI != MainNs) continue;
            switch (r.LocalName)
            {
                case "dimension":
                    s.Dimension = r.GetAttribute("ref");
                    s.DimensionCells = CellsInRange(s.Dimension);
                    break;
                case "c":
                    s.Cells++;
                    break;
                case "f":
                    s.Formulas++;
                    if (!r.IsEmptyElement)
                    {
                        // ReadString dừng ở thẻ đóng </f>, không nuốt thẻ <c> kế tiếp.
                        var text = r.ReadString();
                        if (VolatileRegex().IsMatch(text)) s.VolatileFormulas++;
                    }
                    break;
                case "conditionalFormatting":
                    s.ConditionalFormats++;
                    break;
            }
        }
        return s;
    }

    private static int CountAttr(ZipArchiveEntry? entry, string element, string attr = "count")
    {
        if (entry == null) return 0;
        using var r = XmlReader.Create(entry.Open(), Settings());
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == element)
                return int.TryParse(r.GetAttribute(attr), out var n) ? n : 0;
        return 0;
    }

    internal static long CellsInRange(string? range)
    {
        if (string.IsNullOrEmpty(range)) return 0;
        var parts = range.Split(':');
        var a = ParseRef(parts[0]);
        var b = parts.Length > 1 ? ParseRef(parts[1]) : a;
        if (a == null || b == null) return 0;
        return (Math.Abs(b.Value.Col - a.Value.Col) + 1L) * (Math.Abs(b.Value.Row - a.Value.Row) + 1L);
    }

    private static (int Col, int Row)? ParseRef(string cell)
    {
        var m = CellRefRegex().Match(cell.Replace("$", ""));
        if (!m.Success) return null;
        var col = 0;
        foreach (var ch in m.Groups[1].Value) col = col * 26 + (ch - 'A' + 1);
        return (col, int.Parse(m.Groups[2].Value));
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
    };

    internal static void BuildSuggestions(ExcelReport r, ExcelEnvironment env)
    {
        var s = r.Suggestions;
        var large = r.FileBytes >= LargeFileBytes;
        string Mb(long b) => (b / 1048576.0).ToString("0.#");

        if (env.Office64Bit == false && large)
            s.Add(new("Office32Bit", Severity.Critical));
        if (env.FreeMemoryPercent is < 15)
            s.Add(new("LowMemory", Severity.Critical, new() { ["percent"] = env.FreeMemoryPercent.Value.ToString("0") }));

        var ext = System.IO.Path.GetExtension(r.Path).ToLowerInvariant();
        if (large && ext is ".xlsx" or ".xlsm")
            s.Add(new("SaveAsXlsb", Severity.Warning, new() { ["size"] = Mb(r.FileBytes) }));
        if (ext == ".xls")
            s.Add(new("OldXlsFormat", Severity.Warning));

        foreach (var sh in r.Sheets)
        {
            // Vùng dữ liệu lớn gấp nhiều lần số ô thực có: thường do định dạng cả cột/dòng hoặc xoá dữ liệu nhưng giữ định dạng.
            if (sh.DimensionCells > 1_000_000 && sh.DimensionCells > sh.Cells * 20)
                s.Add(new("GhostUsedRange", Severity.Warning, new() { ["sheet"] = sh.Name, ["range"] = sh.Dimension ?? "" }));
            if (sh.ConditionalFormats > 50)
                s.Add(new("ManyConditionalFormats", Severity.Warning, new() { ["sheet"] = sh.Name, ["count"] = sh.ConditionalFormats.ToString() }));
        }

        var volatileCount = r.Sheets.Sum(x => x.VolatileFormulas);
        if (volatileCount > 100)
            s.Add(new("VolatileFormulas", Severity.Warning, new() { ["count"] = volatileCount.ToString() }));
        var formulas = r.Sheets.Sum(x => x.Formulas);
        if (formulas > 100_000 && r.CalcMode != "manual")
            s.Add(new("ManualCalculation", Severity.Warning, new() { ["count"] = formulas.ToString() }));
        if (r.CellStyles > 4000)
            s.Add(new("TooManyStyles", Severity.Warning, new() { ["count"] = r.CellStyles.ToString() }));
        if (r.ExternalLinks > 0)
            s.Add(new("ExternalLinks", Severity.Warning, new() { ["count"] = r.ExternalLinks.ToString() }));
        if (r.MediaBytes > 10L << 20)
            s.Add(new("LargeImages", Severity.Warning, new() { ["count"] = r.MediaCount.ToString(), ["size"] = Mb(r.MediaBytes) }));
        if (r.PivotCaches > 3)
            s.Add(new("ManyPivotCaches", Severity.Info, new() { ["count"] = r.PivotCaches.ToString() }));
        if (r.Sheets.Sum(x => x.Cells) > 5_000_000)
            s.Add(new("UsePowerQuery", Severity.Info));

        // Gợi ý chung luôn có.
        s.Add(new("DisableAddins", Severity.Info));
        s.Add(new("DisableHardwareAcceleration", Severity.Info));
    }
}

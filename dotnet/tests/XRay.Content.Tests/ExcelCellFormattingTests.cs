using System.IO.Compression;
using System.Text;
using XRay.Content.Core;
using XRay.Content.Extractors;
using XRay.Content.Internal.Biff;
using XRay.Content.Internal.Cfb;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// A spreadsheet's cells reaching the document the way the spreadsheet shows them, and the
/// switch that puts them back the way upstream renders them.
/// </summary>
/// <remarks>
/// The OOXML cases build their own package rather than reaching for a fixture, so the wiring
/// from <c>styles.xml</c> through to the rendered table is covered on a clean clone. The
/// binary-workbook case needs a real .xls and follows the suite's convention of standing down
/// when the corpus is not checked out — <c>CorpusPresenceTests</c> is what reports that.
/// </remarks>
public sealed class ExcelCellFormattingTests
{
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// A workbook of one sheet whose cells carry the given (value, style index) pairs across a
    /// single row, over a <c>styles.xml</c> defining <paramref name="formatCodes"/> as cellXfs
    /// entries 0..n in order.
    /// </summary>
    private static byte[] Workbook(IEnumerable<(string Value, int Style)> cells, params string[] formatCodes)
    {
        var numFmts = new StringBuilder();
        var cellXfs = new StringBuilder();
        for (int i = 0; i < formatCodes.Length; i++)
        {
            int id = 200 + i;
            numFmts.Append($"<numFmt numFmtId=\"{id}\" formatCode=\"{formatCodes[i]}\"/>");
            cellXfs.Append($"<xf numFmtId=\"{id}\"/>");
        }

        var row = new StringBuilder();
        int col = 0;
        foreach (var (value, style) in cells)
        {
            char letter = (char)('A' + col++);
            row.Append($"<c r=\"{letter}1\" s=\"{style}\"><v>{value}</v></c>");
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Write(zip, "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Write(zip, "xl/styles.xml",
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + $"<numFmts>{numFmts}</numFmts><cellXfs>{cellXfs}</cellXfs></styleSheet>");
            Write(zip, "xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + $"<sheetData><row r=\"1\">{row}</row></sheetData></worksheet>");
        }
        return buffer.ToArray();

        static void Write(ZipArchive zip, string path, string xml)
        {
            using var stream = zip.CreateEntry(path).Open();
            var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static List<string> FirstRow(byte[] workbook, ExtractionConfig? config = null)
    {
        var doc = new XlsxExtractor().Extract(workbook, XlsxMime, config ?? new ExtractionConfig());
        return doc.Tables[0].Cells[0];
    }

    // ── the two the report named ──────────────────────────────────────────────

    /// <summary>
    /// The percentage and the currency from `xls/test_excel.xls`, through the OOXML reader:
    /// what a cell shows, rather than the double behind it.
    /// </summary>
    [Fact]
    public void ACellComesOutTheWayTheSheetShowsIt()
    {
        var cells = FirstRow(Workbook(
            new[] { ("0.032280358222708555", 0), ("31200", 1) },
            "0.0%", "_(&quot;$&quot;* #,##0_);_(&quot;$&quot;* \\(#,##0\\);_(&quot;$&quot;* &quot;-&quot;??_);_(@_)"));

        Assert.Equal(new[] { "3.2%", "$31,200" }, cells);
    }

    /// <summary>A date is a serial number until its format says otherwise.</summary>
    [Fact]
    public void ADateCellComesOutAsADate() =>
        Assert.Equal(new[] { "1/1/2008" }, FirstRow(Workbook(new[] { ("39448", 0) }, "m/d/yyyy")));

    /// <summary>
    /// A cell whose format is General is untouched, which is most of the corpus and the reason
    /// this change moves so few fixtures.
    /// </summary>
    [Fact]
    public void AGeneralCellIsLeftAlone() =>
        Assert.Equal(new[] { "31200" }, FirstRow(Workbook(new[] { ("31200", 0) }, "General")));

    /// <summary>
    /// A cell with no style at all takes the first cellXfs entry, as an unstyled cell does in
    /// Excel — and a workbook with no styles part keeps every value bare.
    /// </summary>
    [Fact]
    public void AWorkbookWithoutStylesKeepsItsValues()
    {
        var doc = new XlsxExtractor().Extract(
            Workbook(new[] { ("0.5", 0) }), XlsxMime, new ExtractionConfig());
        Assert.Equal(new[] { "0.5" }, doc.Tables[0].Cells[0]);
    }

    /// <summary>
    /// The switch back to upstream's rendering, which is what a run against the Rust goldens
    /// needs: same workbook, bare values.
    /// </summary>
    [Fact]
    public void TheSwitchRestoresTheUpstreamRendering()
    {
        var workbook = Workbook(new[] { ("0.032280358222708555", 0), ("31200", 1) }, "0.0%", "$#,##0");

        Assert.Equal(new[] { "3.2%", "$31,200" }, FirstRow(workbook));
        Assert.Equal(
            new[] { "0.032280358222708555", "31200" },
            FirstRow(workbook, new ExtractionConfig
            {
                Options = new XRayOptions { ExcelNumberFormats = false },
            }));
    }

    /// <summary>The formatted text is what the rendered table shows, not just what the cell
    /// grid holds.</summary>
    [Fact]
    public void TheFormattedTextReachesTheRenderedTable()
    {
        var doc = new XlsxExtractor().Extract(
            Workbook(new[] { ("0.032280358222708555", 0) }, "0.0%"), XlsxMime, new ExtractionConfig());

        string markdown = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Markdown).Content;

        Assert.Contains("3.2%", markdown);
        Assert.DoesNotContain("0.03228", markdown);
    }

    // ── the binary workbook ───────────────────────────────────────────────────

    /// <summary>
    /// The .xls the report was about: its FORMAT and XF records reaching the same renderer, and
    /// its accounting column and percentage cell coming out formatted.
    /// </summary>
    [Fact]
    public void ABinaryWorkbooksFormatRecordsAreApplied()
    {
        var path = FindFixture("xls/test_excel.xls");
        if (path is null) return;

        var comp = CompoundFile.Open(File.ReadAllBytes(path));
        var cells = Assert.Single(BiffReader.ReadSheets(comp)).Cells!;
        var flat = cells.SelectMany(row => row).ToList();

        Assert.Contains("$31,200", flat);
        Assert.Contains("3.2%", flat);
        Assert.DoesNotContain("0.032280358222708555", flat);

        // And with the switch off, exactly what it produced before.
        var bare = Assert.Single(BiffReader.ReadSheets(comp, numberFormats: false)).Cells!;
        var bareFlat = bare.SelectMany(row => row).ToList();
        Assert.Contains("31200", bareFlat);
        Assert.Contains("0.032280358222708555", bareFlat);
    }

    private static string? FindFixture(string relative)
    {
        foreach (var candidate in new[]
        {
            "/workspace/test_documents",
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents"),
        })
        {
            if (!Directory.Exists(candidate)) continue;
            var path = Path.Combine(candidate, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}

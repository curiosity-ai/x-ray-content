using XRay.Content.Core;
using XRay.Content.Extractors;
using XRay.Content.Internal.Cfb;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Tests for the legacy OLE/CFB office extractors (Doc, Ppt, Xls, Msg, Hwp) and the shared
/// <see cref="CompoundFile"/> reader. Fixture-backed tests locate files under a
/// <c>test_documents</c> tree and are skipped gracefully when it is not present, mirroring the
/// Rust <c>#[cfg(test)]</c> "return if file missing" pattern.
/// </summary>
public sealed class CfbOfficeTests
{
    private static string? FindTestDocuments()
    {
        foreach (var candidate in new[]
        {
            "/workspace/test_documents",
            Path.Combine(AppContext.BaseDirectory, "../../../../../../test_documents"),
        })
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static byte[]? Read(string relative)
    {
        var root = FindTestDocuments();
        if (root is null) return null;
        var path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // ── CompoundFile reader ─────────────────────────────────────────────────────

    [Fact]
    public void CompoundFileOpensDocAndReadsWordDocumentStream()
    {
        byte[]? bytes = Read("vendored/unstructured/doc/simple.doc");
        if (bytes is null) return;

        var comp = CompoundFile.Open(bytes);
        Assert.True(comp.Exists("/WordDocument"));
        byte[]? wd = comp.TryReadStream("/WordDocument");
        Assert.NotNull(wd);
        Assert.True(wd!.Length >= 12);
        // Word magic 0xA5EC at the start of the FIB.
        Assert.Equal(0xEC, wd[0]);
        Assert.Equal(0xA5, wd[1]);
    }

    [Fact]
    public void CompoundFileRejectsNonCfb()
    {
        Assert.ThrowsAny<Exception>(() => CompoundFile.Open(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    // ── Doc ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocExtractsTextAndAuthor()
    {
        byte[]? bytes = Read("vendored/unstructured/doc/fake.doc");
        if (bytes is null) return;

        var doc = new DocExtractor().Extract(bytes, "application/msword", new ExtractionConfig());
        Assert.Equal("application/msword", doc.MimeType);
        Assert.NotEmpty(doc.Elements);
        Assert.Equal(new List<string> { "Mr. Miagi" }, doc.Metadata.Authors);
        string text = string.Join("\n", doc.Elements.Select(e => e.Text));
        Assert.Contains("Lorem ipsum", text);
    }

    // ── Ppt ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PptExtractsSlidesAndText()
    {
        byte[]? bytes = Read("ppt/simple.ppt");
        if (bytes is null) return;

        var doc = new PptExtractor().Extract(bytes, "application/vnd.ms-powerpoint", new ExtractionConfig());
        Assert.Equal(PageUnitType.Slide, doc.Metadata.Pages!.UnitType);
        Assert.True(doc.Metadata.Pages.TotalCount > 0);
        string text = string.Join(" ", doc.Elements.Select(e => e.Text));
        Assert.Contains("Title Slide", text);
    }

    [Fact]
    public void PptNumbersSlidesFromDeckStructure()
    {
        byte[]? bytes = Read("ppt/simple.ppt");
        if (bytes is null) return;

        var doc = new PptExtractor().Extract(bytes, "application/vnd.ms-powerpoint", new ExtractionConfig());
        // Two RT_SLIDE containers in the deck, so two slide elements numbered in persist order --
        // not one run-together block from re-splitting the rendered text.
        var slides = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Slide).ToList();
        Assert.Equal(2u, doc.Metadata.Pages.TotalCount);
        Assert.Equal(new uint[] { 1u, 2u }, slides.Select(e => e.Kind.Number).ToArray());
        Assert.Equal(new[] { "Title Slide", "Things to think about" }, slides.Select(e => e.Text).ToArray());
        // The deck's notes containers hold no text, so no notes footnote is emitted.
        Assert.DoesNotContain(doc.Elements, e => e.Kind.Tag == ElementKindTag.FootnoteDefinition);
    }

    // ── Xls (BIFF) ───────────────────────────────────────────────────────────────

    [Fact]
    public void XlsExtractsSheetsAndCells()
    {
        byte[]? bytes = Read("xls/test_excel.xls");
        if (bytes is null) return;

        var doc = new XlsExtractor().Extract(bytes, "application/vnd.ms-excel", new ExtractionConfig());
        Assert.Single(doc.Tables);
        var cells = doc.Tables[0].Cells;
        Assert.Equal("Item", cells[0][0]);
        // Rendered through the cell's number format, as the sheet displays it.
        Assert.Contains("$192,000", cells[1]);
        var excel = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(new List<string> { "Sheet1" }, excel.SheetNames);
    }

    [Fact]
    public void XlsHandlesMultipleSheets()
    {
        byte[]? bytes = Read("xls/tests_example.xls");
        if (bytes is null) return;

        var doc = new XlsExtractor().Extract(bytes, "application/vnd.ms-excel", new ExtractionConfig());
        var excel = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(3u, excel.SheetCount);
    }

    // ── Msg ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MsgExtractsHeadersAndBody()
    {
        byte[]? bytes = Read("email/simple_msg.msg");
        if (bytes is null) return;

        var doc = new MsgExtractor().Extract(bytes, "application/vnd.ms-outlook", new ExtractionConfig());
        Assert.Equal("This is the subject", doc.Metadata.Subject);
        var email = Assert.IsType<EmailMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("peterpan@neverland.com", email.FromEmail);
        Assert.Contains("crocodile@neverland.com", email.ToEmails);
    }

    [Fact]
    public void MsgAdvertisesOutlookMimeOnly()
    {
        var mimes = new MsgExtractor().SupportedMimeTypes.ToList();
        Assert.Equal(new List<string> { "application/vnd.ms-outlook" }, mimes);
    }

    /// <summary>
    /// An attachment whose method is "embedded message" has no binary stream — the message is a
    /// storage to descend into. Reading only the data stream gave a zero-byte attachment and no
    /// text, losing 8.5 KB from this fixture.
    /// </summary>
    [Fact]
    public void MsgInlinesMessagesAttachedAsMessageObjects()
    {
        byte[]? bytes = Read("email/test_email.msg");
        if (bytes is null) return;

        var doc = new MsgExtractor().Extract(bytes, "application/vnd.ms-outlook", new ExtractionConfig());
        var text = string.Join("\n", doc.Elements.Select(e => e.Text));

        Assert.Contains("1 Days Left", text);
        // Body text that exists only inside the attached message.
        Assert.Contains("Ransomware viruses are becoming more widespread", text);
    }

    /// <summary>
    /// A message's own recipients must not be confused with those of a message attached to it.
    /// Walking the whole container rather than the message's direct children found both, so the
    /// outer message claimed the inner one's recipients as its own.
    /// </summary>
    [Fact]
    public void MsgRecipientsExcludeThoseOfAnAttachedMessage()
    {
        byte[]? bytes = Read("email/test_email.msg");
        if (bytes is null) return;

        var doc = new MsgExtractor().Extract(bytes, "application/vnd.ms-outlook", new ExtractionConfig());
        var email = Assert.IsType<EmailMetadata>(doc.Metadata.Format?.Payload);

        Assert.Equal(new List<string> { "\"Sriram Govindan\" <marirs@gmail.com>" }, email.ToEmails);
    }

    // ── Hwp ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two silent-loss bugs, either of which emptied a genuine HWP 5.0 document: compound-file
    /// paths are absolute and were matched against a relative prefix, so no BodyText section was
    /// ever found; and the record tags took the specification's decimal offsets as hexadecimal,
    /// so even once sections were found no paragraph record matched. Neither raised a warning.
    /// The expected strings are cross-checked against the file's own PrvText preview stream.
    /// </summary>
    [Fact]
    public void HwpRecoversParagraphText()
    {
        byte[]? bytes = Read("hwp/styled_document.hwp");
        if (bytes is null) return;

        var doc = new HwpExtractor().Extract(bytes, "application/x-hwp", new ExtractionConfig());
        var text = string.Join("\n", doc.Elements.Select(e => e.Text));

        Assert.Contains("스타일 문서 예제", text);
        Assert.Contains("이것은 일반 단락입니다. 기본 스타일로 작성되었습니다.", text);
        Assert.Contains("텍스트 스타일링", text);
    }

    // ── Registry dispatch: XLS wins over XLSX for application/vnd.ms-excel ────────

    [Fact]
    public void RegistryRoutesMsExcelToXlsExtractor()
    {
        var registry = Registry.RegisterDefaults();
        var extractor = registry.ForMime("application/vnd.ms-excel");
        Assert.IsType<XlsExtractor>(extractor);
    }
}

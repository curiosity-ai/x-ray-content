using XRay.Content.Core;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Per-page re-render in the requested output format
/// (<see cref="ExtractionConfig.RenderPagesInOutputFormat"/>).
/// </summary>
/// <remarks>
/// A page's <c>Content</c> is the concatenation of its element texts, which is what upstream
/// produces: it is not the document's markup. These pin that the opt-in gives a caller the markup
/// per page without changing what <c>Content</c> has always been.
/// </remarks>
public sealed class PageFormattedContentTests
{
    /// <summary>Two pages: a heading and a paragraph on the first, a table on the second.</summary>
    private static InternalDocument TwoPages()
    {
        var doc = new InternalDocument("pdf") { MimeType = "application/pdf" };

        var heading = InternalElement.TextElement(ElementKind.Heading(1), "Chapter One", 0);
        heading.Page = 1;
        doc.Elements.Add(heading);

        var paragraph = InternalElement.TextElement(ElementKind.Paragraph, "Body text.", 0);
        paragraph.Page = 1;
        doc.Elements.Add(paragraph);

        doc.Tables.Add(new Table
        {
            PageNumber = 2,
            Cells = new List<List<string>>
            {
                new() { "Header" },
                new() { "Cell" },
            },
            Columns = new List<string> { "Header" },
        });
        var table = InternalElement.TextElement(ElementKind.Table(0), "", 0);
        table.Page = 2;
        doc.Elements.Add(table);

        return doc;
    }

    private static ExtractedDocument Derived(bool renderPages) =>
        Derive.DeriveExtractionResult(
            TwoPages(), includeDocumentStructure: false, OutputFormat.Markdown,
            htmlOutput: null, renderPagesInOutputFormat: renderPages);

    /// <summary>
    /// Off by default, because the plain per-page content is what upstream produces and what the
    /// goldens pin.
    /// </summary>
    [Fact]
    public void PagesCarryNoRenderedContentUnlessAsked()
    {
        var pages = Assert.IsType<List<PageContent>>(Derived(renderPages: false).Pages);

        Assert.All(pages, page => Assert.Null(page.FormattedContent));
    }

    /// <summary>
    /// The rendered page is the document's markup: a heading keeps its hashes, where the plain
    /// per-page content does not.
    /// </summary>
    [Fact]
    public void ARenderedPageKeepsItsMarkup()
    {
        var page = Assert.IsType<List<PageContent>>(Derived(renderPages: true).Pages)[0];

        Assert.Equal(1u, page.PageNumber);
        Assert.Contains("# Chapter One", page.FormattedContent);
        Assert.Contains("Body text.", page.FormattedContent);
        Assert.DoesNotContain("# Chapter One", page.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table is the case that makes this more than cosmetic: its element carries no text at
    /// all, so the plain per-page content loses it entirely.
    /// </summary>
    [Fact]
    public void ATableSurvivesOnlyInTheRenderedPage()
    {
        var page = Assert.IsType<List<PageContent>>(Derived(renderPages: true).Pages)[1];

        Assert.Equal(2u, page.PageNumber);
        Assert.Contains("Header", page.FormattedContent);
        Assert.Contains("Cell", page.FormattedContent);
        Assert.DoesNotContain("Header", page.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each page is rendered from its own elements only, so one page's content does not leak into
    /// another's — the whole point of asking per page.
    /// </summary>
    [Fact]
    public void APageCarriesOnlyItsOwnContent()
    {
        var pages = Assert.IsType<List<PageContent>>(Derived(renderPages: true).Pages);

        Assert.DoesNotContain("Header", pages[0].FormattedContent!, StringComparison.Ordinal);
        Assert.DoesNotContain("Chapter One", pages[1].FormattedContent!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose elements are not marked with its number keeps the content it came with.
    /// </summary>
    [Fact]
    public void APageWithNoElementsOfItsOwnKeepsItsContent()
    {
        var doc = TwoPages();
        doc.Elements[2].Page = null;

        var pages = Assert.IsType<List<PageContent>>(Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Markdown,
            htmlOutput: null, renderPagesInOutputFormat: true).Pages);

        Assert.Single(pages);
        Assert.NotNull(pages[0].FormattedContent);
    }

    /// <summary>
    /// A document whose extractor prebuilt its pages is skipped entirely: a workbook's page
    /// content is already rendered, and it carries the sheet's name as a heading, which the
    /// element stream does not — so re-rendering the elements would lose it.
    /// </summary>
    [Fact]
    public void PrebuiltPagesAreLeftAsTheirExtractorWroteThem()
    {
        var doc = new InternalDocument("xlsx") { MimeType = "application/vnd.ms-excel" };
        var cell = InternalElement.TextElement(ElementKind.Paragraph, "a", 0);
        cell.Page = 1;
        doc.Elements.Add(cell);
        doc.PrebuiltPages = new List<PageContent>
        {
            new() { PageNumber = 1, Content = "## Sheet1\n\n| a |\n| --- |" },
        };

        var page = Assert.IsType<List<PageContent>>(Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Markdown,
            htmlOutput: null, renderPagesInOutputFormat: true).Pages)[0];

        Assert.Null(page.FormattedContent);
        Assert.Contains("## Sheet1", page.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A markdown extraction carries the plain text too, so one extraction answers both questions.
    /// </summary>
    [Fact]
    public void AFormattedExtractionAlsoCarriesThePlainText()
    {
        var result = Derive.DeriveExtractionResult(
            TwoPages(), includeDocumentStructure: false, OutputFormat.Markdown);

        Assert.Contains("# Chapter One", result.Content, StringComparison.Ordinal);
        Assert.Equal(result.FormattedContent, result.Content);

        Assert.NotNull(result.PlainContent);
        Assert.Contains("Chapter One", result.PlainContent!, StringComparison.Ordinal);
        Assert.DoesNotContain("#", result.PlainContent!, StringComparison.Ordinal);
    }

    /// <summary>A plain extraction's two contents are the same string.</summary>
    [Fact]
    public void APlainExtractionsPlainContentIsItsContent()
    {
        var result = Derive.DeriveExtractionResult(
            TwoPages(), includeDocumentStructure: false, OutputFormat.Plain);

        Assert.Null(result.FormattedContent);
        Assert.Equal(result.Content, result.PlainContent);
    }
}

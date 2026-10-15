// Tests for the OneNote reader: both [MS-ONESTORE] encodings, the [MS-ONE] content model, notebook
// assembly, the extractor, the Graph-style page HTML and OneNote's deep links. The fixtures are
// real files (Fixtures/OneNote/README.md says where each comes from). The page text of every
// fixture was checked against the onenote.rs parser while this reader was written.

using System.Text.RegularExpressions;
using XRay.Content.Core;
using XRay.Content.OneNote;
using XRay.Content.OneNote.Store;
using Xunit;

namespace XRay.Content.Tests;

public class OneNoteTests
{
    private static string Fixture(params string[] parts) => Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures", "OneNote" }.Concat(parts).ToArray());

    private static byte[] Read(params string[] parts) => File.ReadAllBytes(Fixture(parts));

    private static OneNoteSection Section(string folder, string name, OneNoteReadOptions? options = null) =>
        OneNoteSectionReader.Read(Read(folder, name), name, options);

    private static OneNoteSection SectionAt(params string[] parts) => OneNoteSectionReader.Read(Read(parts), parts[^1]);

    private static IEnumerable<OneNoteRichText> Texts(OneNotePage page) => page.Descendants().OfType<OneNoteRichText>();

    // ------------------------------------------------------------------- the store

    [Fact]
    public void RevisionStore_IsDetectedAndReadAsASection()
    {
        var store = OneStore.Read(Read("aspose", "FormattedRichText.one"));

        Assert.Equal(OneStoreEncoding.RevisionStore, store.Encoding);
        Assert.Equal(OneStoreFileKind.Section, store.Kind);
        Assert.Equal(new Guid("a194a04d-272d-45ba-850f-a72bce3f6304"), store.FileGuid);
        var root = store.Root.Current!.GetRoot(RevisionRoles.DefaultContent)!;
        Assert.Equal(OneNoteJcids.SectionNode, root.JcId.Value);
    }

    [Fact]
    public void FsshttpbPackage_IsDetectedAndReadAsASection()
    {
        var store = OneStore.Read(Read("onenote.rs", "Notebook created on OneNote App", "Section.one"));

        Assert.Equal(OneStoreEncoding.Fsshttpb, store.Encoding);
        Assert.Equal(OneStoreFileKind.Section, store.Kind);
        Assert.Equal(new Guid("c9243001-fb54-4c90-b917-5bb4b43186af"), store.FileGuid);
        Assert.Equal(new Guid("d890422d-763f-4156-bbd9-0760ed3342cd"), store.AncestorGuid);
    }

    [Fact]
    public void RevisionStore_WithAnEmbeddedPackage_IsReadThroughThePackage()
    {
        var toc = OneNoteTableOfContents.Read(Read("onenote.rs", "Embedded Package.onetoc2"));

        Assert.Equal(OneStoreEncoding.Fsshttpb, toc.Encoding);
        Assert.Equal(new Guid("f1da443f-a65f-4513-b200-78d8a9910b8d"), toc.FileId);
        Assert.NotEmpty(toc.Entries);
    }

    [Fact]
    public void IsOneStore_RejectsOtherContent()
    {
        Assert.False(OneStore.IsOneStore(new byte[100]));
        Assert.True(OneStore.IsOneStore(Read("aspose", "SimpleTable.one")));
        Assert.Throws<OneNoteFormatException>(() => OneStore.Read(new byte[200]));
    }

    [Fact]
    public void CorruptedFiles_FailWithAFormatExceptionOrStillRead()
    {
        var rng = new Random(20261015);
        foreach (var (folder, name) in new[] { ("aspose", "SimpleHistory.one"), ("onenote.rs", "Linked Quick Notes.one") })
        {
            var original = Read(folder, name);
            for (int i = 0; i < 150; i++)
            {
                var data = (byte[])original.Clone();
                if (i % 2 == 0)
                {
                    for (int k = 0; k < 8; k++) data[rng.Next(64, data.Length)] ^= (byte)(1 << rng.Next(8));
                }
                else
                {
                    data = data[..rng.Next(64, data.Length)];
                }
                try
                {
                    var section = OneNoteSectionReader.Read(data, name, new OneNoteReadOptions { IncludeVersionHistory = true });
                    foreach (var page in section.Pages) OneNoteHtmlWriter.Write(page);
                }
                catch (OneNoteFormatException)
                {
                }
            }
        }
    }

    // -------------------------------------------------------------- the content model

    [Fact]
    public void Page_CarriesIdentityTitleLevelAndTimes()
    {
        var section = Section("aspose", "Issue1Subpages.one");

        Assert.Equal(2, section.Pages.Count);
        var (first, sub) = (section.Pages[0], section.Pages[1]);
        Assert.Equal("Page1", first.Title);
        Assert.Equal(0, first.Level);
        Assert.Equal(0, first.Order);
        Assert.Equal(new Guid("c56cdb17-3e8b-4962-887b-56db7ad782c6"), first.Id);
        Assert.Equal("Subpage2", sub.Title);
        Assert.Equal(1, sub.Level);
        Assert.Equal(1, sub.Order);
        Assert.Equal("Andreas Doebber", first.Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 20, 14, 50, 35, TimeSpan.Zero), first.CreatedDateTime!.Value.AddTicks(-(first.CreatedDateTime.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("Content1", Assert.Single(Texts(first), t => !t.IsTitleText && !t.IsTitleDate && !t.IsTitleTime).Text);
    }

    [Fact]
    public void RichText_KeepsRunFormattingAndHyperlinks()
    {
        var page = Assert.Single(Section("aspose", "FormattedRichText.one").Pages);
        var text = Texts(page).Single(t => t.Text.StartsWith("This is", StringComparison.Ordinal));

        Assert.Equal("This is hyperlink. This text is not a hyperlink.", text.Text);
        var link = Assert.Single(text.Hyperlinks);
        Assert.Equal("hyperlink", link.Text);
        Assert.Equal("www.google.com", link.Url);
        Assert.Contains(text.Runs, r => r.Text == "is" && r.Style.FontSize == 16 && r.Style.FontColor?.ToHex() == "#E84C22");
        Assert.Contains(text.Runs, r => r.Text == "text" && r.Style.Bold && r.Style.Font == "Times New Roman");
        Assert.Contains(text.Runs, r => r.Text == "not" && r.Style.Italic);
        Assert.DoesNotContain(text.Text, "HYPERLINK");
    }

    [Fact]
    public void Table_IsReadRowByRow()
    {
        var page = Assert.Single(Section("aspose", "SimpleTable.one").Pages);
        var table = page.Descendants().OfType<OneNoteTable>().First();

        Assert.Equal(4, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(3, table.ColumnWidths.Count);
        var firstRow = table.Rows[0].Cells.Select(c => string.Concat(c.SelfAndDescendants().OfType<OneNoteRichText>().Select(t => t.Text)));
        Assert.Equal(new[] { "1", "2", "3" }, firstRow);
    }

    [Fact]
    public void NumberedLists_AreNumberedPerLevelAndFormat()
    {
        var page = Assert.Single(Section("aspose", "NumberedListWithTags.one").Pages);
        var items = page.Descendants().OfType<OneNoteOutlineElement>().Where(e => e.List is not null).ToList();
        string MarkerOf(string text) => items.First(e => e.Content is OneNoteRichText t && t.Text == text).List!.Text;

        Assert.Equal("1.", MarkerOf("First"));
        Assert.Equal("a.", MarkerOf("First-first"));
        Assert.Equal("b.", MarkerOf("First-second"));
        Assert.Equal("i.", MarkerOf("First-second-first"));
        Assert.All(items, e => Assert.True(e.List!.IsNumbered));
    }

    [Fact]
    public void NoteTags_CarryTheirSharedDefinition()
    {
        var page = Assert.Single(Section("aspose", "NumberedListWithTags.one").Pages);
        var tagged = Texts(page).First(t => t.Text == "First");

        Assert.Equal(2, tagged.NoteTags.Count);
        Assert.Contains(tagged.NoteTags, t => t.Label == "Важно" && t.Shape == 13);
        Assert.Contains(tagged.NoteTags, t => t.Label == "Вопрос" && t.Shape == 15);
        Assert.All(tagged.NoteTags, t => Assert.False(t.IsCheckable));
    }

    [Fact]
    public void VersionHistory_IsReadOnRequest()
    {
        var without = Assert.Single(Section("aspose", "SimpleHistory.one").Pages);
        var with = Assert.Single(Section("aspose", "SimpleHistory.one", new OneNoteReadOptions { IncludeVersionHistory = true }).Pages);

        Assert.True(without.HasVersionPages);
        Assert.Empty(without.Versions);
        Assert.Equal("Third text", with.Title);
        var versionTexts = with.Versions.SelectMany(v => Texts(v).Select(t => t.Text)).ToList();
        Assert.Contains("First text", versionTexts);
        Assert.Contains("Second text", versionTexts);
        Assert.Equal(with.Versions.OrderBy(v => v.LastModifiedDateTime).ToList(), with.Versions);
    }

    [Fact]
    public void ImagesAndAttachments_CarryTheirData()
    {
        var section = SectionAt("onenote.rs", "Notebook created on OneNote App", "OneNote_RecycleBin", "OneNote_DeletedPages.one");
        var page = section.Pages.First(p => p.Title == "Created on OneNote App");

        var images = page.Images.ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, i =>
        {
            var data = i.ReadData();
            Assert.NotNull(data);
            Assert.Equal(i.Data!.Length, data!.Length);
            Assert.StartsWith("image/", i.ContentType ?? OneNoteMediaSniff(data));
        });
        var file = Assert.Single(page.EmbeddedFiles);
        Assert.False(string.IsNullOrEmpty(file.FileName));
        Assert.Equal(FileDataLocation.Embedded, file.Data!.Location);
        Assert.True(file.ReadData()!.Length > 0);
    }

    private static string OneNoteMediaSniff(byte[] data) => data[0] == 0x89 ? "image/png" : data[0] == 0xFF ? "image/jpeg" : "unknown";

    [Fact]
    public void Ink_IsDecodedIntoStrokes()
    {
        var page = Assert.Single(Section("onenote.rs", "scaled_ink.one").Pages);
        var strokes = page.Descendants().OfType<OneNoteInk>().SelectMany(i => i.Strokes).ToList();

        Assert.Equal(2, strokes.Count);
        Assert.All(strokes, s => Assert.True(s.Points.Count > 100));
        Assert.Contains(strokes, s => s.Color?.ToHex() == "#E71225");
        var red = strokes.Single(s => s.Color?.ToHex() == "#E71225");
        float spread = red.Points.Max(p => p.Y) - red.Points.Min(p => p.Y);
        Assert.True(spread > 1000, "the path is delta-encoded; undecoded it would hardly move");
    }

    [Fact]
    public void Math_IsMarkedOnItsRuns()
    {
        var page = Assert.Single(Section("onenote.rs", "Math.one").Pages);
        Assert.Contains(Texts(page), t => t.Runs.Any(r => r.Style.IsMath));
    }

    [Fact]
    public void SectionLinkId_ComesFromTheSectionMetadata()
    {
        var section = Section("onenote.rs", "Linked Quick Notes.one");

        Assert.Equal(new Guid("7ea640c0-af2b-4eff-ab18-24e1d84c1bdc"), section.FileId);
        Assert.Equal(new Guid("75256889-9e75-4ec2-82ed-fc799557e1b9"), section.Id);

        var link = Texts(section.Pages[0]).SelectMany(t => t.Hyperlinks).Concat(section.Pages.SelectMany(p => Texts(p).SelectMany(t => t.Hyperlinks)))
            .First(h => h.Url.Contains("page-id=", StringComparison.Ordinal));
        Assert.True(OneNoteLinks.TryParse(link.Url, out var target));
        Assert.Equal(section.Id, target.SectionId);
        Assert.Contains(section.Pages, p => p.Id == target.PageId);
    }

    // --------------------------------------------------------------------- notebooks

    [Fact]
    public void NotebookDirectory_IsAssembledFromItsTableOfContents()
    {
        var notebook = OneNoteNotebookReader.ReadDirectory(Fixture("onenote.rs", "Notebook created on OneNote App"));

        Assert.Equal("Notebook created on OneNote App", notebook.DisplayName);
        Assert.Equal(new Guid("d890422d-763f-4156-bbd9-0760ed3342cd"), notebook.Id);
        Assert.Equal(OneStoreEncoding.Fsshttpb, notebook.Encoding);
        Assert.Equal(new[] { "Section" }, notebook.Sections.Select(s => s.DisplayName));
        Assert.Equal(new[] { "Section A", "Section D" }, notebook.SectionGroups.Select(g => g.DisplayName));
        var groupA = notebook.SectionGroups[0];
        Assert.Equal(new[] { "Section A1" }, groupA.Sections.Select(s => s.DisplayName));
        Assert.Equal("Section B", Assert.Single(groupA.SectionGroups).DisplayName);
        Assert.Equal("Section A/Section B/Section B1.one", groupA.SectionGroups[0].Sections[0].RelativePath);
        Assert.NotNull(notebook.RecycleBin);
        Assert.True(notebook.RecycleBin!.IsRecycleBin);
        Assert.Equal(6, notebook.AllPages().Count());
        Assert.Equal(new[] { "Section A note 2", "Section A note 1" }, groupA.Sections[0].Pages.Select(p => p.Title));
    }

    [Fact]
    public void Package_IsReadFromItsCabinet()
    {
        var notebook = OneNoteNotebookReader.ReadPackage(Convert.FromBase64String(CabinetReaderTests.OneNotePackageBase64), "c-note.onepkg");

        Assert.Equal("c-note", notebook.DisplayName);
        var section = Assert.Single(notebook.Sections);
        Assert.Equal("新分区 1", section.DisplayName);
        Assert.Equal(OneStoreEncoding.RevisionStore, section.Encoding);
        Assert.Equal("第一个项目", Assert.Single(section.Pages).Title);
        Assert.Equal(1u, section.Order);
    }

    // --------------------------------------------------------------------- extraction

    [Fact]
    public void Extractor_DetectsOneNoteByContentAndRendersEveryPage()
    {
        var bytes = Read("aspose", "Issue1Subpages.one");
        Assert.Equal(Mime.OneNoteMimeType, Mime.DetectMimeTypeFromBytes(bytes));
        Assert.Equal(Mime.OneNoteMimeType, Mime.DetectMimeType("notes.one", checkExists: false));

        var config = new ExtractionConfig { OutputFormat = OutputFormat.Markdown, RenderPagesInOutputFormat = true, SourceName = "Issue1Subpages.one" };
        var result = new Extractor().Extract(ExtractInput.FromBytes(bytes, Mime.OneNoteMimeType, "Issue1Subpages.one"), config);
        var doc = Assert.Single(result.Results);

        Assert.Empty(result.Errors);
        Assert.Equal(2, doc.Pages!.Count);
        Assert.Contains("# Page1", doc.Content);
        Assert.Contains("Content2", doc.Content);
        Assert.Contains("Content1", doc.Pages[0].FormattedContent);
        Assert.DoesNotContain("Content1", doc.Pages[1].Content);
        Assert.True(doc.Metadata.Additional.ContainsKey("onenote"));
        Assert.Contains("c56cdb17-3e8b-4962-887b-56db7ad782c6", doc.Metadata.Additional["onenote"].GetRawText());
    }

    [Fact]
    public void Extractor_ReadsAPackageAsOneDocument()
    {
        var bytes = Convert.FromBase64String(CabinetReaderTests.OneNotePackageBase64);
        Assert.Equal(Mime.CabinetMimeType, Mime.DetectMimeTypeFromBytes(bytes));

        var result = new Extractor().Extract(ExtractInput.FromBytes(bytes, Mime.CabinetMimeType, "c-note.onepkg"), new ExtractionConfig());
        var doc = Assert.Single(result.Results);

        Assert.Contains("第一个项目", doc.Content);
        Assert.Equal(1, doc.Pages?.Count ?? 1);
    }

    [Fact]
    public void Extractor_ExtractsAttachmentsAsChildren()
    {
        var bytes = Read("onenote.rs", "Notebook created on OneNote App", "OneNote_RecycleBin", "OneNote_DeletedPages.one");
        var result = new Extractor().Extract(ExtractInput.FromBytes(bytes, Mime.OneNoteMimeType, "OneNote_DeletedPages.one"), new ExtractionConfig());
        var doc = Assert.Single(result.Results);

        Assert.Contains("[Attachment:", doc.Content);
        Assert.NotNull(doc.Children);
        Assert.NotEmpty(doc.Children!);
    }

    // ---------------------------------------------------------------------- page HTML

    [Fact]
    public void PageHtml_FollowsTheGraphOutputShape()
    {
        var page = Assert.Single(Section("aspose", "FormattedRichText.one").Pages);
        string html = OneNoteHtmlWriter.Write(page);

        Assert.Contains("<title>One hyperlink</title>", html);
        Assert.Contains("data-absolute-enabled=\"true\"", html);
        Assert.Matches(new Regex("<div id=\"div:\\{[0-9a-f-]{36}\\}\\{\\d+\\}\" data-id=\"_default\" style=\"position:absolute;left:\\d+px;top:\\d+px;width:\\d+px\">"), html);
        Assert.Matches(new Regex("<p id=\"p:\\{[0-9a-f-]{36}\\}\\{\\d+\\}\""), html);
        Assert.Contains("<a href=\"www.google.com\">hyperlink</a>", html);
        Assert.Contains("font-weight:bold", html);
    }

    [Fact]
    public void PageHtml_EmbedsImagesAndDeclaresAttachments()
    {
        var section = SectionAt("onenote.rs", "Notebook created on OneNote App", "OneNote_RecycleBin", "OneNote_DeletedPages.one");
        string html = OneNoteHtmlWriter.Write(section.Pages.First(p => p.Title == "Created on OneNote App"));

        Assert.Equal(2, Regex.Matches(html, "<img [^>]*src=\"data:image/").Count);
        Assert.Matches(new Regex("<object [^>]*data-attachment=\"[^\"]+\""), html);

        string referenced = OneNoteHtmlWriter.Write(section.Pages.First(p => p.Title == "Created on OneNote App"), new OneNoteHtmlOptions { ResourceUrl = (e, d) => "https://blob/" + e.ObjectId.Guid });
        Assert.DoesNotContain("data:image/", referenced);
        Assert.Contains("data-fullres-src=\"https://blob/", referenced);
        Assert.Matches(new Regex("<object [^>]*data=\"https://blob/"), referenced);
    }

    // -------------------------------------------------------------------------- links

    [Fact]
    public void Links_RoundTripTheGraphsBusinessExample()
    {
        const string client = "onenote:https://contoso-my.sharepoint.com/personal/john_contoso_com/Documents/Notitieblokken/My%20OneNote/Test.one#Page%20B&section-id=94cacaca-d6b5-428d-b967-d3cf01b95c28&page-id=46a1b220-7ffd-4512-a571-55322097c08d&end";
        const string web = "https://contoso-my.sharepoint.com/personal/john_contoso_com/Documents/Notitieblokken/My%20OneNote?wd=target%28Test.one%7C94cacaca-d6b5-428d-b967-d3cf01b95c28%2FPage%20B%7C46a1b220-7ffd-4512-a571-55322097c08d%2F%29";
        var section = new Guid("94cacaca-d6b5-428d-b967-d3cf01b95c28");
        var page = new Guid("46a1b220-7ffd-4512-a571-55322097c08d");

        Assert.True(OneNoteLinks.TryParse(client, out var c));
        Assert.Equal(section, c.SectionId);
        Assert.Equal(page, c.PageId);
        Assert.Equal("Page B", c.PageTitle);
        Assert.EndsWith("/My OneNote/Test.one", c.Section);

        Assert.True(OneNoteLinks.TryParse(web, out var w));
        Assert.Equal(section, w.SectionId);
        Assert.Equal(page, w.PageId);
        Assert.Equal("Page B", w.PageTitle);
        Assert.Equal("Test.one", w.Section);

        string built = OneNoteLinks.WebUrl("https://contoso-my.sharepoint.com/personal/john_contoso_com/Documents/Notitieblokken/My%20OneNote", "Test.one", section, "Page B", page);
        Assert.Equal(web, built);

        string builtClient = OneNoteLinks.ClientUrl("https://contoso-my.sharepoint.com/personal/john_contoso_com/Documents/Notitieblokken/My OneNote/Test.one", section, "Page B", page);
        Assert.True(OneNoteLinks.TryParse(builtClient, out var bc));
        Assert.Equal(c with { Section = bc.Section }, bc);
        Assert.Contains("section-id={94CACACA-D6B5-428D-B967-D3CF01B95C28}", builtClient);
    }

    [Fact]
    public void Links_EscapeSlashesInPageTitles()
    {
        const string escaped = "https://onedrive.live.com/redir.aspx?cid=587d2b9129361ef3&page=edit&resid=587D2B9129361EF3!755716&parId=587D2B9129361EF3!132&wd=target%28Test.one%7C19268cf1-a68a-4e6a-80d5-b3aba21a9b26%2FTest%20-%2026%5C%2F10%5C%2F%7C5926d327-a56b-43a0-a60e-ff4c453bd18c%2F%29";

        Assert.True(OneNoteLinks.TryParse(escaped, out var target));
        Assert.Equal("Test - 26/10/", target.PageTitle);
        Assert.Equal(new Guid("5926d327-a56b-43a0-a60e-ff4c453bd18c"), target.PageId);
        Assert.Equal("target(Test.one|19268cf1-a68a-4e6a-80d5-b3aba21a9b26/Test - 26\\/10\\/|5926d327-a56b-43a0-a60e-ff4c453bd18c/)",
            OneNoteLinks.WdTarget("Test.one", new Guid("19268cf1-a68a-4e6a-80d5-b3aba21a9b26"), "Test - 26/10/", new Guid("5926d327-a56b-43a0-a60e-ff4c453bd18c")));
    }

    [Fact]
    public void Links_SharePointDocAspxKeepsSourcedocAndSectionGroups()
    {
        const string notebook = "https://contoso.sharepoint.com/sites/team/_layouts/15/Doc.aspx?sourcedoc=%7Bcbce2e75-17b2-4e70-b422-60d034fcd4af%7D&file=Notes";
        string url = OneNoteLinks.WebUrl(notebook, "Knowledge Base/Product.one", new Guid("cfbcc7fb-4b00-4c43-9e29-70bdedd83b98"), "Backlog Refinement", new Guid("4ef1b64b-327d-4628-823a-0d1fc5fce6ea"));

        Assert.StartsWith("https://contoso.sharepoint.com/sites/team/_layouts/15/Doc.aspx?sourcedoc=%7Bcbce2e75-17b2-4e70-b422-60d034fcd4af%7D&action=edit&wd=target%28Knowledge%20Base%2FProduct.one%7C", url);
        Assert.True(OneNoteLinks.TryParse(url, out var target));
        Assert.Equal(new Guid("cbce2e75-17b2-4e70-b422-60d034fcd4af"), target.SourceDoc);
        Assert.Equal("Knowledge Base/Product.one", target.Section);
        Assert.Equal("Backlog Refinement", target.PageTitle);
    }

    [Fact]
    public void GraphIds_AreSplitIntoTheirParts()
    {
        Assert.True(GraphOneNoteId.TryParse("1-a26aaec43ed348bd82edf4eb44e73d6c!68-3eb21088-b613-4698-98df-92a7d34e0678", out var page));
        Assert.True(page.IsPage);
        Assert.Equal(1, page.Prefix);
        Assert.Equal(68, page.PageNumber);
        Assert.Equal("1-3eb21088-b613-4698-98df-92a7d34e0678", page.ParentSectionId);

        Assert.True(GraphOneNoteId.TryParse("1-3eb21088-b613-4698-98df-92a7d34e0678", out var section));
        Assert.False(section.IsPage);
        Assert.Equal(new Guid("3eb21088-b613-4698-98df-92a7d34e0678"), section.Guid);

        Assert.True(GraphOneNoteId.TryParse("0-22e6d1d60c0807e31875c40f4d3efc68!1-587D2B9129361EF3!755736", out var personal));
        Assert.Equal("0-587D2B9129361EF3!755736", personal.ParentSectionId);
    }
}

// Lowers the OneNote content model into the pipeline's InternalDocument, so a section, a table
// of contents or a package renders through the same plain / markdown / HTML / JSON renderers as
// every other format. One OneNote page is one page of the result.

using System.Text;
using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Types;

namespace XRay.Content.OneNote;

/// <summary>Builds an <see cref="InternalDocument"/> from OneNote content.</summary>
public static class OneNoteDocumentBuilder
{
    /// <summary>Lowers the sections given, in order, numbering their pages consecutively.</summary>
    public static InternalDocument Build(IEnumerable<OneNoteSection> sections, string mimeType, ExtractionConfig config, OneNoteNotebook? notebook = null)
    {
        var b = new InternalDocumentBuilder("onenote");
        b.SetMimeType(mimeType);
        var state = new State(b, config);
        var sectionList = sections.ToList();
        bool multiple = sectionList.Count > 1 || notebook is not null;

        foreach (var section in sectionList)
        {
            foreach (var w in section.Warnings) b.AddWarning(new ProcessingWarning { Source = "onenote", Message = $"{section.DisplayName}: {w}" });
            if (section.IsPasswordProtected)
                b.AddWarning(new ProcessingWarning { Source = "onenote", Message = $"Section '{section.DisplayName}' is password protected; its pages are encrypted and were not read." });
            foreach (var page in section.Pages) state.PushPage(page, multiple ? section : null);
        }
        if (notebook is not null)
            foreach (var w in notebook.Warnings) b.AddWarning(new ProcessingWarning { Source = "onenote", Message = w });

        var doc = b.Build();
        var first = sectionList.SelectMany(s => s.Pages).FirstOrDefault();
        var metadata = new Metadata
        {
            Title = notebook?.DisplayName ?? (sectionList.Count == 1 ? sectionList[0].DisplayName : null) ?? first?.Title,
            CreatedAt = (sectionList.Select(s => s.CreatedDateTime).Where(t => t is not null).Min() ?? first?.CreatedDateTime)?.ToString("o"),
            ModifiedAt = sectionList.SelectMany(s => s.Pages).Select(p => p.LastModifiedDateTime).Where(t => t is not null).Max()?.ToString("o"),
            Authors = sectionList.SelectMany(s => s.Pages).Select(p => p.Author).Where(a => !string.IsNullOrEmpty(a)).Distinct().Select(a => a!).ToList() is { Count: > 0 } authors ? authors : null,
            Pages = new PageStructure { TotalCount = (uint)state.PageCount, UnitType = PageUnitType.Page },
        };
        metadata.Additional["onenote"] = JsonSerializer.SerializeToElement(OneNoteSummary.Describe(sectionList, notebook), OneNoteSummary.JsonOptions);
        doc.Metadata = metadata;
        foreach (var child in state.Children) (doc.Children ??= new()).Add(child);
        return doc;
    }

    private sealed class State
    {
        private readonly InternalDocumentBuilder _b;
        private readonly ExtractionConfig _config;
        private uint _page;
        private uint _imageIndex;

        public List<ArchiveEntry> Children { get; } = new();
        public int PageCount => (int)_page;

        [ThreadStatic] private static int s_depth;
        private const int MaxAttachmentDepth = 3;

        public State(InternalDocumentBuilder b, ExtractionConfig config)
        {
            _b = b;
            _config = config;
        }

        public void PushPage(OneNotePage page, OneNoteSection? section)
        {
            uint p = ++_page;
            uint title = _b.PushTitle(string.IsNullOrWhiteSpace(page.Title) ? "Untitled page" : page.Title, p, null);
            var attrs = new Dictionary<string, string>
            {
                ["onenote_page_id"] = page.Id.ToString("B"),
                ["onenote_page_level"] = page.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (section is not null) attrs["onenote_section"] = section.DisplayName;
            _b.SetAttributes(title, attrs);
            _b.SetAnchor(title, "page-" + page.Id.ToString("D"));

            var dateLine = string.Join(" ", new[] { page.TitleBlock?.Date, page.TitleBlock?.Time }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (dateLine.Length > 0) _b.PushParagraph(dateLine, new(), p, null);

            foreach (var element in page.Content) PushElement(element, p);
            if (page.InkRecognition is { } rec && rec.Text.Length > 0) _b.PushParagraph(rec.Text, new(), p, null);
        }

        private void PushElement(OneNoteElement element, uint p)
        {
            switch (element)
            {
                case OneNoteOutline outline:
                    PushOutlineChildren(outline.Children, p);
                    break;
                case OneNoteOutlineElement oe:
                    PushOutlineChildren(new[] { oe }, p);
                    break;
                case OneNoteRichText text:
                    PushText(text, p, null);
                    break;
                case OneNoteTable table:
                    PushTable(table, p);
                    break;
                case OneNoteImage image:
                    PushImage(image, p);
                    break;
                case OneNoteEmbeddedFile file:
                    PushFile(file, p);
                    break;
                case OneNoteEmbeddedMedia media when media.Url is { Length: > 0 } url:
                {
                    uint i = _b.PushParagraph(url, new() { Link(0, Utf8(url, url.Length), url) }, p, null);
                    _b.PushUri(new ExtractedUri { Url = url, Kind = UriKind.Hyperlink, Page = p });
                    _ = i;
                    break;
                }
                case OneNoteInk ink when ink.AllStrokes().Any():
                    break;
            }
        }

        private void PushOutlineChildren(IReadOnlyList<OneNoteOutlineElement> children, uint p)
        {
            int i = 0;
            while (i < children.Count)
            {
                var oe = children[i];
                if (oe.List is not null && oe.Content is OneNoteRichText)
                {
                    bool ordered = oe.List.IsNumbered;
                    _b.PushList(ordered);
                    while (i < children.Count && children[i].List is { } marker && marker.IsNumbered == ordered && children[i].Content is OneNoteRichText item)
                    {
                        PushText(item, p, marker);
                        if (children[i].Children.Count > 0) PushOutlineChildren(children[i].Children, p);
                        i++;
                    }
                    _b.EndList();
                    continue;
                }
                if (oe.Content is not null) PushElement(oe.Content, p);
                if (oe.Children.Count > 0) PushOutlineChildren(oe.Children, p);
                i++;
            }
        }

        private void PushText(OneNoteRichText text, uint p, OneNoteListMarker? marker)
        {
            if (text.IsTitleText || text.IsTitleDate || text.IsTitleTime) return;
            string body = text.Text.TrimEnd();
            string prefix = TagPrefix(text.NoteTags);
            if (body.Length == 0 && prefix.Length == 0 && text.InlineInk.Count == 0) return;
            string content = prefix + body;
            var annotations = new List<TextAnnotation>();
            foreach (var run in text.Runs)
            {
                int start = run.Start + prefix.Length;
                int end = Math.Min(start + run.Length, content.Length);
                if (end <= start) continue;
                start = Utf8(content, start);
                end = Utf8(content, end);
                if (run.Style.Bold) annotations.Add(Ann(start, end, AnnotationKind.Tag.Bold));
                if (run.Style.Italic) annotations.Add(Ann(start, end, AnnotationKind.Tag.Italic));
                if (run.Style.Underline && run.Hyperlink is null) annotations.Add(Ann(start, end, AnnotationKind.Tag.Underline));
                if (run.Style.Strikethrough) annotations.Add(Ann(start, end, AnnotationKind.Tag.Strikethrough));
                if (run.Style.Superscript) annotations.Add(Ann(start, end, AnnotationKind.Tag.Superscript));
                if (run.Style.Subscript) annotations.Add(Ann(start, end, AnnotationKind.Tag.Subscript));
                if (run.Style.Highlight is { IsAutomatic: false }) annotations.Add(Ann(start, end, AnnotationKind.Tag.Highlight));
            }
            foreach (var link in text.Hyperlinks)
            {
                int start = link.Start + prefix.Length;
                int end = Math.Min(start + link.Length, content.Length);
                if (end > start) annotations.Add(Link(Utf8(content, start), Utf8(content, end), link.Url));
                _b.PushUri(new ExtractedUri { Url = link.Url, Label = link.Text, Kind = link.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? UriKind.Email : UriKind.Hyperlink, Page = p });
            }

            uint index;
            if (marker is not null)
            {
                index = _b.PushListItem(content, marker.IsNumbered, annotations, p, null);
                if (marker.IsNumbered && marker.Text != $"{marker.Number}.") _b.SetListItemSourceLabel(index, marker.Text);
            }
            else if (text.HeadingLevel is { } h)
            {
                index = _b.PushHeading((byte)Math.Min(6, h + 1), content, p, null);
            }
            else if (text.ParagraphStyle?.StyleId == "code")
            {
                index = _b.PushCode(content, null, p, null);
            }
            else
            {
                index = _b.PushParagraph(content, annotations, p, null);
            }
            if (text.NoteTags.Count > 0)
            {
                _b.SetAttributes(index, new Dictionary<string, string>
                {
                    ["onenote_tags"] = string.Join(";", text.NoteTags.Select(t => t.GraphDataTag ?? t.Label ?? $"shape-{t.Shape}")),
                });
            }
        }

        private static string TagPrefix(List<OneNoteNoteTag> tags)
        {
            var checkable = tags.FirstOrDefault(t => t.IsCheckable || t.IsTask);
            if (checkable is not null) return checkable.IsCompleted ? "[x] " : "[ ] ";
            return "";
        }

        private void PushTable(OneNoteTable table, uint p)
        {
            bool layout = table.Rows.Count == 1 && table.Rows[0].Cells.Count == 1
                || table.Rows.SelectMany(r => r.Cells).Any(c => c.SelfAndDescendants().Skip(1).Any(e => e is OneNoteTable));
            if (layout)
            {
                foreach (var row in table.Rows)
                    foreach (var cell in row.Cells)
                        PushOutlineChildren(cell.Children, p);
                return;
            }

            var cells = new List<List<string>>();
            foreach (var row in table.Rows)
                cells.Add(row.Cells.Select(CellText).ToList());
            if (cells.Count == 0) return;
            int width = cells.Max(r => r.Count);
            foreach (var r in cells)
                while (r.Count < width) r.Add("");
            _b.PushTableFromCells(cells, p, null);

            foreach (var row in table.Rows)
                foreach (var cell in row.Cells)
                    foreach (var nested in cell.SelfAndDescendants())
                        if (nested is OneNoteImage image) PushImage(image, p);
                        else if (nested is OneNoteEmbeddedFile file) PushFile(file, p);
        }

        private static string CellText(OneNoteTableCell cell)
        {
            var parts = new List<string>();
            foreach (var e in cell.SelfAndDescendants())
            {
                if (e is OneNoteRichText t && t.Text.Trim().Length > 0) parts.Add(TagPrefix(t.NoteTags) + t.Text.Trim());
            }
            return string.Join("\n", parts);
        }

        private void PushImage(OneNoteImage image, uint p)
        {
            string? description = image.AltText ?? image.FileName;
            var extracted = new ExtractedImage
            {
                Format = (image.Extension ?? ".png").TrimStart('.').ToLowerInvariant(),
                ImageIndex = _imageIndex++,
                PageNumber = p,
                Description = description,
                SourcePath = image.FileName,
            };
            if (_config.NeedsImageData() && image.Data?.ReadAll() is { } data) extracted.Data = data;
            _b.PushImage(description, extracted, p, null);
            if (image.OcrText is { Length: > 0 } ocr) _b.PushParagraph(ocr.Trim(), new(), p, null);
            if (image.Hyperlink is { Length: > 0 } link) _b.PushUri(new ExtractedUri { Url = link, Kind = UriKind.Hyperlink, Page = p });
        }

        private void PushFile(OneNoteEmbeddedFile file, uint p)
        {
            string name = file.FileName ?? "attachment" + (file.Extension ?? "");
            _b.PushParagraph($"[Attachment: {name}]", new(), p, null);
            if (!_config.Options.OneNoteExtractAttachments || s_depth >= MaxAttachmentDepth) return;
            if (file.Data is not { Location: XRay.Content.OneNote.Store.FileDataLocation.Embedded } data) return;
            var bytes = data.ReadAll();
            if (bytes is null || bytes.Length == 0) return;
            string? mime = Mime.DetectMimeTypeFromBytes(bytes);
            if (mime is null || mime == Mime.OctetStream) mime = Mime.DetectMimeType(name, checkExists: false);
            if (mime is null || mime == Mime.OctetStream) return;
            s_depth++;
            try
            {
                var result = new Extractor().Extract(ExtractInput.FromBytes(bytes, mime, name), _config);
                if (result.Results.FirstOrDefault() is { } child) Children.Add(new ArchiveEntry { Path = name, MimeType = mime, Result = child });
                foreach (var error in result.Errors)
                    _b.AddWarning(new ProcessingWarning { Source = "onenote_attachment", Message = $"Failed to extract '{name}': {error.Message}" });
            }
            catch (Exception ex)
            {
                _b.AddWarning(new ProcessingWarning { Source = "onenote_attachment", Message = $"Failed to extract '{name}': {ex.Message}" });
            }
            finally
            {
                s_depth--;
            }
        }

        /// <summary>Annotation offsets are UTF-8 byte offsets, as the renderers expect.</summary>
        private static int Utf8(string s, int charIndex) => System.Text.Encoding.UTF8.GetByteCount(s.AsSpan(0, Math.Min(charIndex, s.Length)));

        private static TextAnnotation Ann(int start, int end, AnnotationKind.Tag tag) =>
            new() { Start = (uint)start, End = (uint)end, Kind = new AnnotationKind { Which = tag } };

        private static TextAnnotation Link(int start, int end, string url) =>
            new() { Start = (uint)start, End = (uint)end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = url } };
    }
}

/// <summary>A JSON-friendly outline of OneNote content: what <c>Metadata.Additional["onenote"]</c>
/// carries, so a caller can line pages up with the Graph without re-reading the file.</summary>
public static class OneNoteSummary
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record PageInfo(
        string Id,
        string Title,
        int Level,
        int Order,
        int PageNumber,
        DateTimeOffset? CreatedDateTime,
        DateTimeOffset? LastModifiedDateTime,
        string? Author,
        string? LastModifiedBy,
        bool IsDeleted,
        int ConflictPages,
        int Versions);

    public sealed record SectionInfo(
        string Id,
        string FileId,
        string DisplayName,
        string? FileName,
        string? RelativePath,
        string? NotebookFileId,
        string? Color,
        DateTimeOffset? CreatedDateTime,
        string Encoding,
        bool IsPasswordProtected,
        List<PageInfo> Pages);

    public sealed record NotebookInfo(string? Id, string DisplayName, string? Color, List<SectionInfo> Sections);

    public static NotebookInfo Describe(IReadOnlyList<OneNoteSection> sections, OneNoteNotebook? notebook)
    {
        int pageNumber = 0;
        var infos = new List<SectionInfo>();
        foreach (var s in sections)
        {
            var pages = s.Pages.Select(p => new PageInfo(
                p.Id.ToString("D"),
                p.Title,
                p.Level,
                p.Order,
                ++pageNumber,
                p.CreatedDateTime,
                p.LastModifiedDateTime,
                p.Author,
                p.LastModifiedBy,
                p.IsDeleted,
                p.ConflictPages.Count,
                p.Versions.Count)).ToList();
            infos.Add(new SectionInfo(
                s.Id.ToString("D"),
                s.FileId.ToString("D"),
                s.DisplayName,
                s.FileName,
                s.RelativePath,
                s.NotebookFileId?.ToString("D"),
                s.Color?.ToHex(),
                s.CreatedDateTime,
                s.Encoding.ToString(),
                s.IsPasswordProtected,
                pages));
        }
        return new NotebookInfo(notebook?.Id?.ToString("D"), notebook?.DisplayName ?? "", notebook?.Color?.ToHex(), infos);
    }
}

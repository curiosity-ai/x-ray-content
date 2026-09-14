using System.Globalization;
using System.Text;
using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Core.Ocr;
using XRay.Content.Internal.Ooxml;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>
/// Word document extractor. Ports `extractors/docx.rs` `build_internal_document` over a
/// DOCX parsed by <see cref="DocxReader"/>: emits headings/paragraphs/lists/tables/images and
/// header/footer/footnote layers, plus core/app/custom metadata (<see cref="DocxMetadata"/>).
/// Math (OMML→LaTeX) is not converted.
/// </summary>
public sealed class DocxExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-word.document.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        "application/vnd.ms-word.template.macroEnabled.12",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        using var pkg = new OoxmlPackage(content);
        var doc = DocxReader.Parse(pkg);
        var internalDoc = BuildInternalDocument(doc);
        PopulateImages(doc, internalDoc);
        // Relationship targets are `word/`-relative ("media/image1.png"), which is the shape
        // `SourcePath` carries; the package wants the part name.
        OcrImageSource.AttachBytes(internalDoc, config, path =>
            pkg.ReadBytes(path.StartsWith('/') ? path[1..] : "word/" + path));
        internalDoc.MimeType = mimeType;
        internalDoc.Metadata = BuildMetadata(pkg);
        SetPageStructure(doc, internalDoc.Metadata);
        return internalDoc;
    }

    // ── build_internal_document (docx.rs) ──────────────────────────────────────
    private static InternalDocument BuildInternalDocument(DocxDocument doc)
    {
        var b = new InternalDocumentBuilder("docx");

        long? currentListNumId = null;
        bool currentListOrdered = false;
        long currentNesting = 0;
        long openListCount = 0;

        void CloseLists()
        {
            if (currentListNumId is not null)
            {
                for (long i = 0; i < openListCount; i++) b.EndList();
                currentListNumId = null;
                openListCount = 0;
            }
        }

        uint drawingIndex = 0;
        foreach (var el in doc.Elements)
        {
            switch (el.Kind)
            {
                case DocElementKind.Paragraph:
                {
                    var para = el.Paragraph!;
                    string text = CollectText(para.Runs);
                    var annotations = CollectRunAnnotations(para.Runs);
                    var mathFormulas = CollectMathFormulas(para.Runs);
                    if (text.Length == 0 && mathFormulas.Count == 0) { CloseLists(); continue; }

                    byte? headingLevel = DocxReader.ResolveHeadingLevel(para.StyleId);
                    bool isQuote = IsQuoteStyle(para.StyleId);

                    uint? elementIdx = null;
                    if (headingLevel is { } level)
                    {
                        // Headings do not emit standalone math formulas (matches Rust).
                        CloseLists();
                        string headingText = text.Length == 0 ? RunsToMarkdown(para.Runs) : text;
                        uint headingIdx = b.PushHeading(level, headingText, null, null);
                        if (annotations.Count > 0) b.SetAnnotations(headingIdx, annotations);
                        elementIdx = headingIdx;
                    }
                    else if (isQuote)
                    {
                        CloseLists();
                        foreach (var f in mathFormulas) b.PushFormula(f, null, null);
                        if (text.Length != 0)
                        {
                            b.PushQuoteStart();
                            elementIdx = b.PushParagraph(text, annotations, null, null);
                            b.PushQuoteEnd();
                        }
                    }
                    else if (para.NumId is { } nid)
                    {
                        foreach (var f in mathFormulas) b.PushFormula(f, null, null);
                        if (text.Length != 0)
                        {
                            long nlvl = para.NumLevel ?? 0;
                            bool isOrdered = doc.NumberingOrdered.TryGetValue((nid, nlvl), out var ord) && ord;
                            if (currentListNumId != nid)
                            {
                                if (currentListNumId is not null)
                                    for (long i = 0; i < openListCount; i++) b.EndList();
                                b.PushList(isOrdered);
                                currentListNumId = nid;
                                currentListOrdered = isOrdered;
                                currentNesting = nlvl;
                                openListCount = 1;
                            }
                            else if (nlvl > currentNesting)
                            {
                                for (long i = 0; i < nlvl - currentNesting; i++) { b.PushList(isOrdered); openListCount++; }
                                currentNesting = nlvl;
                            }
                            else if (nlvl < currentNesting)
                            {
                                for (long i = 0; i < currentNesting - nlvl; i++) { b.EndList(); openListCount = Math.Max(0, openListCount - 1); }
                                currentNesting = nlvl;
                            }
                            elementIdx = b.PushListItem(text, currentListOrdered, annotations, null, null);
                        }
                    }
                    else
                    {
                        CloseLists();
                        foreach (var f in mathFormulas) b.PushFormula(f, null, null);
                        if (text.Length != 0) elementIdx = b.PushParagraph(text, annotations, null, null);
                    }

                    if (elementIdx is { } sourceIdx)
                    {
                        foreach (var run in para.Runs)
                        {
                            if (run.MathLatex is not null || run.Text.Length == 0) continue;
                            if (run.HyperlinkUrl is not { } url) continue;
                            if (url.StartsWith('#'))
                                b.PushRelationship(
                                    sourceIdx,
                                    RelationshipTarget.FromKey(url.TrimStart('#')),
                                    RelationshipKind.InternalLink);
                            b.PushUri(new ExtractedUri { Url = url, Label = run.Text, Kind = UriKind.Hyperlink });
                        }

                        // Footnote and endnote markers were written into the run text as `[^N]`
                        // when the reference was walked; the definitions live in a separate part,
                        // so the reference elements are recovered by scanning the assembled text.
                        foreach (var refId in ScanMarkers(text, "[^"))
                        {
                            if (refId.Length == 0 || !refId.All(char.IsAsciiDigit)) continue;
                            b.PushFootnoteRef(refId, $"fn{refId}", null);
                        }

                        // Comment markers (`[cmt:N]`) work the same way, but get their own element
                        // kind so a consumer can tell a reviewer comment from an authored footnote.
                        foreach (var commentId in ScanMarkers(text, "[cmt:"))
                        {
                            if (commentId.Length == 0) continue;
                            b.PushCommentRef(commentId, $"cmt{commentId}", null);
                        }
                    }
                    break;
                }
                case DocElementKind.Table:
                {
                    CloseLists();
                    var table = el.Table!;
                    if (!string.IsNullOrEmpty(table.Caption))
                        b.PushParagraph(table.Caption!, new(), null, null);
                    var cells = BuildTableCells(table);
                    if (cells.Count > 0) b.PushTableFromCells(cells, null, null);
                    break;
                }
                case DocElementKind.Drawing:
                {
                    var draw = el.Drawing!;
                    uint idx = drawingIndex++;
                    // A text box contributes one paragraph carrying all of its lines: its inner
                    // `w:p` elements are layout, not document structure, so they must not become
                    // headings or list items of their own.
                    if (draw.TextBoxContent is { } boxText && boxText.Trim().Length != 0)
                    {
                        CloseLists();
                        b.PushParagraph(boxText, new(), null, null);
                    }
                    if (draw.ImageRid is null) break; // textbox shapes etc.
                    CloseLists();
                    var elem = InternalElement.TextElement(ElementKind.Image(idx), draw.Description ?? "", 0);
                    b.PushElement(elem);
                    break;
                }
                case DocElementKind.PageBreak:
                    break;
            }
        }
        CloseLists();

        // Headers / footers
        foreach (var hf in doc.Headers)
        {
            string text = string.Join("\n", hf.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0) { uint idx = b.PushParagraph(text, new(), null, null); b.SetLayer(idx, ContentLayer.Header); }
        }
        foreach (var hf in doc.Footers)
        {
            string text = string.Join("\n", hf.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0) { uint idx = b.PushParagraph(text, new(), null, null); b.SetLayer(idx, ContentLayer.Footer); }
        }

        // Footnotes + endnotes
        foreach (var note in doc.Footnotes.Concat(doc.Endnotes))
        {
            string text = string.Join(" ", note.Paragraphs.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length > 0)
            {
                uint idx = b.PushFootnoteDefinition(text, $"fn{note.Id}", null);
                b.SetLayer(idx, ContentLayer.Footnote);
            }
        }

        // A comment has the same shape as a footnote — a marker in the body and a definition
        // elsewhere — but a reader needs to tell a reviewer's remark from an authored note, so it
        // gets its own kind rather than sharing the footnote's.
        foreach (var comment in doc.Comments)
        {
            string text = string.Join(" ", comment.Paragraphs.Select(p => RunsToMarkdown(p.Runs)));
            if (text.Length == 0) continue;
            uint idx = b.PushCommentDefinition(text, $"cmt{comment.Id}", null);
            b.SetLayer(idx, ContentLayer.Footnote);
        }

        return b.Build();
    }

    /// <summary>Build one <see cref="ExtractedImage"/> per drawing (index = image_index) so the
    /// renderers can resolve alt text / source path. Mirrors the Rust extractor's placeholder images.</summary>
    private static void PopulateImages(DocxDocument doc, InternalDocument internalDoc)
    {
        var pageNumbers = DrawingPageNumbers(doc);
        for (int i = 0; i < doc.Drawings.Count; i++)
        {
            var d = doc.Drawings[i];
            string? sourcePath = d.ImageRid is not null && doc.ImageRelationships.TryGetValue(d.ImageRid, out var t) ? t : null;
            string format = sourcePath is not null && sourcePath.Contains('.')
                ? sourcePath[(sourcePath.LastIndexOf('.') + 1)..].ToLowerInvariant()
                : "png";
            internalDoc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i,
                Description = d.Description,
                Format = format,
                SourcePath = sourcePath,
                PageNumber = pageNumbers[i],
            });
        }
    }

    /// <summary>
    /// The 1-based page each drawing appears on, in drawing order.
    /// </summary>
    /// <remarks>
    /// Derived by walking the parsed element list, the way table page numbers are. Upstream used
    /// to search the rendered markdown for an <c>![alt](image_N)</c> placeholder, but every
    /// drawing renders to the same target, so the per-image key it asked for never existed and
    /// every image was reported as page 1 (xberg-io/xberg#1546). Walking the elements is also
    /// independent of whether placeholders were rendered at all.
    /// </remarks>
    private static uint[] DrawingPageNumbers(DocxDocument doc)
    {
        var pages = new uint[doc.Drawings.Count];
        Array.Fill(pages, 1u);
        uint currentPage = 1;
        int drawingIndex = 0;
        foreach (var el in doc.Elements)
        {
            switch (el.Kind)
            {
                case DocElementKind.PageBreak:
                    currentPage++;
                    break;
                case DocElementKind.Drawing:
                    if (drawingIndex < pages.Length) pages[drawingIndex] = currentPage;
                    drawingIndex++;
                    break;
            }
        }
        return pages;
    }

    // ── page structure (metadata.pages) via to_plain_text form-feed boundaries ──
    private static void SetPageStructure(DocxDocument doc, Metadata meta)
    {
        string text = BuildPlainText(doc);
        var bytes = Encoding.UTF8.GetBytes(text);
        var bounds = new List<(int Start, int End, uint Page)>();
        int start = 0; uint page = 1;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0c) { bounds.Add((start, i, page)); start = i + 1; page++; }
        }
        bounds.Add((start, bytes.Length, page));
        if (bounds.Count <= 1) return;

        meta.Pages = new PageStructure
        {
            TotalCount = (uint)bounds.Count,
            UnitType = PageUnitType.Page,
            Boundaries = bounds.Select(b => (object)new BoundaryDto(b.Start, b.End, b.Page)).ToList(),
            Pages = bounds.Select(b => (object)new PageInfoDto(b.Page)).ToList(),
        };
    }

    /// <summary>
    /// Ports <c>Paragraph::to_text</c>: run text with each math run replaced by its LaTeX. This is
    /// the text the page-boundary offsets are measured against, and unlike
    /// <see cref="CollectText"/> it counts equations — a document made mostly of formulas would
    /// otherwise report boundaries far short of its real length.
    /// </summary>
    private static string ParagraphPlainText(List<DocxRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs) sb.Append(run.MathLatex ?? run.Text);
        return sb.ToString();
    }

    private sealed record BoundaryDto(int ByteStart, int ByteEnd, uint PageNumber);

    /// <summary>Ports `Document::to_plain_text` (used for page-boundary computation).</summary>
    private static string BuildPlainText(DocxDocument doc)
    {
        var sb = new StringBuilder();
        void EnsureBlank()
        {
            if (sb.Length == 0) return;
            if (sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n') return;
            if (sb[^1] != '\n') sb.Append('\n');
            sb.Append('\n');
        }

        foreach (var el in doc.Elements)
        {
            switch (el.Kind)
            {
                case DocElementKind.Paragraph:
                    var t = ParagraphPlainText(el.Paragraph!.Runs);
                    if (t.Length > 0) { EnsureBlank(); sb.Append(t); }
                    break;
                case DocElementKind.Table:
                    EnsureBlank();
                    if (!string.IsNullOrEmpty(el.Table!.Caption)) { sb.Append(el.Table.Caption); sb.Append("\n\n"); }
                    sb.Append(TablePlainText(el.Table));
                    break;
                case DocElementKind.Drawing:
                    var d = el.Drawing!.Description;
                    if (!string.IsNullOrEmpty(d)) { EnsureBlank(); sb.Append(d); }
                    break;
                case DocElementKind.PageBreak:
                    sb.Append('\f');
                    break;
            }
        }

        AppendNotes(sb, doc.Footnotes);
        AppendNotes(sb, doc.Endnotes);
        return sb.ToString().Trim();
    }

    private static void AppendNotes(StringBuilder sb, List<DocxNote> notes)
    {
        if (notes.Count == 0) return;
        sb.Append("\n\n");
        foreach (var note in notes)
        {
            string text = string.Join(" ", note.Paragraphs.Select(p => ParagraphPlainText(p.Runs)).Where(s => s.Length > 0));
            if (text.Length > 0) sb.Append($"{note.Id}: {text}\n");
        }
    }

    /// <summary>Ports `Table::to_plain_text` → tab-separated cells, over the shared
    /// <see cref="ToCellGrid"/>.</summary>
    private static string TablePlainText(DocxTable table)
    {
        var cells = ToCellGrid(table, p => ParagraphPlainText(p.Runs));
        var sb = new StringBuilder();
        foreach (var row in cells)
        {
            for (int i = 0; i < row.Count; i++) { if (i > 0) sb.Append('\t'); sb.Append(row[i]); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsQuoteStyle(string? style)
    {
        if (style is null) return false;
        var lower = style.ToLowerInvariant();
        return lower is "quote" or "blockquote" or "intenseq" or "intensequote" || lower.Contains("quote");
    }

    /// <summary>
    /// Yield the payload of every <c>{prefix}…]</c> marker in <paramref name="text"/>, scanning
    /// left to right and resuming after each closing bracket. An unterminated marker ends the scan.
    /// </summary>
    private static IEnumerable<string> ScanMarkers(string text, string prefix)
    {
        int searchStart = 0;
        while (searchStart <= text.Length)
        {
            int start = text.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (start < 0) yield break;
            int end = text.IndexOf(']', start);
            if (end < 0) yield break;
            yield return text[(start + prefix.Length)..end];
            searchStart = end + 1;
        }
    }

    /// <summary>Plain text: concatenate non-math run text.</summary>
    private static string CollectText(List<DocxRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.MathLatex is not null) continue;
            sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Collect byte-offset-based formatting annotations for the plain text produced by
    /// <see cref="CollectText"/>. Ports Rust <c>collect_run_annotations</c>; offsets are UTF-8
    /// byte offsets, matching the Rust <c>String::len()</c> the renderers expect.
    /// </summary>
    private static List<TextAnnotation> CollectRunAnnotations(List<DocxRun> runs)
    {
        var annotations = new List<TextAnnotation>();
        uint offset = 0;

        foreach (var run in runs)
        {
            if (run.MathLatex is not null) continue;
            if (run.Text.Length == 0) continue;

            uint start = offset;
            offset += (uint)Encoding.UTF8.GetByteCount(run.Text);
            uint end = offset;

            void Add(AnnotationKind kind) =>
                annotations.Add(new TextAnnotation { Start = start, End = end, Kind = kind });

            if (run.Bold) Add(new AnnotationKind { Which = AnnotationKind.Tag.Bold });
            if (run.Italic) Add(new AnnotationKind { Which = AnnotationKind.Tag.Italic });
            if (run.Underline) Add(new AnnotationKind { Which = AnnotationKind.Tag.Underline });
            if (run.Strike) Add(new AnnotationKind { Which = AnnotationKind.Tag.Strikethrough });
            if (run.Subscript) Add(new AnnotationKind { Which = AnnotationKind.Tag.Subscript });
            if (run.Superscript) Add(new AnnotationKind { Which = AnnotationKind.Tag.Superscript });
            if (run.FontSize is { } sz)
            {
                double pts = sz / 2.0;
                string value = pts == Math.Floor(pts)
                    ? $"{(uint)pts}pt"
                    : pts.ToString("0.0", CultureInfo.InvariantCulture) + "pt";
                Add(new AnnotationKind { Which = AnnotationKind.Tag.FontSize, Value = value });
            }
            if (run.FontColor is { } color)
                Add(new AnnotationKind { Which = AnnotationKind.Tag.Color, Value = "#" + color });
            if (run.Highlight) Add(new AnnotationKind { Which = AnnotationKind.Tag.Highlight });
            if (run.HyperlinkUrl is { } url)
                Add(new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = url, Title = null });
        }

        MergeAdjacentAnnotations(annotations);
        return annotations;
    }

    /// <summary>
    /// Merge adjacent or overlapping annotations of the same kind. Consecutive runs with the
    /// same formatting each produce their own annotation; without merging the markdown renderer
    /// would close and immediately reopen markers (<c>**a****b**</c> instead of <c>**ab**</c>).
    /// </summary>
    private static void MergeAdjacentAnnotations(List<TextAnnotation> annotations)
    {
        if (annotations.Count < 2) return;

        static int KindKey(AnnotationKind k) => k.Which switch
        {
            AnnotationKind.Tag.Bold => 0,
            AnnotationKind.Tag.Italic => 1,
            AnnotationKind.Tag.Underline => 2,
            AnnotationKind.Tag.Strikethrough => 3,
            AnnotationKind.Tag.Subscript => 4,
            AnnotationKind.Tag.Superscript => 5,
            AnnotationKind.Tag.Highlight => 6,
            AnnotationKind.Tag.Code => 7,
            AnnotationKind.Tag.Link => 8,
            _ => 255,
        };

        // Simple kinds match by discriminant; links match only on identical url + title.
        static bool SameKindForMerge(AnnotationKind a, AnnotationKind b) => a.Which == b.Which && a.Which switch
        {
            AnnotationKind.Tag.Bold or AnnotationKind.Tag.Italic or AnnotationKind.Tag.Underline
                or AnnotationKind.Tag.Strikethrough or AnnotationKind.Tag.Subscript
                or AnnotationKind.Tag.Superscript or AnnotationKind.Tag.Highlight
                or AnnotationKind.Tag.Code => true,
            AnnotationKind.Tag.Link => a.Url == b.Url && a.Title == b.Title,
            _ => false,
        };

        static bool IsMergeable(AnnotationKind k) => KindKey(k) != 255;

        // Stable sort by (kind, start) so same-kind runs land next to each other in text order.
        var sorted = annotations
            .Select((a, i) => (a, i))
            .OrderBy(t => KindKey(t.a.Kind))
            .ThenBy(t => t.a.Start)
            .ThenBy(t => t.i)
            .Select(t => t.a)
            .ToList();

        var merged = new List<TextAnnotation>(sorted.Count);
        int p = 0;
        while (p < sorted.Count)
        {
            var ann = sorted[p];
            if (IsMergeable(ann.Kind))
            {
                int q = p + 1;
                while (q < sorted.Count && SameKindForMerge(sorted[q].Kind, ann.Kind) && sorted[q].Start <= ann.End)
                {
                    ann.End = Math.Max(ann.End, sorted[q].End);
                    q++;
                }
                merged.Add(ann);
                p = q;
            }
            else
            {
                merged.Add(ann);
                p++;
            }
        }

        annotations.Clear();
        annotations.AddRange(merged);
    }

    /// <summary>Non-empty LaTeX strings from math runs, emitted as standalone Formula nodes
    /// (matches Rust `collect_run_annotations` math_formulas).</summary>
    private static List<string> CollectMathFormulas(List<DocxRun> runs)
    {
        var list = new List<string>();
        foreach (var run in runs)
            if (run.MathLatex is { Length: > 0 } latex) list.Add(latex);
        return list;
    }

    // ── table cell grid (docx parser `Table::to_cell_grid`) ───────────────────
    /// <summary>
    /// Lay a DOCX table out as a rectangular grid, writing a cell that spans several grid
    /// columns (<c>w:gridSpan</c>) or rows (<c>w:vMerge</c>) <em>once</em>, at its origin, and
    /// leaving every column and row it covers blank.
    /// </summary>
    /// <remarks>
    /// Ports `Table::to_cell_grid` (xberg-io/xberg#1549). The grid used to clone a spanned
    /// cell's text into every column it covered, and a <c>w:vMerge</c> continuation then copied
    /// the row above down over all of them, so a cell merged across 4 columns and 3 rows came
    /// back 12 times — in the table's cells, its markdown and the document content alike.
    /// <paramref name="render"/> is the only difference between the markdown and plain-text
    /// grids, so both share this builder and cannot drift apart.
    /// </remarks>
    private static List<List<string>> ToCellGrid(DocxTable table, Func<DocxParagraph, string> render)
    {
        var cells = new List<List<string>>();
        foreach (var row in table.Rows)
        {
            var rowCells = new List<string>();
            foreach (var cell in row.Cells)
            {
                string text = cell.VMergeContinue
                    ? ""
                    : string.Join(" ", cell.Paragraphs.Select(render)).Trim();
                rowCells.Add(text);
                for (uint s = 1; s < cell.GridSpan; s++) rowCells.Add("");
            }
            cells.Add(rowCells);
        }
        return cells;
    }

    private static List<List<string>> BuildTableCells(DocxTable table) =>
        ToCellGrid(table, p => RunsToMarkdown(p.Runs));

    // ── runs_to_markdown (docx parser) ─────────────────────────────────────────
    private static string RunsToMarkdown(List<DocxRun> runs)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < runs.Count)
        {
            var r = runs[i];
            // Math runs (and empty runs) are emitted individually. Math renders as
            // `$$latex$$` (display) or `$latex$` (inline), matching Rust `Run::to_markdown`.
            if (r.MathLatex is not null)
            {
                sb.Append(r.MathDisplay ? $"$${r.MathLatex}$$" : $"${r.MathLatex}$");
                i++;
                continue;
            }
            int j = i + 1;
            while (j < runs.Count && runs[j].MathLatex is null && SameGroup(runs[j], r)) j++;
            bool allSameUS = true;
            for (int k = i; k < j; k++)
                if (runs[k].Underline != r.Underline || runs[k].Strike != r.Strike) { allSameUS = false; break; }

            string biOpen = r.Bold && r.Italic ? "***" : r.Bold ? "**" : r.Italic ? "*" : "";
            if (allSameUS)
            {
                var text = new StringBuilder();
                for (int k = i; k < j; k++) text.Append(runs[k].Text);
                if (r.HyperlinkUrl is not null) sb.Append('[');
                if (r.Underline) sb.Append("<u>");
                if (r.Strike) sb.Append("~~");
                sb.Append(biOpen).Append(text).Append(biOpen);
                if (r.Strike) sb.Append("~~");
                if (r.Underline) sb.Append("</u>");
                if (r.HyperlinkUrl is not null) sb.Append("](").Append(r.HyperlinkUrl).Append(')');
            }
            else
            {
                if (r.HyperlinkUrl is not null) sb.Append('[');
                sb.Append(biOpen);
                for (int k = i; k < j; k++)
                {
                    var run = runs[k];
                    if (run.Underline) sb.Append("<u>");
                    if (run.Strike) sb.Append("~~");
                    sb.Append(run.Text);
                    if (run.Strike) sb.Append("~~");
                    if (run.Underline) sb.Append("</u>");
                }
                sb.Append(biOpen);
                if (r.HyperlinkUrl is not null) sb.Append("](").Append(r.HyperlinkUrl).Append(')');
            }
            i = j;
        }
        return sb.ToString();
    }

    private static bool SameGroup(DocxRun a, DocxRun r) =>
        a.Bold == r.Bold && a.Italic == r.Italic && a.HyperlinkUrl == r.HyperlinkUrl;

    // ── metadata ───────────────────────────────────────────────────────────────
    private static Metadata BuildMetadata(OoxmlPackage pkg)
    {
        var core = OfficeMetadata.ExtractCore(pkg);
        var app = OfficeMetadata.ExtractApp(pkg);
        var custom = OfficeMetadata.ExtractCustom(pkg);

        var additional = new Dictionary<string, JsonElement>();
        void AddNum(string key, int? v) { if (v is not null) additional[key] = JsonNumber(v.Value); }
        void AddStr(string key, string? v) { if (v is not null) additional[key] = JsonString(v); }

        AddNum("page_count", app.Pages);
        AddNum("word_count", app.Words);
        AddNum("character_count", app.Characters);
        AddNum("line_count", app.Lines);
        AddNum("paragraph_count", app.Paragraphs);
        AddStr("template", app.Template);
        AddStr("company", app.Company);
        AddNum("total_editing_time_minutes", app.TotalTime);
        AddStr("application", app.Application);
        AddStr("revision", core.Revision);
        AddStr("category", core.Category);
        AddStr("content_status", core.ContentStatus);
        AddStr("description", core.Description);
        // #230: surface both the raw DocSecurity value and the decoded ECMA-376 flags so a
        // consumer can tell a read-only-recommended or password-protected document apart
        // without knowing the bit layout.
        if (app.DocSecurity is { } docSecurity)
        {
            additional[OfficeMetadata.DocSecurityKey] = JsonNumber(docSecurity);
            foreach (var (key, value) in OfficeMetadata.DecodeDocSecurityFlags(docSecurity))
                additional[key] = JsonBool(value);
        }
        // Custom properties also surface as `custom_<name>` entries in `additional`.
        foreach (var (k, v) in custom) additional["custom_" + k] = v;

        var docxMeta = new DocxMetadata
        {
            CoreProperties = CoreToElement(core),
            AppProperties = AppToElement(app),
            CustomProperties = custom.Count > 0 ? custom : new(),
        };

        List<string>? keywords = core.Keywords is { } kw
            ? kw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : null;

        return new Metadata
        {
            Title = core.Title,
            Subject = core.Subject,
            Authors = core.Creator is { } c ? new List<string> { c } : null,
            Keywords = keywords,
            Language = core.Language,
            CreatedAt = core.Created,
            ModifiedAt = core.Modified,
            CreatedBy = core.Creator,
            ModifiedBy = core.LastModifiedBy,
            Format = new FormatMetadata { FormatType = "docx", Payload = docxMeta },
            Additional = additional,
        };
    }

    private static JsonElement CoreToElement(CoreProperties c) => DictToElement(new()
    {
        ["title"] = c.Title, ["subject"] = c.Subject, ["creator"] = c.Creator, ["keywords"] = c.Keywords,
        ["description"] = c.Description, ["last_modified_by"] = c.LastModifiedBy, ["revision"] = c.Revision,
        ["created"] = c.Created, ["modified"] = c.Modified, ["category"] = c.Category,
        ["content_status"] = c.ContentStatus, ["language"] = c.Language, ["identifier"] = c.Identifier,
        ["version"] = c.Version, ["last_printed"] = c.LastPrinted,
    });

    private static JsonElement AppToElement(AppProperties a) => DictToElement(new()
    {
        ["application"] = a.Application, ["app_version"] = a.AppVersion, ["template"] = a.Template,
        ["total_time"] = a.TotalTime, ["pages"] = a.Pages, ["words"] = a.Words, ["characters"] = a.Characters,
        ["characters_with_spaces"] = a.CharactersWithSpaces, ["lines"] = a.Lines, ["paragraphs"] = a.Paragraphs,
        ["company"] = a.Company, ["doc_security"] = a.DocSecurity, ["scale_crop"] = a.ScaleCrop,
        ["links_up_to_date"] = a.LinksUpToDate, ["shared_doc"] = a.SharedDoc, ["hyperlinks_changed"] = a.HyperlinksChanged,
    });

    /// <summary>Serialize a key→value map to a JsonElement, omitting null values (canonicalizer
    /// strips nulls on both sides, so non-null-only is equivalent).</summary>
    private static JsonElement DictToElement(Dictionary<string, object?> map)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var (k, v) in map)
            {
                if (v is null) continue;
                w.WritePropertyName(k);
                switch (v)
                {
                    case string s: w.WriteStringValue(s); break;
                    case int n: w.WriteNumberValue(n); break;
                    case bool bo: w.WriteBooleanValue(bo); break;
                    default: w.WriteStringValue(v.ToString()); break;
                }
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement JsonString(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    private static JsonElement JsonNumber(int n) =>
        JsonDocument.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();

    private static JsonElement JsonBool(bool b) =>
        JsonDocument.Parse(b ? "true" : "false").RootElement.Clone();
}

using System.Text;
using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Core.Ocr;
using XRay.Content.Internal.Ooxml;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>
/// PowerPoint presentation extractor. Ports `extractors/pptx.rs` + `extraction/pptx`.
/// Reads slides into a markdown/plain content string, then rebuilds an
/// <see cref="InternalDocument"/> from that string (block-splitting) exactly as the Rust
/// <c>build_internal_document</c> does.
/// </summary>
public sealed class PptxExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-powerpoint.presentation.macroEnabled.12",
        "application/vnd.openxmlformats-officedocument.presentationml.slideshow",
        "application/vnd.openxmlformats-officedocument.presentationml.template",
        "application/vnd.ms-powerpoint.template.macroEnabled.12",
    };

    // Keys mapped to typed Metadata fields (excluded from `additional`); slide_count is dropped.
    private static readonly HashSet<string> StandardKeys = new()
    {
        "title", "subject", "created_by", "modified_by", "created_at", "modified_at", "author", "keywords",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        bool plain = config.OutputFormat.Equals(OutputFormat.Plain);
        var result = PptxReader.Extract(content, plain, injectPlaceholders: true, config.SecurityLimits);
        var doc = BuildDocumentFromResult(result, mimeType, config.SecurityLimits);
        // A slide's pictures are not part of what the reader emits, so the OCR pass would find
        // nothing to read in a deck of screenshots without this.
        OcrImageSource.AddPicturesFromZip(doc, content, config, "ppt/media/");
        return doc;
    }

    private static InternalDocument BuildDocumentFromResult(PptxResult result, string mimeType, SecurityLimits? limits)
    {
        var doc = BuildInternalDocument(result.Content, result.SlideCount, limits);
        doc.MimeType = mimeType;

        var office = result.OfficeMetadata;
        string? Get(string k) => office.TryGetValue(k, out var v) ? v : null;

        var pptxMeta = new PptxMetadata
        {
            SlideCount = result.AppSlideCount,
            SlideNames = result.SlideNames,
            ImageCount = (uint)result.ImageCount,
            TableCount = (uint)result.TableCount,
        };

        List<string>? authors = Get("author") is { } a ? new List<string> { a } : null;
        List<string>? keywords = Get("keywords") is { } kw
            ? kw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : null;

        var additional = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in office)
        {
            if (StandardKeys.Contains(key) || key == "slide_count") continue;
            if (key is "notes_count" or "hidden_slides")
                additional[key] = long.TryParse(value, out var n) ? JsonNumber(n) : JsonString(value);
            else
                additional[key] = JsonString(value);
        }

        doc.Metadata = new Metadata
        {
            Title = Get("title"),
            Subject = Get("subject"),
            Authors = authors,
            Keywords = keywords,
            CreatedAt = Get("created_at"),
            ModifiedAt = Get("modified_at"),
            CreatedBy = Get("created_by"),
            ModifiedBy = Get("modified_by"),
            Format = new FormatMetadata { FormatType = "pptx", Payload = pptxMeta },
            Additional = additional,
        };
        return doc;
    }

    /// <summary>Ports Rust <c>PptxExtractor::build_internal_document</c> — splits <paramref name="content"/>
    /// into "\n\n" blocks and interprets headings/lists/tables/paragraphs.</summary>
    private static InternalDocument BuildInternalDocument(string content, int slideCount, SecurityLimits? limits)
    {
        var budget = new SecurityBudget(limits ?? new SecurityLimits());
        var builder = new InternalDocumentBuilder("pptx");
        uint slideNum = 0;
        bool inNotes = false;

        foreach (var block in content.Split("\n\n"))
        {
            budget.Step();
            string trimmed = block.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("### Notes:", StringComparison.Ordinal) || trimmed == "Notes:")
            {
                inNotes = true;
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                inNotes = false;
                slideNum++;
                string title = trimmed[2..].Trim();
                if (title.Length > 0)
                {
                    budget.AccountText(Encoding.UTF8.GetByteCount(title));
                    builder.PushHeading(2, title, null, null);
                }
                continue;
            }

            if (inNotes) inNotes = false;

            if (trimmed.StartsWith('|'))
            {
                var cells = ParseMarkdownTable(trimmed);
                if (cells.Count > 0) builder.PushTableFromCells(cells, slideNum, null);
                continue;
            }

            bool? inList = null; // null = no list open, else ordered flag
            foreach (var line in SplitLines(trimmed))
            {
                string lt = line.Trim();
                if (lt.Length == 0)
                {
                    if (inList is not null) { builder.EndList(); inList = null; }
                    continue;
                }

                (bool Ordered, string Text)? listMatch = null;
                if (lt.StartsWith("- ", StringComparison.Ordinal))
                    listMatch = (false, lt[2..]);
                else if (StripOrderedPrefix(lt) is { } rest)
                    listMatch = (true, rest);

                if (listMatch is { } lm)
                {
                    if (inList is { } prev && prev != lm.Ordered) { builder.EndList(); builder.PushList(lm.Ordered); inList = lm.Ordered; }
                    else if (inList is null) { builder.PushList(lm.Ordered); inList = lm.Ordered; }
                    budget.AccountText(Encoding.UTF8.GetByteCount(lm.Text));
                    builder.PushListItem(lm.Text, lm.Ordered, new(), slideNum, null);
                }
                else
                {
                    if (inList is not null) { builder.EndList(); inList = null; }
                    budget.AccountText(Encoding.UTF8.GetByteCount(lt));
                    builder.PushParagraph(lt, new(), null, null);
                }
            }
            if (inList is not null) builder.EndList();
        }

        if (slideNum == 0 && slideCount > 0)
            builder.PushSlide(1, null, 1);

        return builder.Build();
    }

    private static string? StripOrderedPrefix(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] is >= '0' and <= '9') i++;
        if (i == 0 || i + 2 > line.Length) return null;
        if (line[i] == '.' && line[i + 1] == ' ') return line[(i + 2)..];
        return null;
    }

    private static List<List<string>> ParseMarkdownTable(string tableText)
    {
        var cells = new List<List<string>>();
        foreach (var line in SplitLines(tableText))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Contains("---")) continue;
            var row = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToList();
            if (row.Count > 0) cells.Add(row);
        }
        return cells;
    }

    // Rust `str::lines()`: split on '\n', strip a trailing '\r'.
    private static IEnumerable<string> SplitLines(string s)
    {
        foreach (var line in s.Split('\n'))
            yield return line.EndsWith('\r') ? line[..^1] : line;
    }

    private static JsonElement JsonString(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    private static JsonElement JsonNumber(long n) =>
        JsonDocument.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();
}

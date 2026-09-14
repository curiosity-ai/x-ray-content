// Ported from Rust `crates/xberg/src/extractors/odt.rs` (OdtExtractor + extract_content).
// Tracked-changes/revisions are omitted (the C# port's
// InternalDocument.Revisions is [JsonIgnore]); image_kind::classify is skipped.

using System.IO.Compression;
using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Core.Ocr;
using XRay.Content.Internal.Odf;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>
/// Native ODT (OpenDocument Text) extractor. Parses the ZIP container's content.xml,
/// styles.xml and meta.xml directly. Mirrors Rust <c>OdtExtractor</c> (priority 60).
/// </summary>
public sealed class OdtExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.oasis.opendocument.text" };

    public int Priority => 60;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        byte[] bytes = content.ToArray();

        InternalDocument doc;
        using (var archiveStream = new MemoryStream(bytes, writable: false))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
        {
            doc = OdfContentParser.BuildInternalDocument(archive, config.SecurityLimits);
        }
        doc.MimeType = mimeType;

        // Metadata from meta.xml.
        OdtProperties props;
        using (var metaStream = new MemoryStream(bytes, writable: false))
        using (var metaArchive = new ZipArchive(metaStream, ZipArchiveMode.Read))
        {
            props = OdfMetadata.Extract(metaArchive);
        }

        doc.Metadata = BuildMetadata(props);
        OcrImageSource.AddPicturesFromZip(doc, bytes, config, "Pictures/");
        return doc;
    }

    /// <summary>
    /// Map ODF document properties onto the common metadata shape. Shared with the presentation
    /// extractor, which reads the same <c>meta.xml</c> schema.
    /// </summary>
    internal static Metadata BuildMetadata(OdtProperties props)
    {
        static JsonElement S(string s) => JsonSerializer.SerializeToElement(s);
        static JsonElement N(long n) => JsonSerializer.SerializeToElement(n);

        var additional = new Dictionary<string, JsonElement>();

        string? createdBy = null;
        List<string>? authors = null;
        if (props.Creator is not null)
        {
            authors = new List<string> { props.Creator };
            createdBy = props.Creator;
        }

        if (props.InitialCreator is not null)
            additional["initial_creator"] = S(props.InitialCreator);
        if (props.Description is not null)
            additional["description"] = S(props.Description);
        if (props.Generator is not null)
            additional["generator"] = S(props.Generator);
        if (props.EditingDuration is not null)
            additional["editing_duration"] = S(props.EditingDuration);
        if (props.EditingCycles is not null)
            additional["editing_cycles"] = S(props.EditingCycles);
        if (props.PageCount is not null)
            additional["page_count"] = N(props.PageCount.Value);
        if (props.WordCount is not null)
            additional["word_count"] = N(props.WordCount.Value);
        if (props.CharacterCount is not null)
            additional["character_count"] = N(props.CharacterCount.Value);
        if (props.ParagraphCount is not null)
            additional["paragraph_count"] = N(props.ParagraphCount.Value);
        if (props.TableCount is not null)
            additional["table_count"] = N(props.TableCount.Value);
        if (props.ImageCount is not null)
            additional["image_count"] = N(props.ImageCount.Value);

        List<string>? keywords = null;
        if (props.Keywords is not null)
        {
            keywords = props.Keywords
                .Split(',')
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();
        }

        return new Metadata
        {
            Title = props.Title,
            Subject = props.Subject,
            Authors = authors,
            Keywords = keywords,
            Language = props.Language,
            CreatedAt = props.CreationDate,
            ModifiedAt = props.Date,
            CreatedBy = createdBy,
            Additional = additional,
        };
    }
}

using System.Text.Json.Serialization;

namespace XRay.Content.Types;

/// <summary>Unit of pagination. Serialized as a bare snake_case string.</summary>
public enum PageUnitType
{
    Page,
    Slide,
    Sheet,
}

/// <summary>Per-page content. `tables`/`image_indices` are omitted when empty (see PORT_NOTES).</summary>
public sealed class PageContent
{
    public uint PageNumber { get; set; }
    public string Content { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<Table> Tables { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<uint> ImageIndices { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Hierarchy { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsBlank { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? LayoutRegions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeakerNotes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectionName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SheetName { get; set; }

    /// <summary>
    /// This page rendered in the extraction's output format, when
    /// <see cref="XRay.Content.Core.ExtractionConfig.RenderPagesInOutputFormat"/> asked for it.
    /// </summary>
    /// <remarks>
    /// <see cref="Content"/> is a concatenation of the page's element texts and is not the
    /// document's markup — a heading loses its hashes, and a table, whose element carries no text,
    /// disappears. This is the page as the document's own renderer writes it, so a caller can have
    /// both the markup and the page it came from. Not part of the wire format, like
    /// <see cref="ExtractedDocument.FormattedContent"/>.
    /// </remarks>
    [JsonIgnore]
    public string? FormattedContent { get; set; }
}

/// <summary>
/// One entry of <see cref="PageStructure.Pages"/>.
/// </summary>
/// <remarks>
/// Upstream's <c>PageInfo</c> marks <c>dimensions</c> <c>#[serde(skip)]</c> — it never reaches
/// the wire — but the DocTags renderer reads it back to place <c>&lt;loc_*&gt;</c> tokens, so it
/// travels here as an ignored property rather than not at all.
/// </remarks>
public sealed record PageInfoDto(uint Number)
{
    [JsonIgnore]
    public (double Width, double Height)? Dimensions { get; init; }
}

/// <summary>Document-level page structure summary.</summary>
public sealed class PageStructure
{
    public uint TotalCount { get; set; }
    public PageUnitType UnitType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Boundaries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Pages { get; set; }
}

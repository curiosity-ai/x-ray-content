// The OneNote content model of [MS-ONE] 1.3 and 2.2, shaped after the Microsoft Graph OneNote
// resources (notebook, sectionGroup, onenoteSection, onenotePage) so a parsed notebook and the
// Graph's view of the same notebook can be lined up. Every element keeps the identity of the
// object it was read from, and the store it came from stays reachable for anything this model
// does not surface.

using XRay.Content.OneNote.Store;

namespace XRay.Content.OneNote;

/// <summary>A colour as OneNote stores it. <see cref="IsAutomatic"/> marks the "automatic"
/// value of a COLORREF ([MS-ONE] 2.2.8).</summary>
public readonly record struct OneNoteColor(byte R, byte G, byte B, byte A = 255)
{
    public bool IsAutomatic { get; init; }

    /// <summary>The CSS form, <c>#RRGGBB</c>.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => IsAutomatic ? "auto" : ToHex();

    /// <summary>[MS-ONE] 2.2.8 COLORREF: 0x00BBGGRR, or 0xFF000000 for automatic.</summary>
    public static OneNoteColor? FromColorRef(uint? value)
    {
        if (value is not { } v) return null;
        if (v == 0xFF000000) return new OneNoteColor(0, 0, 0) { IsAutomatic = true };
        if ((v & 0xFF000000) != 0) return null;
        return new OneNoteColor((byte)v, (byte)(v >> 8), (byte)(v >> 16));
    }

    /// <summary>[MS-ONE] 2.2.95 NotebookColor; 0xFFFFFFFF means "no colour".</summary>
    public static OneNoteColor? FromNotebookColor(uint? value)
    {
        if (value is not { } v || v == 0xFFFFFFFF) return null;
        return new OneNoteColor((byte)v, (byte)(v >> 8), (byte)(v >> 16));
    }
}

/// <summary>A notebook: the folder a table of contents (<c>.onetoc2</c>) describes, or the
/// contents of a <c>.onepkg</c> package. Mirrors the Graph <c>notebook</c> resource.</summary>
public sealed class OneNoteNotebook
{
    public string DisplayName { get; set; } = "";

    /// <summary>The table of contents' file identity (<c>Header.guidFile</c>), when there is one.
    /// Sections name it as their <see cref="OneNoteSection.NotebookFileId"/>.</summary>
    public Guid? Id { get; set; }

    public OneNoteColor? Color { get; set; }

    /// <summary>[MS-ONE] 2.3.103: whether version history pages may be kept.</summary>
    public bool? EnableHistory { get; set; }

    public List<OneNoteSection> Sections { get; } = new();
    public List<OneNoteSectionGroup> SectionGroups { get; } = new();

    /// <summary>The <c>OneNote_RecycleBin</c> folder: deleted sections and deleted pages.</summary>
    public OneNoteSectionGroup? RecycleBin { get; set; }

    public OneStoreEncoding? Encoding { get; set; }

    public List<string> Warnings { get; } = new();

    /// <summary>Every section of the notebook, depth first, excluding the recycle bin.</summary>
    public IEnumerable<OneNoteSection> AllSections()
    {
        foreach (var s in Sections) yield return s;
        foreach (var g in SectionGroups)
            foreach (var s in g.AllSections()) yield return s;
    }

    /// <summary>Every page of every section, excluding the recycle bin.</summary>
    public IEnumerable<OneNotePage> AllPages() => AllSections().SelectMany(s => s.Pages);

    public override string ToString() => $"Notebook {DisplayName}";
}

/// <summary>A folder of sections inside a notebook. Mirrors the Graph <c>sectionGroup</c>.</summary>
public sealed class OneNoteSectionGroup
{
    public string DisplayName { get; set; } = "";

    /// <summary>The folder's path relative to the notebook root, with <c>/</c> separators.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>The identity of the group's own table of contents, when it has one.</summary>
    public Guid? Id { get; set; }

    public OneNoteColor? Color { get; set; }

    /// <summary>The position the parent table of contents gives the group.</summary>
    public uint? Order { get; set; }

    public bool IsRecycleBin { get; set; }

    public List<OneNoteSection> Sections { get; } = new();
    public List<OneNoteSectionGroup> SectionGroups { get; } = new();

    public IEnumerable<OneNoteSection> AllSections()
    {
        foreach (var s in Sections) yield return s;
        foreach (var g in SectionGroups)
            foreach (var s in g.AllSections()) yield return s;
    }

    public override string ToString() => $"Section group {DisplayName}";
}

/// <summary>A section: one <c>.one</c> file. Mirrors the Graph <c>onenoteSection</c>.</summary>
public sealed class OneNoteSection
{
    /// <summary>The section's identity as OneNote's links carry it (<c>section-id</c>): the
    /// FileIdentityGuid of the section's metadata when it has one ([MS-ONE] 2.2.93), else the
    /// file's own identity. The two differ once a section has been copied or re-uploaded: the
    /// file gets a new identity, the section keeps the one its links were made with.</summary>
    public Guid Id { get; set; }

    /// <summary>The file's identity: <c>Header.guidFile</c>, or the FSSHTTPB header cell's
    /// FileIdentityGuid. A notebook's table of contents names sections by this or by
    /// <see cref="Id"/>.</summary>
    public Guid FileId { get; set; }

    public string DisplayName { get; set; } = "";

    /// <summary>The file name, with extension, when the section was read from a named file.</summary>
    public string? FileName { get; set; }

    /// <summary>The file's path relative to the notebook root, with <c>/</c> separators.</summary>
    public string? RelativePath { get; set; }

    /// <summary>The <c>guidFile</c> of the notebook's table of contents (Header.guidAncestor).</summary>
    public Guid? NotebookFileId { get; set; }

    public OneNoteColor? Color { get; set; }

    /// <summary>The position the table of contents gives the section.</summary>
    public uint? Order { get; set; }

    public DateTimeOffset? CreatedDateTime { get; set; }

    /// <summary>The most recent modification of any page.</summary>
    public DateTimeOffset? LastModifiedDateTime => Pages.Select(p => p.LastModifiedDateTime).Where(t => t is not null).DefaultIfEmpty().Max();

    public OneStoreEncoding Encoding { get; set; }

    /// <summary>True for a password-protected section: its pages are encrypted and not read.</summary>
    public bool IsPasswordProtected { get; set; }

    /// <summary>True for <c>OneNote_DeletedPages.one</c>, the recycle bin's page store.</summary>
    public bool IsDeletedPages { get; set; }

    public List<OneNotePage> Pages { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>The parsed store, for anything this model does not surface.</summary>
    public OneStore? Store { get; set; }

    public override string ToString() => $"Section {DisplayName} ({Pages.Count} pages)";
}

/// <summary>A page. Mirrors the Graph <c>onenotePage</c>, plus everything the file holds about
/// the page's content.</summary>
public sealed class OneNotePage
{
    /// <summary>The page's identity: [MS-ONE] 2.2.58 NotebookManagementEntityGuid of its
    /// metadata. This is the GUID OneNote's links carry as <c>page-id</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>The object space the page is stored in.</summary>
    public ExGuid ObjectSpaceId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Zero-based indentation of the page among its siblings, as the Graph's
    /// <c>level</c> reports it ([MS-ONE] PageLevel minus one).</summary>
    public int Level { get; set; }

    /// <summary>Zero-based position of the page in its section, as the Graph's <c>order</c>.</summary>
    public int Order { get; set; }

    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    /// <summary>The page's author ([MS-ONE] 2.2.67).</summary>
    public string? Author { get; set; }

    /// <summary>The author of the most recent revision.</summary>
    public string? LastModifiedBy { get; set; }

    /// <summary>The title block: the title text plus the date and time shown under it.</summary>
    public OneNoteTitle? TitleBlock { get; set; }

    /// <summary>The page's content, in the order the page lists it: outlines, and images,
    /// attachments and ink placed directly on the page.</summary>
    public List<OneNoteElement> Content { get; } = new();

    /// <summary>Page size in half-inch units ([MS-ONE] 2.3.6, 2.3.7).</summary>
    public float? Width { get; set; }
    public float? Height { get; set; }
    public bool? IsPortrait { get; set; }
    public bool IsRightToLeft { get; set; }
    public bool IsReadOnly { get; set; }

    /// <summary>[MS-ONE] 2.3.73: the page is marked as deleted (it sits in the recycle bin).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>[MS-ONE] 2.1.2: this page records a merge conflict on its parent.</summary>
    public bool IsConflictPage { get; set; }
    public string? ConflictingUserName { get; set; }

    /// <summary>Conflict pages attached to this page.</summary>
    public List<OneNotePage> ConflictPages { get; } = new();

    public bool HasVersionPages { get; set; }

    /// <summary>Earlier versions of the page ([MS-ONE] 2.1.17), oldest first, when version
    /// history was requested.</summary>
    public List<OneNotePage> Versions { get; } = new();

    /// <summary>Handwriting recognition OneNote stored for the page's ink, when present.</summary>
    public OneNoteInkRecognition? InkRecognition { get; set; }

    /// <summary>The page's section; null for a version or conflict page read on its own.</summary>
    public OneNoteSection? Section { get; set; }

    /// <summary>Every element of the page in reading order, nested elements included.</summary>
    public IEnumerable<OneNoteElement> Descendants()
    {
        if (TitleBlock is not null)
            foreach (var e in TitleBlock.Outlines.SelectMany(o => o.SelfAndDescendants())) yield return e;
        foreach (var c in Content)
            foreach (var e in c.SelfAndDescendants()) yield return e;
    }

    public IEnumerable<OneNoteImage> Images => Descendants().OfType<OneNoteImage>();
    public IEnumerable<OneNoteEmbeddedFile> EmbeddedFiles => Descendants().OfType<OneNoteEmbeddedFile>();

    public override string ToString() => $"Page {Title} ({Id:B})";
}

/// <summary>[MS-ONE] 2.1.16: a page title, with the date and time OneNote shows beneath it.</summary>
public sealed class OneNoteTitle
{
    public List<OneNoteOutline> Outlines { get; } = new();
    public string Text { get; set; } = "";
    public string? Date { get; set; }
    public string? Time { get; set; }
}

/// <summary>Base of every piece of page content.</summary>
public abstract class OneNoteElement
{
    /// <summary>The identity of the object the element was read from.</summary>
    public ExGuid ObjectId { get; set; }

    public DateTimeOffset? LastModifiedDateTime { get; set; }

    /// <summary>The element and every element nested in it, in reading order.</summary>
    public virtual IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
    }
}

/// <summary>Position and size of an element placed on the page, in half-inch units
/// relative to the page's margin origin.</summary>
public readonly record struct OneNoteLayout(float? X, float? Y, float? Width, float? Height);

/// <summary>[MS-ONE] 2.2.20: a container of outline elements placed at a position on the page.</summary>
public sealed class OneNoteOutline : OneNoteElement
{
    public OneNoteLayout Layout { get; set; }
    public bool IsTitleText { get; set; }
    public bool IsTitleDate { get; set; }
    public List<OneNoteOutlineElement> Children { get; } = new();

    public override IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var e in c.SelfAndDescendants()) yield return e;
    }
}

/// <summary>[MS-ONE] 2.2.21: one entry of an outline — a paragraph, a table, an image or an
/// attachment — with the entries indented beneath it. An outline group ([MS-ONE] 2.2.22) is
/// an element with no content of its own.</summary>
public sealed class OneNoteOutlineElement : OneNoteElement
{
    /// <summary>Indentation below the outline, zero for a top-level entry.</summary>
    public int Depth { get; set; }

    public bool IsGroup { get; set; }

    public OneNoteElement? Content { get; set; }

    /// <summary>The bullet or number of a list entry.</summary>
    public OneNoteListMarker? List { get; set; }

    public string? Author { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public bool IsRightToLeft { get; set; }

    public List<OneNoteOutlineElement> Children { get; } = new();

    public override IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
        if (Content is not null)
            foreach (var e in Content.SelfAndDescendants()) yield return e;
        foreach (var c in Children)
            foreach (var e in c.SelfAndDescendants()) yield return e;
    }
}

/// <summary>[MS-ONE] 2.2.25: a list item's bullet or number.</summary>
public sealed class OneNoteListMarker
{
    /// <summary>The raw NumberListFormat, without its length prefix.</summary>
    public string Format { get; set; } = "";

    public bool IsNumbered { get; set; }

    /// <summary>The automatic number this item carries, for a numbered list.</summary>
    public int? Number { get; set; }

    /// <summary>The marker as displayed: the bullet character, or e.g. <c>3.</c> / <c>c)</c>.</summary>
    public string Text { get; set; } = "";

    /// <summary>[MS-OSHARED] MSONFC numbering style of a numbered list.</summary>
    public int? NumberFormat { get; set; }

    public int? Restart { get; set; }
    public string? Font { get; set; }
}

/// <summary>Character formatting of a text run ([MS-ONE] 2.2.43).</summary>
public sealed record OneNoteTextStyle
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public bool Superscript { get; init; }
    public bool Subscript { get; init; }
    public string? Font { get; init; }

    /// <summary>Font size in points.</summary>
    public float? FontSize { get; init; }

    public OneNoteColor? FontColor { get; init; }
    public OneNoteColor? Highlight { get; init; }
    public uint? LanguageId { get; init; }
    public bool IsHyperlink { get; init; }
    public bool IsHidden { get; init; }
    public bool IsMath { get; init; }

    /// <summary>The paragraph style a paragraph style object names (<c>h1</c>, <c>p</c>, <c>PageTitle</c>, <c>code</c>, ...).</summary>
    public string? StyleId { get; init; }

    public static readonly OneNoteTextStyle Default = new();
}

/// <summary>A run of characters sharing one style. Offsets index <see cref="OneNoteRichText.Text"/>.</summary>
public sealed record OneNoteTextRun(int Start, int Length, string Text, OneNoteTextStyle Style)
{
    /// <summary>The link target when the run is a hyperlink's display text.</summary>
    public string? Hyperlink { get; init; }

    /// <summary>The math object a math-formatted run carries.</summary>
    public OneNoteMathObject? Math { get; init; }
}

/// <summary>A hyperlink over a range of <see cref="OneNoteRichText.Text"/>.</summary>
public sealed record OneNoteHyperlink(int Start, int Length, string Text, string Url);

/// <summary>[MS-ONE] 2.2.23: a paragraph of rich text.</summary>
public sealed class OneNoteRichText : OneNoteElement
{
    /// <summary>The visible text. Hidden runs (OneNote's hyperlink field codes) are removed and
    /// a soft line break (U+000B) is a <c>\n</c>.</summary>
    public string Text { get; set; } = "";

    public List<OneNoteTextRun> Runs { get; } = new();
    public List<OneNoteHyperlink> Hyperlinks { get; } = new();
    public List<OneNoteNoteTag> NoteTags { get; } = new();

    /// <summary>The paragraph's style ([MS-ONE] 2.2.44): <c>StyleId</c> is <c>h1</c>..<c>h6</c>,
    /// <c>p</c>, <c>PageTitle</c>, <c>cite</c>, <c>blockquote</c>, <c>code</c> and so on.</summary>
    public OneNoteTextStyle? ParagraphStyle { get; set; }

    /// <summary>0 left, 1 centre, 2 right ([MS-ONE] 2.3.94).</summary>
    public int? Alignment { get; set; }

    public uint? LanguageId { get; set; }
    public bool IsRightToLeft { get; set; }
    public bool IsTitleText { get; set; }
    public bool IsTitleDate { get; set; }
    public bool IsTitleTime { get; set; }

    /// <summary>Handwriting written inline in the paragraph.</summary>
    public List<OneNoteInk> InlineInk { get; } = new();

    /// <summary>The heading level the paragraph style implies (1-6), or null.</summary>
    public int? HeadingLevel => ParagraphStyle?.StyleId is { Length: 2 } id && id[0] == 'h' && id[1] is >= '1' and <= '6' ? id[1] - '0' : null;

    public override string ToString() => Text;
}

/// <summary>[MS-ONE] 2.2.24: an image.</summary>
public sealed class OneNoteImage : OneNoteElement
{
    public FileDataReference? Data { get; set; }

    /// <summary>The resized copy OneNote keeps for the web view ([MS-ONE] 2.3.98).</summary>
    public FileDataReference? WebData { get; set; }

    /// <summary>The file extension of the image data, with a dot (<c>.png</c>).</summary>
    public string? Extension { get; set; }

    /// <summary>The IANA media type, from the data's signature or its extension.</summary>
    public string? ContentType { get; set; }

    public string? FileName { get; set; }
    public string? AltText { get; set; }

    /// <summary>Text OneNote recognised in the image (its own OCR), when stored.</summary>
    public string? OcrText { get; set; }

    public string? Hyperlink { get; set; }

    /// <summary>Displayed size in half-inch units.</summary>
    public float? Width { get; set; }
    public float? Height { get; set; }

    public OneNoteLayout Layout { get; set; }
    public bool IsBackground { get; set; }

    /// <summary>For a printout of a multi-page file, the page shown (zero based).</summary>
    public uint? DisplayedPageNumber { get; set; }

    public List<OneNoteNoteTag> NoteTags { get; } = new();

    public byte[]? ReadData() => Data?.ReadAll();
}

/// <summary>[MS-ONE] 2.2.32: an attached file, or an audio or video recording.</summary>
public sealed class OneNoteEmbeddedFile : OneNoteElement
{
    public FileDataReference? Data { get; set; }
    public string? FileName { get; set; }

    /// <summary>The path the file was attached from ([MS-ONE] 2.2.72).</summary>
    public string? SourcePath { get; set; }

    public string? Extension { get; set; }
    public string? ContentType { get; set; }

    /// <summary>The attachment icon.</summary>
    public OneNoteImage? Icon { get; set; }

    /// <summary>1 audio, 2 video ([MS-ONE] 2.3.62), null for an ordinary file.</summary>
    public uint? RecordingType { get; set; }

    public OneNoteLayout Layout { get; set; }
    public List<OneNoteNoteTag> NoteTags { get; } = new();

    public long? Size => Data?.Length;
    public byte[]? ReadData() => Data?.ReadAll();
}

/// <summary>[MS-ONE] 2.2.26: a table.</summary>
public sealed class OneNoteTable : OneNoteElement
{
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }

    /// <summary>Column widths in half-inch units.</summary>
    public List<float> ColumnWidths { get; } = new();

    public bool BordersVisible { get; set; }
    public List<OneNoteTableRow> Rows { get; } = new();
    public List<OneNoteNoteTag> NoteTags { get; } = new();

    public override IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
        foreach (var row in Rows)
            foreach (var cell in row.Cells)
                foreach (var e in cell.SelfAndDescendants()) yield return e;
    }
}

public sealed class OneNoteTableRow
{
    public ExGuid ObjectId { get; set; }
    public List<OneNoteTableCell> Cells { get; } = new();
}

public sealed class OneNoteTableCell : OneNoteElement
{
    public OneNoteColor? Background { get; set; }
    public List<OneNoteOutlineElement> Children { get; } = new();

    public override IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var e in c.SelfAndDescendants()) yield return e;
    }
}

/// <summary>[MS-ONE] 2.1.9: a note tag on a paragraph, image, table or attachment.</summary>
public sealed class OneNoteNoteTag
{
    public string? Label { get; set; }

    /// <summary>[MS-ONE] 2.3.86 NoteTagShape: which icon is shown.</summary>
    public int Shape { get; set; }

    public bool IsCheckable => NoteTagShapes.IsCheckable(Shape);

    /// <summary>[MS-ONE] 2.3.85 ActionItemType: 0-99 a normal tag, 100-105 a task.</summary>
    public int? ActionItemType { get; set; }

    public bool IsTask { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsDisabled { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? CompletedDateTime { get; set; }
    public DateTimeOffset? DueDateTime { get; set; }
    public OneNoteColor? TextColor { get; set; }
    public OneNoteColor? HighlightColor { get; set; }

    /// <summary>The <c>data-tag</c> value the Graph's page HTML uses for this tag
    /// (<c>to-do</c>, <c>important</c>, ...; <c>:completed</c> appended when done), or null
    /// for a custom tag the Graph has no name for.</summary>
    public string? GraphDataTag { get; set; }
}

/// <summary>Handwriting or a drawing.</summary>
public sealed class OneNoteInk : OneNoteElement
{
    public List<OneNoteInkStroke> Strokes { get; } = new();
    public List<OneNoteInk> Groups { get; } = new();

    /// <summary>Bounding box of the strokes, in the ink's own coordinate units (HIMETRIC).</summary>
    public (float X, float Y, float Width, float Height)? BoundingBox { get; set; }

    public OneNoteLayout Layout { get; set; }

    public IEnumerable<OneNoteInkStroke> AllStrokes() => Strokes.Concat(Groups.SelectMany(g => g.AllStrokes()));

    public override IEnumerable<OneNoteElement> SelfAndDescendants()
    {
        yield return this;
        foreach (var g in Groups)
            foreach (var e in g.SelfAndDescendants()) yield return e;
    }
}

public sealed class OneNoteInkStroke
{
    public ExGuid ObjectId { get; set; }
    public List<(float X, float Y)> Points { get; } = new();
    public OneNoteColor? Color { get; set; }

    /// <summary>Pen width and height, in the stroke's coordinate units.</summary>
    public float Width { get; set; }
    public float Height { get; set; }

    public byte? Transparency { get; set; }
    public byte? PenTip { get; set; }

    /// <summary>0 handwriting, 1 drawing, 2 either ([MS-ONE] undocumented InkBias).</summary>
    public int? Bias { get; set; }
}

/// <summary>Handwriting recognition results for a page.</summary>
public sealed class OneNoteInkRecognition
{
    public List<List<OneNoteRecognizedWord>> Lines { get; } = new();

    public string Text => string.Join("\n", Lines.Select(l => string.Join(" ", l.Select(w => w.Text).Where(t => t.Length > 0))));
}

public sealed class OneNoteRecognizedWord
{
    public List<string> Alternatives { get; } = new();
    public string Text => Alternatives.Count > 0 ? Alternatives[0] : "";
    public uint? LanguageId { get; set; }
}

/// <summary>Content embedded from the web (a video, a form): undocumented jcid 0x00060058.</summary>
public sealed class OneNoteEmbeddedMedia : OneNoteElement
{
    public string? Url { get; set; }
    public uint? EmbedType { get; set; }
    public OneNoteLayout Layout { get; set; }
}

/// <summary>The structure carried by a math-formatted text run (undocumented).</summary>
public sealed record OneNoteMathObject(uint Type, uint ArgumentCount, byte? Column, byte? Alignment, char? Char, char? Char1, char? Char2)
{
    public string TypeName => Type switch
    {
        0 => "SimpleText",
        10 => "Accent",
        11 => "Box",
        12 => "BoxedFormula",
        13 => "Brackets",
        14 => "BracketsWithSeparators",
        15 => "EquationArray",
        16 => "Fraction",
        17 => "FunctionApply",
        18 => "LeftSubSup",
        19 => "LowerLimit",
        20 => "Matrix",
        21 => "Nary",
        22 => "OpChar",
        23 => "Overbar",
        24 => "Phantom",
        25 => "Radical",
        26 => "SlashedFraction",
        27 => "Stack",
        28 => "StretchStack",
        29 => "Subscript",
        30 => "SubSup",
        31 => "Superscript",
        32 => "Underbar",
        33 => "UpperLimit",
        0x90000000 => "PlainText",
        _ => $"0x{Type:X}",
    };
}

/// <summary>An object of a type this model has no class for, kept so nothing is lost.</summary>
public sealed class OneNoteUnknownElement : OneNoteElement
{
    public JcId JcId { get; set; }
    public StoreObject? Object { get; set; }
}

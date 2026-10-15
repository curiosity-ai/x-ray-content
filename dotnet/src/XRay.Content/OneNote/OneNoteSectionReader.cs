// Builds the content model from a section's store: [MS-ONE] 2.1.14 (SectionObjectSpace),
// 2.1.10 (PageObjectSpace), 2.1.17-2.1.18 (version history), 2.1.2 (conflict pages) and the
// property sets of 2.2. Ink, handwriting recognition, math and embedded web content follow the
// undocumented structures OneNote writes.

using System.Text;
using XRay.Content.OneNote.Store;
using P = XRay.Content.OneNote.Store.OneNotePropertyNames;
using J = XRay.Content.OneNote.Store.OneNoteJcids;

namespace XRay.Content.OneNote;

/// <summary>Options for reading OneNote content.</summary>
public sealed class OneNoteReadOptions
{
    /// <summary>Read each page's earlier versions into <see cref="OneNotePage.Versions"/>.</summary>
    public bool IncludeVersionHistory { get; init; }

    /// <summary>Read conflict pages into <see cref="OneNotePage.ConflictPages"/>.</summary>
    public bool IncludeConflictPages { get; init; } = true;

    /// <summary>Keep pages marked as deleted ([MS-ONE] 2.3.73).</summary>
    public bool IncludeDeletedPages { get; init; } = true;

    /// <summary>Read the recycle bin (<c>OneNote_RecycleBin</c>) of a notebook.</summary>
    public bool IncludeRecycleBin { get; init; } = true;

    /// <summary>Resolves a <c>.onebin</c> file of a section's <c>_onefiles</c> folder.</summary>
    public Func<string, byte[]?>? ExternalFileResolver { get; init; }

    public static readonly OneNoteReadOptions Default = new();
}

/// <summary>Reads sections (<c>.one</c>) into the content model.</summary>
public static class OneNoteSectionReader
{
    private const int MaxDepth = 64;
    private const string HyperlinkMarker = "﷟HYPERLINK \"";
    private const uint InkSpaceBlob = 0x00020026;
    private const uint InkEndOfLineBlob = 0x00020027;

    /// <summary>Reads a section from the bytes of a <c>.one</c> file.</summary>
    public static OneNoteSection Read(byte[] data, string? fileName = null, OneNoteReadOptions? options = null)
    {
        options ??= OneNoteReadOptions.Default;
        var store = OneStore.Read(data, new OneStoreReadOptions { ExternalFileResolver = options.ExternalFileResolver });
        return Read(store, fileName, options);
    }

    /// <summary>Builds a section from a store already read.</summary>
    public static OneNoteSection Read(OneStore store, string? fileName = null, OneNoteReadOptions? options = null)
    {
        options ??= OneNoteReadOptions.Default;
        if (store.Kind != OneStoreFileKind.Section) throw new OneNoteFormatException("The file is a table of contents, not a section.");

        var section = new OneNoteSection
        {
            Id = store.FileGuid,
            FileId = store.FileGuid,
            FileName = fileName is null ? null : Path.GetFileName(fileName),
            DisplayName = fileName is null ? "" : Path.GetFileNameWithoutExtension(fileName),
            NotebookFileId = store.AncestorGuid == Guid.Empty ? null : store.AncestorGuid,
            Encoding = store.Encoding,
            Store = store,
        };
        section.IsDeletedPages = string.Equals(section.FileName, "OneNote_DeletedPages.one", StringComparison.OrdinalIgnoreCase);
        section.Warnings.AddRange(store.Warnings);

        var rev = store.Root.Current;
        if (rev is null)
        {
            section.Warnings.Add("The section has no current revision.");
            return section;
        }
        if (rev.IsEncrypted)
        {
            section.IsPasswordProtected = true;
            return section;
        }

        var meta = rev.GetRoot(RevisionRoles.Metadata);
        if (meta is not null)
        {
            if (meta.Properties.GetString(P.SectionDisplayName) is { Length: > 0 } name) section.DisplayName = name;
            section.Color = OneNoteColor.FromNotebookColor(meta.Properties.GetUInt32(P.NotebookColor));
            if (meta.Properties.GetGuid(P.FileIdentityGuid) is { } linkId && linkId != Guid.Empty) section.Id = linkId;
        }

        var node = rev.GetRoot(RevisionRoles.DefaultContent);
        if (node is null)
        {
            section.Warnings.Add("The section has no content root.");
            return section;
        }
        section.CreatedDateTime = OneNoteTime.FromFileTime(node.Properties.GetInteger(P.TopologyCreationTimeStamp));

        var ctx = new Context(store, options, section.Warnings);
        int order = 0;
        foreach (var seriesId in node.Properties.GetObjectIds(P.ElementChildNodes))
        {
            var series = rev.GetObject(seriesId);
            if (series is null) continue;
            foreach (var cell in series.Properties.GetObjectSpaceIds(P.ChildGraphSpaceElementNodes))
            {
                var page = ctx.ReadPage(cell, isConflict: false);
                if (page is null) continue;
                if (page.IsDeleted && !options.IncludeDeletedPages) continue;
                page.Order = order++;
                page.Section = section;
                section.Pages.Add(page);
            }
        }

        if (section.Pages.Count == 0 && ctx.SawEncryptedPage) section.IsPasswordProtected = true;
        return section;
    }

    private sealed class Context
    {
        private readonly OneStore _store;
        private readonly OneNoteReadOptions _options;
        private readonly List<string> _warnings;
        private StoreRevision _rev = null!;
        private ExGuid _space;

        public bool SawEncryptedPage { get; private set; }

        public Context(OneStore store, OneNoteReadOptions options, List<string> warnings)
        {
            _store = store;
            _options = options;
            _warnings = warnings;
        }

        public OneNotePage? ReadPage(CellId cell, bool isConflict)
        {
            var space = _store.GetObjectSpace(cell.ObjectSpace);
            if (space is null)
            {
                _warnings.Add($"Page object space {cell.ObjectSpace} is missing.");
                return null;
            }
            var rev = space.GetRevision(cell.Context);
            if (rev is null)
            {
                _warnings.Add($"Page object space {cell.ObjectSpace} has no current revision.");
                return null;
            }
            if (rev.IsEncrypted)
            {
                SawEncryptedPage = true;
                return null;
            }
            try
            {
                return ReadPage(space, rev, isConflict, nested: isConflict);
            }
            catch (OneNoteFormatException ex)
            {
                _warnings.Add($"Page {cell.ObjectSpace} could not be read: {ex.Message}");
                return null;
            }
        }

        private OneNotePage ReadPage(StoreObjectSpace space, StoreRevision rev, bool isConflict, bool nested)
        {
            var savedRev = _rev;
            var savedSpace = _space;
            _rev = rev;
            _space = space.Id;
            try
            {
                var page = new OneNotePage { ObjectSpaceId = space.Id, IsConflictPage = isConflict };
                var manifest = rev.GetRoot(RevisionRoles.DefaultContent);
                var meta = rev.GetRoot(RevisionRoles.Metadata);
                var versionMeta = rev.GetRoot(RevisionRoles.VersionMetadata);

                if (meta is not null)
                {
                    var mp = meta.Properties;
                    page.Id = mp.GetGuid(P.NotebookManagementEntityGuid) ?? Guid.Empty;
                    page.Title = mp.GetString(P.CachedTitleString) ?? "";
                    page.Level = Math.Max(0, (int)(mp.GetUInt32(P.PageLevel) ?? 1) - 1);
                    page.CreatedDateTime = OneNoteTime.FromFileTime(mp.GetInteger(P.TopologyCreationTimeStamp));
                    page.HasVersionPages = mp.GetBool(P.HasVersionPages) ?? false;
                    page.IsDeleted = IsSet(mp, P.IsDeletedGraphSpaceContent);
                    if (meta.JcId.Value == J.ConflictPageMetaData)
                    {
                        page.IsConflictPage = true;
                        page.ConflictingUserName = mp.GetString(P.ConflictingUserName);
                    }
                }
                if (versionMeta is not null)
                {
                    page.LastModifiedDateTime = OneNoteTime.FromFileTime(versionMeta.Properties.GetInteger(P.LastModifiedTimeStamp));
                    page.LastModifiedBy = AuthorOf(versionMeta.Properties.GetObjectId(P.AuthorMostRecent));
                }

                var pageNode = manifest is null ? null : Get(manifest.Properties.GetObjectId(P.ContentChildNodes));
                if (pageNode is not null)
                {
                    var pp = pageNode.Properties;
                    page.Author = pp.GetString(P.Author);
                    page.LastModifiedDateTime ??= OneNoteTime.FromTime32(pp.GetInteger(P.LastModifiedTime));
                    page.Width = pp.GetFloat(P.PageWidth);
                    page.Height = pp.GetFloat(P.PageHeight);
                    page.IsPortrait = pp.GetBool(P.PortraitPage);
                    page.IsRightToLeft = pp.GetBool(P.EditRootRTL) ?? false;
                    page.IsReadOnly = pp.GetBool(P.IsReadOnly) ?? false;
                    if (pp.GetBool(P.IsConflictPage) == true) page.IsConflictPage = true;

                    if (Get(pp.GetObjectId(P.StructureElementChildNodes)) is { } titleNode && titleNode.JcId.Value == J.TitleNode)
                        page.TitleBlock = ReadTitle(titleNode);

                    foreach (var childId in pp.GetObjectIds(P.ElementChildNodes))
                        if (ReadElement(childId, 0, 0) is { } element) page.Content.Add(element);

                    if (Get(pp.GetObjectId(P.PageRecognizedTextContainer)) is { } recognition)
                        page.InkRecognition = ReadRecognition(recognition);

                    if (page.TitleBlock is { Text.Length: > 0 } t) page.Title = t.Text;
                    else if (page.Title.Length == 0) page.Title = pp.GetString(P.CachedTitleStringFromPage) ?? "";
                }
                else
                {
                    _warnings.Add($"Page {space.Id} has no page node.");
                }

                if (manifest is not null)
                {
                    if (_options.IncludeConflictPages && !nested)
                    {
                        foreach (var cell in manifest.Properties.GetObjectSpaceIds(P.ChildGraphSpaceElementNodes))
                            if (ReadPage(cell, isConflict: true) is { } conflict) page.ConflictPages.Add(conflict);
                    }
                    if (_options.IncludeVersionHistory && !nested)
                        ReadVersions(space, manifest, page);
                }
                return page;
            }
            finally
            {
                _rev = savedRev;
                _space = savedSpace;
            }
        }

        private void ReadVersions(StoreObjectSpace space, StoreObject manifest, OneNotePage page)
        {
            foreach (var historyContext in manifest.Properties.GetContextIds(P.VersionHistoryGraphSpaceContextNodes))
            {
                var history = space.GetRevision(historyContext);
                var content = history?.GetRoot(RevisionRoles.DefaultContent);
                if (history is null || content is null) continue;
                foreach (var proxyId in content.Properties.GetObjectIds(P.ElementChildNodes))
                {
                    var proxy = history.GetObject(proxyId);
                    if (proxy is null) continue;
                    foreach (var versionContext in proxy.Properties.GetContextIds(P.VersionContextNodes))
                    {
                        var versionRev = space.GetRevision(versionContext);
                        if (versionRev is null || versionRev.IsEncrypted) continue;
                        try
                        {
                            var version = ReadPage(space, versionRev, isConflict: false, nested: true);
                            var lastModified = OneNoteTime.FromFileTime(proxy.Properties.GetInteger(P.LastModifiedTimeStamp));
                            version.LastModifiedDateTime = lastModified ?? version.LastModifiedDateTime;
                            var savedRev = _rev;
                            _rev = history;
                            version.LastModifiedBy = AuthorOf(proxy.Properties.GetObjectId(P.AuthorMostRecent)) ?? version.LastModifiedBy;
                            _rev = savedRev;
                            page.Versions.Add(version);
                        }
                        catch (OneNoteFormatException ex)
                        {
                            _warnings.Add($"A version of page {page.Id:B} could not be read: {ex.Message}");
                        }
                    }
                }
            }
            page.Versions.Sort((a, b) => Nullable.Compare(a.LastModifiedDateTime, b.LastModifiedDateTime));
        }

        private OneNoteTitle ReadTitle(StoreObject titleNode)
        {
            var title = new OneNoteTitle();
            foreach (var outlineId in titleNode.Properties.GetObjectIds(P.ElementChildNodes))
            {
                if (ReadElement(outlineId, 0, 0) is OneNoteOutline outline) title.Outlines.Add(outline);
            }
            var texts = title.Outlines.SelectMany(o => o.SelfAndDescendants()).OfType<OneNoteRichText>().ToList();
            var dates = texts.Where(t => t.IsTitleDate).Select(t => t.Text).ToList();
            var times = texts.Where(t => t.IsTitleTime).Select(t => t.Text).ToList();
            title.Date = dates.Count > 0 ? string.Join(" ", dates) : null;
            title.Time = times.Count > 0 ? string.Join(" ", times) : null;
            var titleOutline = title.Outlines.FirstOrDefault(o => o.IsTitleText) ?? title.Outlines.FirstOrDefault(o => !o.IsTitleDate);
            if (titleOutline is not null)
            {
                title.Text = string.Join(" ", titleOutline.SelfAndDescendants().OfType<OneNoteRichText>()
                    .Where(t => !t.IsTitleDate && !t.IsTitleTime)
                    .Select(t => t.Text.Replace('\n', ' ').Trim())
                    .Where(t => t.Length > 0));
            }
            return title;
        }

        // ------------------------------------------------------------------ elements

        private StoreObject? Get(ExGuid? id) => id is { } i && !i.IsNil ? _rev.GetObject(i) : null;

        private OneNoteElement? ReadElement(ExGuid id, int depth, int recursion)
        {
            if (recursion > MaxDepth) throw new OneNoteFormatException("Page content is nested too deeply.");
            var obj = Get(id);
            if (obj is null) return null;
            var p = obj.Properties;
            switch (obj.JcId.Value)
            {
                case J.OutlineNode:
                {
                    var outline = new OneNoteOutline
                    {
                        ObjectId = obj.Id,
                        LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                        Layout = new OneNoteLayout(p.GetFloat(P.OffsetFromParentHoriz), p.GetFloat(P.OffsetFromParentVert), p.GetFloat(P.LayoutMaxWidth), p.GetFloat(P.LayoutMaxHeight)),
                        IsTitleText = p.GetBool(P.IsTitleText) ?? false,
                        IsTitleDate = p.GetBool(P.IsTitleDate) ?? false,
                    };
                    ReadOutlineChildren(p.GetObjectIds(P.ElementChildNodes), outline.Children, 0, recursion);
                    return outline;
                }
                case J.OutlineElementNode:
                case J.OutlineGroup:
                {
                    var list = new List<OneNoteOutlineElement>();
                    ReadOutlineChildren(new[] { id }, list, depth, recursion);
                    return list.FirstOrDefault();
                }
                case J.RichTextOENode: return ReadRichText(obj, recursion);
                case J.ImageNode: return ReadImage(obj);
                case J.EmbeddedFileNode: return ReadEmbeddedFile(obj);
                case J.TableNode: return ReadTable(obj, recursion);
                case J.InkContainer: return ReadInk(obj, recursion);
                case J.IFrameNode:
                    return new OneNoteEmbeddedMedia
                    {
                        ObjectId = obj.Id,
                        Url = p.GetString(P.ImageEmbeddedUrl),
                        EmbedType = p.GetUInt32(P.ImageEmbedType),
                        Layout = new OneNoteLayout(p.GetFloat(P.OffsetFromParentHoriz), p.GetFloat(P.OffsetFromParentVert), p.GetFloat(P.LayoutMaxWidth), p.GetFloat(P.LayoutMaxHeight)),
                        LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                    };
                default:
                    return new OneNoteUnknownElement { ObjectId = obj.Id, JcId = obj.JcId, Object = obj };
            }
        }

        private void ReadOutlineChildren(IReadOnlyList<ExGuid> ids, List<OneNoteOutlineElement> into, int depth, int recursion)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var obj = Get(id);
                if (obj is null) continue;
                var p = obj.Properties;
                if (obj.JcId.Value == J.OutlineGroup)
                {
                    var group = new OneNoteOutlineElement
                    {
                        ObjectId = obj.Id,
                        IsGroup = true,
                        Depth = depth,
                        LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                    };
                    ReadOutlineChildren(p.GetObjectIds(P.ElementChildNodes), group.Children, depth + 1, recursion + 1);
                    into.Add(group);
                    counters.Clear();
                    continue;
                }
                if (obj.JcId.Value != J.OutlineElementNode)
                {
                    if (ReadElement(id, depth, recursion + 1) is { } other)
                        into.Add(new OneNoteOutlineElement { ObjectId = obj.Id, Depth = depth, Content = other });
                    continue;
                }

                var element = new OneNoteOutlineElement
                {
                    ObjectId = obj.Id,
                    Depth = depth,
                    LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                    CreatedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.CreationTimeStamp)),
                    Author = AuthorOf(p.GetObjectId(P.AuthorOriginal)),
                    LastModifiedBy = AuthorOf(p.GetObjectId(P.AuthorMostRecent)),
                    IsRightToLeft = p.GetBool(P.OutlineElementRTL) ?? false,
                };
                var contentIds = p.GetObjectIds(P.ContentChildNodes);
                if (contentIds.Count > 0) element.Content = ReadElement(contentIds[0], depth, recursion + 1);
                if (Get(p.GetObjectIds(P.ListNodes).FirstOrDefault()) is { } listNode)
                {
                    element.List = ReadListMarker(listNode, counters);
                }
                else
                {
                    counters.Clear();
                }
                ReadOutlineChildren(p.GetObjectIds(P.ElementChildNodes), element.Children, depth + 1, recursion + 1);
                into.Add(element);
            }
        }

        private static OneNoteListMarker ReadListMarker(StoreObject node, Dictionary<string, int> counters)
        {
            var p = node.Properties;
            var bytes = p.GetBytes(P.NumberListFormat) ?? Array.Empty<byte>();
            string raw = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
            string format = raw.Length > 0 && raw[0] <= raw.Length - 1 ? raw.Substring(1, raw[0]) : raw.TrimEnd('\0');
            var marker = new OneNoteListMarker
            {
                Format = format,
                Font = p.GetString(P.ListFont) ?? p.GetString(P.Font),
                Restart = p.GetUInt32(P.ListRestart) is { } r ? (int)r : null,
            };
            int at = format.IndexOf('�');
            if (at >= 0 && at + 1 < format.Length)
            {
                marker.IsNumbered = true;
                marker.NumberFormat = format[at + 1];
                int n = marker.Restart ?? (counters.TryGetValue(format, out var prev) ? prev + 1 : 1);
                counters[format] = n;
                marker.Number = n;
                marker.Text = format[..at] + FormatNumber(n, format[at + 1]) + format[(at + 2)..];
            }
            else
            {
                counters.Clear();
                marker.Text = format.Length == 0 ? "\u2022" : SymbolBullet(format, marker.Font);
            }
            return marker;
        }

        /// <summary>A bullet drawn from a symbol font, as the Unicode character it shows.</summary>
        private static string SymbolBullet(string format, string? font)
        {
            if (format.Length != 1 || font is null) return format;
            char c = format[0];
            if (font.StartsWith("Wingdings 3", StringComparison.OrdinalIgnoreCase)) return c switch { '}' => "\u25B8", 'u' => "\u25B6", _ => "\u25AA" };
            if (font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase))
                return c switch { '\u00A7' => "\u25AA", '\u00D8' => "\u27A2", '\u00FC' => "\u2714", 'v' => "\u2756", 'q' => "\u2751", 'n' => "\u25A0", 'l' => "\u25CF", 'o' => "\u25A1", _ => "\u25AA" };
            if (font.Equals("Symbol", StringComparison.OrdinalIgnoreCase)) return c == '\u00B7' ? "\u2022" : format;
            return format;
        }

        /// <summary>[MS-OSHARED] 2.2.1.3 MSONFC, the styles OneNote offers.</summary>
        private static string FormatNumber(int n, int msonfc) => msonfc switch
        {
            1 => ToRoman(n).ToUpperInvariant(),
            2 => ToRoman(n),
            3 => ToLetters(n).ToUpperInvariant(),
            4 => ToLetters(n),
            _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        private static string ToRoman(int n)
        {
            if (n <= 0 || n > 3999) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            (int, string)[] map = { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
            foreach (var (v, s) in map)
                while (n >= v) { sb.Append(s); n -= v; }
            return sb.ToString();
        }

        private static string ToLetters(int n)
        {
            if (n <= 0) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            while (n > 0)
            {
                n--;
                sb.Insert(0, (char)('a' + n % 26));
                n /= 26;
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- rich text

        private OneNoteRichText ReadRichText(StoreObject obj, int recursion)
        {
            var p = obj.Properties;
            var text = new OneNoteRichText
            {
                ObjectId = obj.Id,
                LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                IsTitleText = p.GetBool(P.IsTitleText) ?? false,
                IsTitleDate = p.GetBool(P.IsTitleDate) ?? false,
                IsTitleTime = p.GetBool(P.IsTitleTime) ?? false,
                IsRightToLeft = p.GetBool(P.ReadingOrderRTL) ?? false,
                LanguageId = p.GetUInt32(P.RichEditTextLangID),
                Alignment = p.GetUInt32(P.ParagraphAlignment) is { } a ? (int)a : null,
            };
            if (Get(p.GetObjectId(P.ParagraphStyle)) is { } paragraphStyle)
            {
                text.ParagraphStyle = ReadStyle(paragraphStyle);
                text.Alignment ??= paragraphStyle.Properties.GetUInt32(P.ParagraphAlignment) is { } pa ? (int)pa : null;
            }

            string raw = p.GetString(P.RichEditTextUnicode)
                ?? (p.GetBytes(P.TextExtendedAscii) is { } ascii ? DecodeAnsi(ascii) : "");
            var indices = p.GetUInt32Array(P.TextRunIndex).ToList();
            IReadOnlyList<ExGuid> formatting = p.GetObjectIds(P.TextRunFormatting);
            if (formatting.Count > indices.Count + 1 && formatting.Any(f => f.IsNil))
            {
                var trimmed = formatting.ToList();
                for (int i = 0; i < trimmed.Count && trimmed.Count > indices.Count + 1;)
                {
                    if (trimmed[i].IsNil) trimmed.RemoveAt(i); else i++;
                }
                formatting = trimmed;
            }
            var styles = formatting.Select(f => Get(f) is { } s ? ReadStyle(s) : OneNoteTextStyle.Default).ToList();
            var runData = p.GetPropertySets(P.TextRunData);

            if (formatting.Count == 0 || !formatting[0].IsNil) FixLeadingVerticalTab(ref raw, indices, styles);
            BuildRuns(text, raw, indices, styles, runData);
            ReadInlineInk(text, p, styles, runData, recursion);
            ReadNoteTags(p, text.NoteTags);
            return text;
        }

        private static string DecodeAnsi(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                if (b == 0) break;
                sb.Append(b < 0x80 || b >= 0xA0 ? (char)b : XRay.Content.Internal.Cfb.OleUtil.Cp1252ToChar(b));
            }
            return sb.ToString();
        }

        /// <summary>OneNote sometimes writes a paragraph that opens with U+000B and a first run
        /// boundary of 1 but formatting laid out as if neither were there; realign them.</summary>
        private static void FixLeadingVerticalTab(ref string text, List<uint> indices, List<OneNoteTextStyle> styles)
        {
            if (!text.StartsWith('\u000B') || indices.Count == 0 || indices[0] != 1) return;
            int extra = styles.Count - indices.Count;
            if (extra is not (0 or 1)) return;
            text = text[1..];
            indices.RemoveAt(0);
            for (int i = 0; i < indices.Count; i++) indices[i]--;
            if (extra == 1) styles.RemoveAt(styles.Count - 1);
        }

        private static void BuildRuns(OneNoteRichText text, string raw, List<uint> indices, List<OneNoteTextStyle> styles, IReadOnlyList<PropertySet> runData)
        {
            var bounds = new List<(int Start, int End, OneNoteTextStyle Style, PropertySet? Data)>();
            int start = 0;
            int runCount = Math.Max(1, indices.Count + 1);
            for (int i = 0; i < runCount; i++)
            {
                int end = i < indices.Count ? (int)Math.Min(indices[i], (uint)raw.Length) : raw.Length;
                if (end < start) end = start;
                var style = i < styles.Count ? styles[i] : (styles.Count > 0 ? styles[^1] : OneNoteTextStyle.Default);
                bounds.Add((start, end, style, i < runData.Count ? runData[i] : null));
                start = end;
            }

            var visible = new StringBuilder(raw.Length);
            string? pendingLink = null;
            int linkStart = -1;
            string? linkUrl = null;

            void CloseLink()
            {
                if (linkUrl is not null && linkStart >= 0 && visible.Length > linkStart)
                    text.Hyperlinks.Add(new OneNoteHyperlink(linkStart, visible.Length - linkStart, visible.ToString(linkStart, visible.Length - linkStart), linkUrl));
                linkUrl = null;
                linkStart = -1;
            }

            foreach (var (s, e, style, data) in bounds)
            {
                string segment = raw.Substring(s, e - s);
                if (style.IsHidden)
                {
                    int m = segment.IndexOf(HyperlinkMarker, StringComparison.Ordinal);
                    if (m >= 0)
                    {
                        int urlStart = m + HyperlinkMarker.Length;
                        int urlEnd = segment.IndexOf('"', urlStart);
                        CloseLink();
                        pendingLink = urlEnd > urlStart ? segment[urlStart..urlEnd] : segment[urlStart..];
                    }
                    continue;
                }
                if (segment.Length == 0) continue;

                if (style.IsHyperlink && pendingLink is not null)
                {
                    CloseLink();
                    linkUrl = pendingLink;
                    linkStart = visible.Length;
                    pendingLink = null;
                }
                else if (!style.IsHyperlink)
                {
                    CloseLink();
                    pendingLink = null;
                }

                string shown = segment.Replace('\u000B', '\n').Replace("\r\n", "\n").Replace('\r', '\n').Replace("\uFFFC", "");
                if (shown.Length == 0) continue;
                OneNoteMathObject? math = style.IsMath && data is not null ? ReadMath(data) : null;
                text.Runs.Add(new OneNoteTextRun(visible.Length, shown.Length, shown, style) { Hyperlink = style.IsHyperlink ? linkUrl : null, Math = math });
                visible.Append(shown);
            }
            CloseLink();
            text.Text = visible.ToString();
        }

        private static OneNoteMathObject? ReadMath(PropertySet data)
        {
            if (!data.Has(P.MathInlineObjectType) && !data.Has(P.MathInlineObjectChar)) return null;
            static char? C(ulong? v) => v is { } x and > 0 and < 0xD800 ? (char)x : null;
            return new OneNoteMathObject(
                data.GetUInt32(P.MathInlineObjectType) ?? 0,
                data.GetUInt32(P.MathInlineObjectCount) ?? 0,
                data.GetInteger(P.MathInlineObjectColumns) is { } col ? (byte)col : null,
                data.GetInteger(P.MathInlineObjectAlignment) is { } al ? (byte)al : null,
                C(data.GetInteger(P.MathInlineObjectChar)),
                C(data.GetInteger(P.MathInlineObjectChar1)),
                C(data.GetInteger(P.MathInlineObjectChar2)));
        }

        private void ReadInlineInk(OneNoteRichText text, PropertySet p, List<OneNoteTextStyle> styles, IReadOnlyList<PropertySet> runData, int recursion)
        {
            var objects = p.GetObjectIds(P.TextRunDataObject);
            int objectIndex = 0;
            for (int i = 0; i < runData.Count && i < styles.Count; i++)
            {
                var data = runData[i];
                bool embedded = data.Has(P.EmbeddedObjectType) || data.Has(P.EmbeddedInkStartX) || data.Has(P.EmbeddedInkSpaceWidth);
                if (!embedded) continue;
                uint? type = data.GetUInt32(P.EmbeddedObjectType);
                if (type is InkSpaceBlob or InkEndOfLineBlob)
                {
                    if (objectIndex < objects.Count && Get(objects[objectIndex]) is null) objectIndex++;
                    continue;
                }
                if (objectIndex >= objects.Count) break;
                var inkData = Get(objects[objectIndex++]);
                if (inkData is null) continue;
                var ink = new OneNoteInk
                {
                    ObjectId = inkData.Id,
                    Layout = new OneNoteLayout(data.GetFloat(P.EmbeddedInkOffsetHoriz), data.GetFloat(P.EmbeddedInkOffsetVert), data.GetFloat(P.EmbeddedInkWidth), data.GetFloat(P.EmbeddedInkHeight)),
                };
                ReadInkData(inkData, ink, null, null);
                text.InlineInk.Add(ink);
            }
        }

        private OneNoteTextStyle ReadStyle(StoreObject obj)
        {
            var p = obj.Properties;
            return new OneNoteTextStyle
            {
                Bold = p.GetBool(P.Bold) ?? false,
                Italic = p.GetBool(P.Italic) ?? false,
                Underline = p.GetBool(P.Underline) ?? false,
                Strikethrough = p.GetBool(P.Strikethrough) ?? false,
                Superscript = p.GetBool(P.Superscript) ?? false,
                Subscript = p.GetBool(P.Subscript) ?? false,
                Font = p.GetString(P.Font),
                FontSize = p.GetInteger(P.FontSize) is { } halfPoints ? halfPoints / 2f : null,
                FontColor = OneNoteColor.FromColorRef(p.GetUInt32(P.FontColor)),
                Highlight = OneNoteColor.FromColorRef(p.GetUInt32(P.Highlight)),
                LanguageId = p.GetUInt32(P.LanguageID),
                IsHyperlink = p.GetBool(P.Hyperlink) ?? false,
                IsHidden = p.GetBool(P.Hidden) ?? false,
                IsMath = p.GetBool(P.MathFormatting) ?? false,
                StyleId = p.GetString(P.ParagraphStyleId),
            };
        }

        private void ReadNoteTags(PropertySet p, List<OneNoteNoteTag> into)
        {
            foreach (var state in p.GetPropertySets(P.NoteTagStates))
            {
                var tag = new OneNoteNoteTag
                {
                    CreatedDateTime = OneNoteTime.FromTime32(state.GetInteger(P.NoteTagCreated)),
                    CompletedDateTime = OneNoteTime.FromTime32(state.GetInteger(P.NoteTagCompleted)),
                    DueDateTime = OneNoteTime.FromTime32(state.GetInteger(P.TaskTagDueDate)),
                };
                uint status = state.GetUInt32(P.ActionItemStatus) ?? 0;
                tag.IsCompleted = (status & 1) != 0;
                tag.IsDisabled = (status & 2) != 0;
                tag.IsTask = (status & 4) != 0;

                var definition = Get(state.GetObjectId(P.NoteTagDefinitionOid));
                var source = definition?.Properties ?? state;
                tag.Label = source.GetString(P.NoteTagLabel);
                tag.Shape = (int)(source.GetInteger(P.NoteTagShape) ?? state.GetInteger(P.NoteTagShape) ?? 0);
                tag.ActionItemType = source.GetInteger(P.ActionItemType) is { } t ? (int)(short)t : state.GetInteger(P.ActionItemType) is { } t2 ? (int)(short)t2 : null;
                tag.TextColor = OneNoteColor.FromColorRef(source.GetUInt32(P.NoteTagTextColor));
                tag.HighlightColor = OneNoteColor.FromColorRef(source.GetUInt32(P.NoteTagHighlightColor));
                if (tag.ActionItemType is >= 100 and <= 105) tag.IsTask = true;
                if (!tag.IsCheckable && !tag.IsTask) tag.IsCompleted = false;

                string? name = GraphNoteTags.FromLabel(tag.Label) ?? (tag.IsTask ? null : tag.Shape == 3 && tag.Label is null ? "to-do" : null);
                tag.GraphDataTag = name is null ? null : tag.IsCompleted && (tag.IsCheckable || tag.IsTask) ? name + ":completed" : name;
                into.Add(tag);
            }
        }

        // ------------------------------------------------------- images and attachments

        private OneNoteImage ReadImage(StoreObject obj)
        {
            var p = obj.Properties;
            var image = new OneNoteImage
            {
                ObjectId = obj.Id,
                LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                AltText = NullIfEmpty(p.GetString(P.ImageAltText)),
                FileName = NullIfEmpty(p.GetString(P.ImageFilename)),
                OcrText = NullIfEmpty(p.GetString(P.RichEditTextUnicode)),
                Hyperlink = NullIfEmpty(p.GetString(P.WzHyperlinkUrl)),
                Width = p.GetFloat(P.PictureWidth) ?? p.GetFloat(P.LayoutMaxWidth),
                Height = p.GetFloat(P.PictureHeight) ?? p.GetFloat(P.LayoutMaxHeight),
                Layout = new OneNoteLayout(p.GetFloat(P.OffsetFromParentHoriz), p.GetFloat(P.OffsetFromParentVert), p.GetFloat(P.LayoutMaxWidth), p.GetFloat(P.LayoutMaxHeight)),
                IsBackground = p.GetBool(P.IsBackground) ?? false,
                DisplayedPageNumber = p.GetUInt32(P.DisplayedPageNumber),
            };
            image.Data = Get(p.GetObjectId(P.PictureContainer))?.FileData;
            image.WebData = Get(p.GetObjectId(P.WebPictureContainer14))?.FileData;
            image.Extension = image.Data?.Extension ?? (image.FileName is { } fn ? NullIfEmpty(Path.GetExtension(fn)) : null);
            image.ContentType = OneNoteMedia.FromExtension(image.Extension);
            if (image.ContentType is null && image.Data?.Location == FileDataLocation.Embedded) image.ContentType = OneNoteMedia.Sniff(image.Data.ReadAll(), null);
            ReadNoteTags(p, image.NoteTags);
            return image;
        }

        private OneNoteEmbeddedFile ReadEmbeddedFile(StoreObject obj)
        {
            var p = obj.Properties;
            var file = new OneNoteEmbeddedFile
            {
                ObjectId = obj.Id,
                LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                FileName = NullIfEmpty(p.GetString(P.EmbeddedFileName)),
                SourcePath = NullIfEmpty(p.GetString(P.SourceFilepath)),
                RecordingType = p.GetUInt32(P.IRecordMedia),
                Layout = new OneNoteLayout(p.GetFloat(P.OffsetFromParentHoriz), p.GetFloat(P.OffsetFromParentVert), p.GetFloat(P.LayoutMaxWidth), p.GetFloat(P.LayoutMaxHeight)),
            };
            file.Data = Get(p.GetObjectId(P.EmbeddedFileContainer))?.FileData;
            file.Extension = file.FileName is { } fn && Path.GetExtension(fn) is { Length: > 0 } ext ? ext : file.Data?.Extension;
            file.ContentType = OneNoteMedia.FromExtension(file.Extension);
            if (Get(p.GetObjectId(P.PictureContainer))?.FileData is { } icon)
            {
                file.Icon = new OneNoteImage { ObjectId = obj.Id, Data = icon, Extension = icon.Extension, ContentType = OneNoteMedia.FromExtension(icon.Extension) };
            }
            ReadNoteTags(p, file.NoteTags);
            return file;
        }

        // ---------------------------------------------------------------------- tables

        private OneNoteTable ReadTable(StoreObject obj, int recursion)
        {
            var p = obj.Properties;
            var table = new OneNoteTable
            {
                ObjectId = obj.Id,
                LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                RowCount = (int)(p.GetUInt32(P.RowCount) ?? 0),
                ColumnCount = (int)(p.GetUInt32(P.ColumnCount) ?? 0),
                BordersVisible = p.GetBool(P.TableBordersVisible) ?? true,
            };
            if (p.GetBytes(P.TableColumnWidths) is { Length: > 0 } widths)
            {
                int n = widths[0];
                for (int i = 0; i < n && 1 + i * 4 + 4 <= widths.Length; i++) table.ColumnWidths.Add(BitConverter.ToSingle(widths, 1 + i * 4));
            }
            foreach (var rowId in p.GetObjectIds(P.ElementChildNodes))
            {
                var rowObj = Get(rowId);
                if (rowObj is null) continue;
                var row = new OneNoteTableRow { ObjectId = rowObj.Id };
                foreach (var cellId in rowObj.Properties.GetObjectIds(P.ElementChildNodes))
                {
                    var cellObj = Get(cellId);
                    if (cellObj is null) continue;
                    var cell = new OneNoteTableCell
                    {
                        ObjectId = cellObj.Id,
                        LastModifiedDateTime = OneNoteTime.FromTime32(cellObj.Properties.GetInteger(P.LastModifiedTime)),
                        Background = OneNoteColor.FromColorRef(cellObj.Properties.GetUInt32(P.CellShadingColor)),
                    };
                    ReadOutlineChildren(cellObj.Properties.GetObjectIds(P.ElementChildNodes), cell.Children, 0, recursion + 1);
                    row.Cells.Add(cell);
                }
                table.Rows.Add(row);
            }
            ReadNoteTags(p, table.NoteTags);
            return table;
        }

        // ------------------------------------------------------------------------- ink

        private static readonly Guid InkDimensionX = new("598A6A8F-52C0-4BA0-93AF-AF357411A561");
        private static readonly Guid InkDimensionY = new("B53F9F75-04E0-4498-A7EE-C30DBB5A9011");

        private OneNoteInk ReadInk(StoreObject obj, int recursion)
        {
            if (recursion > MaxDepth) throw new OneNoteFormatException("Ink is nested too deeply.");
            var p = obj.Properties;
            var ink = new OneNoteInk
            {
                ObjectId = obj.Id,
                LastModifiedDateTime = OneNoteTime.FromTime32(p.GetInteger(P.LastModifiedTime)),
                Layout = new OneNoteLayout(p.GetFloat(P.OffsetFromParentHoriz), p.GetFloat(P.OffsetFromParentVert), p.GetFloat(P.LayoutMaxWidth), p.GetFloat(P.LayoutMaxHeight)),
            };
            if (Get(p.GetObjectId(P.InkData)) is { } data)
            {
                ReadInkData(data, ink, p.GetFloat(P.InkScalingX), p.GetFloat(P.InkScalingY));
            }
            foreach (var childId in p.GetObjectIds(P.ContentChildNodes))
            {
                if (Get(childId) is { } child && child.JcId.Value == J.InkContainer) ink.Groups.Add(ReadInk(child, recursion + 1));
            }
            if (ink.BoundingBox is null && ink.Groups.Count > 0)
            {
                var boxes = ink.Groups.Select(g => g.BoundingBox).Where(b => b is not null).Select(b => b!.Value).ToList();
                if (boxes.Count > 0)
                {
                    float x0 = boxes.Min(b => b.X), y0 = boxes.Min(b => b.Y);
                    float x1 = boxes.Max(b => b.X + b.Width), y1 = boxes.Max(b => b.Y + b.Height);
                    ink.BoundingBox = (x0, y0, x1 - x0, y1 - y0);
                }
            }
            return ink;
        }

        private void ReadInkData(StoreObject data, OneNoteInk ink, float? scaleX, float? scaleY)
        {
            float sx = scaleX ?? 1f, sy = scaleY ?? 1f;
            foreach (var strokeId in data.Properties.GetObjectIds(P.InkStrokes))
            {
                var strokeObj = Get(strokeId);
                if (strokeObj is null) continue;
                var stroke = new OneNoteInkStroke
                {
                    ObjectId = strokeObj.Id,
                    Bias = strokeObj.Properties.GetInteger(P.InkBias) is { } b ? (int)b : null,
                };
                var props = Get(strokeObj.Properties.GetObjectId(P.InkStrokeProperties))?.Properties;
                var dims = new List<Guid>();
                if (props is not null)
                {
                    stroke.Width = props.GetFloat(P.InkWidth) ?? 0;
                    stroke.Height = props.GetFloat(P.InkHeight) ?? 0;
                    stroke.Color = OneNoteColor.FromColorRef(props.GetUInt32(P.InkColor));
                    stroke.Transparency = props.GetInteger(P.InkTransparency) is { } t ? (byte)t : null;
                    stroke.PenTip = props.GetInteger(P.InkPenTip) is { } tip ? (byte)tip : null;
                    if (props.GetBytes(P.InkDimensions) is { } d)
                        for (int i = 0; i + 32 <= d.Length; i += 32) dims.Add(new Guid(d.AsSpan(i, 16)));
                }
                if (strokeObj.Properties.GetBytes(P.InkPath) is { } path)
                {
                    try
                    {
                        var values = DecodeMultiByteSigned(path);
                        int ix = dims.IndexOf(InkDimensionX), iy = dims.IndexOf(InkDimensionY);
                        if (ix < 0) ix = 0;
                        if (iy < 0) iy = 1;
                        int stride = values.Count / Math.Max(2, dims.Count);
                        if (Math.Max(ix, iy) < Math.Max(2, dims.Count))
                        {
                            long x = 0, y = 0;
                            for (int i = 0; i < stride; i++)
                            {
                                x += values[ix * stride + i];
                                y += values[iy * stride + i];
                                stroke.Points.Add((x * sx, y * sy));
                            }
                        }
                    }
                    catch (OneNoteFormatException ex)
                    {
                        _warnings.Add($"Ink stroke {strokeObj.Id} has an unreadable path: {ex.Message}");
                    }
                }
                ink.Strokes.Add(stroke);
            }

            if (ink.Strokes.SelectMany(s => s.Points).ToList() is { Count: > 0 } pts)
            {
                float pad = ink.Strokes.Max(s => Math.Max(s.Width, s.Height)) / 2;
                float x0 = pts.Min(q => q.X) - pad, y0 = pts.Min(q => q.Y) - pad;
                ink.BoundingBox = (x0, y0, pts.Max(q => q.X) + pad - x0, pts.Max(q => q.Y) + pad - y0);
            }
            else if (data.Properties.GetBytes(P.InkBoundingBox) is { Length: 16 } bb)
            {
                int x0 = BitConverter.ToInt32(bb, 0), y0 = BitConverter.ToInt32(bb, 4), x1 = BitConverter.ToInt32(bb, 8), y1 = BitConverter.ToInt32(bb, 12);
                ink.BoundingBox = (x0 * sx, y0 * sy, (x1 - x0) * sx, (y1 - y0) * sy);
            }
        }

        /// <summary>The variable-length integers of an ink path: 7 bits a byte, low first, the
        /// sign in bit 0 of the decoded value; the first value is the count. Each dimension's
        /// values are deltas from the previous point.</summary>
        private static List<long> DecodeMultiByteSigned(byte[] data)
        {
            int pos = 0;
            ulong Next()
            {
                ulong value = 0;
                for (int shift = 0; ; shift += 7)
                {
                    if (pos >= data.Length) throw new OneNoteFormatException("ink path ends mid-value");
                    if (shift > 63) throw new OneNoteFormatException("ink path value overflows");
                    byte b = data[pos++];
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return value;
                }
            }
            ulong count = Next() >> 1;
            if (count > (ulong)data.Length) throw new OneNoteFormatException("ink path declares more values than it holds");
            var result = new List<long>((int)count);
            for (ulong i = 0; i < count; i++)
            {
                ulong v = Next();
                long magnitude = (long)(v >> 1);
                result.Add((v & 1) != 0 ? -magnitude : magnitude);
            }
            return result;
        }

        private OneNoteInkRecognition ReadRecognition(StoreObject root)
        {
            var recognition = new OneNoteInkRecognition();
            void Walk(StoreObject node, List<OneNoteRecognizedWord>? line, int depth)
            {
                if (depth > MaxDepth) return;
                if (node.JcId.Value == J.RecognizedTextWord)
                {
                    var word = new OneNoteRecognizedWord { LanguageId = node.Properties.GetUInt32(P.RecognizedTextLanguageId) };
                    if (node.Properties.GetBytes(P.RecognizedText) is { } b)
                        word.Alternatives.AddRange(Encoding.Unicode.GetString(b, 0, b.Length & ~1).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                    if (line is null)
                    {
                        line = new List<OneNoteRecognizedWord>();
                        recognition.Lines.Add(line);
                    }
                    line.Add(word);
                    return;
                }
                if (node.JcId.Value == J.RecognizedTextLine)
                {
                    line = new List<OneNoteRecognizedWord>();
                    recognition.Lines.Add(line);
                }
                foreach (var childId in node.Properties.GetObjectIds(P.RecognizedTextChildNodes))
                    if (Get(childId) is { } child) Walk(child, line, depth + 1);
            }
            Walk(root, null, 0);
            recognition.Lines.RemoveAll(l => l.Count == 0);
            return recognition;
        }

        // --------------------------------------------------------------------- helpers

        private string? AuthorOf(ExGuid? id) => Get(id)?.Properties.GetString(P.Author);

        private static bool IsSet(PropertySet p, uint id) => p.Get(id) switch
        {
            null => false,
            { Type: PropertyType.Bool } v => v.Bool,
            { Type: PropertyType.NoData } => true,
            { Bytes: { } b } => b.Any(x => x != 0),
            { } v => v.Integer != 0,
        };

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}

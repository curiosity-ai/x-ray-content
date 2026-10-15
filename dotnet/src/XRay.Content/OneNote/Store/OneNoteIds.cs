// [MS-ONE] 2.1.12 (property ids) and 2.1.13 (property-set JCIDs); [MS-ONESTORE] 2.7.2 and
// 2.7.6 (the ids only the FSSHTTPB encoding uses). The ink, math, embedded-object and
// handwriting-recognition ids are undocumented; their values are the ones OneNote writes.

using System.Reflection;

namespace XRay.Content.OneNote.Store;

/// <summary>Property ids, as [MS-ONE] tabulates them (type bits included, bool bit excluded).
/// <see cref="PropertySet"/> lookups compare only the 26-bit identity.</summary>
public static class OneNotePropertyNames
{
    public const uint LayoutTightLayout = 0x08001C00;
    public const uint PageWidth = 0x14001C01;
    public const uint PageHeight = 0x14001C02;
    public const uint OutlineElementChildLevel = 0x0C001C03;
    public const uint Bold = 0x08001C04;
    public const uint Italic = 0x08001C05;
    public const uint Underline = 0x08001C06;
    public const uint Strikethrough = 0x08001C07;
    public const uint Superscript = 0x08001C08;
    public const uint Subscript = 0x08001C09;
    public const uint Font = 0x1C001C0A;
    public const uint FontSize = 0x10001C0B;
    public const uint FontColor = 0x14001C0C;
    public const uint Highlight = 0x14001C0D;
    public const uint RgOutlineIndentDistance = 0x1C001C12;
    public const uint BodyTextAlignment = 0x0C001C13;
    public const uint OffsetFromParentHoriz = 0x14001C14;
    public const uint OffsetFromParentVert = 0x14001C15;
    public const uint NumberListFormat = 0x1C001C1A;
    public const uint LayoutMaxWidth = 0x14001C1B;
    public const uint LayoutMaxHeight = 0x14001C1C;
    public const uint ContentChildNodes = 0x24001C1F;
    public const uint ElementChildNodes = 0x24001C20;
    public const uint RichEditTextUnicode = 0x1C001C22;
    public const uint ListNodes = 0x24001C26;
    public const uint EmbeddedInkSpaceWidth = 0x14001C27;
    public const uint EmbeddedInkSpaceHeight = 0x14001C28;
    public const uint NotebookManagementEntityGuid = 0x1C001C30;
    public const uint OutlineElementRTL = 0x08001C34;
    public const uint LanguageID = 0x14001C3B;
    public const uint LayoutAlignmentInParent = 0x14001C3E;
    public const uint PictureContainer = 0x20001C3F;
    public const uint InkScalingX = 0x14001C46;
    public const uint InkScalingY = 0x14001C47;
    public const uint PageMarginTop = 0x14001C4C;
    public const uint PageMarginBottom = 0x14001C4D;
    public const uint PageMarginLeft = 0x14001C4E;
    public const uint PageMarginRight = 0x14001C4F;
    public const uint ListFont = 0x1C001C52;
    public const uint TopologyCreationTimeStamp = 0x18001C65;
    public const uint LayoutAlignmentSelf = 0x14001C84;
    public const uint IsTitleTime = 0x08001C87;
    public const uint IsBoilerText = 0x08001C88;
    public const uint PageSize = 0x14001C8B;
    public const uint PortraitPage = 0x08001C8E;
    public const uint EnforceOutlineStructure = 0x08001C91;
    public const uint EditRootRTL = 0x08001C92;
    public const uint CannotBeSelected = 0x08001CB2;
    public const uint IsTitleText = 0x08001CB4;
    public const uint IsTitleDate = 0x08001CB5;
    public const uint ListRestart = 0x14001CB7;
    public const uint NotebookElementOrderingID = 0x14001CB9;
    public const uint IsLayoutSizeSetByUser = 0x08001CBD;
    public const uint NotebookColor = 0x14001CBE;
    public const uint ListSpacingMu = 0x14001CCB;
    public const uint LayoutOutlineReservedWidth = 0x14001CDB;
    public const uint LayoutResolveChildCollisions = 0x08001CDC;
    public const uint IsReadOnly = 0x08001CDE;
    public const uint LayoutMinimumOutlineWidth = 0x14001CEC;
    public const uint LayoutCollisionPriority = 0x14001CF1;
    public const uint CachedTitleString = 0x1C001CF3;
    public const uint TOCEntryIndex_OidIndex = 0x24001CF6;
    public const uint DescendantsCannotBeMoved = 0x08001CF9;
    public const uint RichEditTextLangID = 0x10001CFE;
    public const uint LayoutTightAlignment = 0x08001CFF;
    public const uint Charset = 0x0C001D01;
    public const uint CreationTimeStamp = 0x14001D09;
    public const uint Deletable = 0x08001D0C;
    public const uint ListMSAAIndex = 0x10001D0E;
    public const uint PageMarginOriginX = 0x14001D0F;
    public const uint PageMarginOriginY = 0x14001D10;
    public const uint IsBackground = 0x08001D13;
    public const uint IRecordMedia = 0x14001D24;
    public const uint CachedTitleStringFromPage = 0x1C001D3C;
    public const uint RowCount = 0x14001D57;
    public const uint ColumnCount = 0x14001D58;
    public const uint TableBordersVisible = 0x08001D5E;
    public const uint StructureElementChildNodes = 0x24001D5F;
    public const uint ChildGraphSpaceElementNodes = 0x2C001D63;
    public const uint TableColumnWidths = 0x1C001D66;
    public const uint FolderChildFilename = 0x1C001D6B;
    public const uint Author = 0x1C001D75;
    public const uint LastModifiedTimeStamp = 0x18001D77;
    public const uint AuthorOriginal = 0x20001D78;
    public const uint AuthorMostRecent = 0x20001D79;
    public const uint LastModifiedTime = 0x14001D7A;
    public const uint IsConflictPage = 0x08001D7C;
    public const uint TableColumnsLocked = 0x1C001D7D;
    public const uint SchemaRevisionInOrderToRead = 0x14001D82;
    public const uint FileNameCRC = 0x14001D93;
    public const uint FileIdentityGuid = 0x1C001D94;
    public const uint FileAncestorIdentityGuid = 0x1C001D95;
    public const uint IsConflictObjectForRender = 0x08001D96;
    public const uint FileLastCodeVersionThatWroteToIt = 0x14001D99;
    public const uint EmbeddedFileContainer = 0x20001D9B;
    public const uint EmbeddedFileName = 0x1C001D9C;
    public const uint SourceFilepath = 0x1C001D9D;
    public const uint ConflictingUserName = 0x1C001D9E;
    public const uint ImageFilename = 0x1C001DD7;
    public const uint IsConflictObjectForSelection = 0x08001DDB;
    public const uint IsDeletedGraphSpaceContent = 0x00001DE9;
    public const uint PageLevel = 0x14001DFF;
    public const uint TextRunIndex = 0x1C001E12;
    public const uint TextRunFormatting = 0x24001E13;
    public const uint Hyperlink = 0x08001E14;
    public const uint UnderlineType = 0x0C001E15;
    public const uint Hidden = 0x08001E16;
    public const uint HyperlinkProtected = 0x08001E19;
    public const uint EnableHistory = 0x08001E1E;
    public const uint WzHyperlinkUrl = 0x1C001E20;
    public const uint TextRunIsEmbeddedObject = 0x08001E22;
    public const uint CellShadingColor = 0x14001E26;
    public const uint ImageAltText = 0x1C001E58;
    public const uint MathFormatting = 0x08003401;
    public const uint InkStrokeProperties = 0x20003409;
    public const uint InkDimensions = 0x1C00340A;
    public const uint InkPath = 0x1C00340B;
    public const uint InkHeight = 0x1400340C;
    public const uint InkWidth = 0x1400340D;
    public const uint InkAntialiased = 0x0800340E;
    public const uint InkColor = 0x1400340F;
    public const uint InkFitToCurve = 0x08003410;
    public const uint InkIgnorePressure = 0x08003411;
    public const uint InkPenTip = 0x0C003412;
    public const uint InkRasterOperation = 0x0C003413;
    public const uint InkTransparency = 0x0C003414;
    public const uint InkData = 0x20003415;
    public const uint InkStrokes = 0x24003416;
    public const uint InkBoundingBox = 0x1C003418;
    public const uint InkBias = 0x0C00341C;
    public const uint FileDataObjectExtension = 0x1C003424;
    public const uint ParagraphStyle = 0x2000342C;
    public const uint ParagraphSpaceBefore = 0x1400342E;
    public const uint ParagraphSpaceAfter = 0x1400342F;
    public const uint ParagraphLineSpacingExact = 0x14003430;
    public const uint FileDataObjectInvalidData = 0x0800343D;
    public const uint FileDataObjectGuid = 0x1C00343E;
    public const uint MetaDataObjectsAboveGraphSpace = 0x24003442;
    public const uint MathInlineObjectType = 0x1400344F;
    public const uint MathInlineObjectCount = 0x14003450;
    public const uint MathInlineObjectColumns = 0x0C003451;
    public const uint MathInlineObjectAlignment = 0x0C003452;
    public const uint MathInlineObjectChar = 0x10003453;
    public const uint MathInlineObjectChar1 = 0x10003454;
    public const uint MathInlineObjectChar2 = 0x10003455;
    public const uint EmbeddedObjectType = 0x14003457;
    public const uint TextRunDataObject = 0x24003458;
    public const uint ParagraphStyleId = 0x1C00345A;
    public const uint HasVersionPages = 0x08003462;
    public const uint ActionItemType = 0x10003463;
    public const uint NoteTagShape = 0x10003464;
    public const uint NoteTagHighlightColor = 0x14003465;
    public const uint NoteTagTextColor = 0x14003466;
    public const uint NoteTagPropertyStatus = 0x14003467;
    public const uint NoteTagLabel = 0x1C003468;
    public const uint TaskTagDueDate = 0x1400346B;
    public const uint NoteTagCreated = 0x1400346E;
    public const uint NoteTagCompleted = 0x1400346F;
    public const uint ActionItemStatus = 0x10003470;
    public const uint ActionItemSchemaVersion = 0x0C003473;
    public const uint ReadingOrderRTL = 0x08003476;
    public const uint ParagraphAlignment = 0x0C003477;
    public const uint VersionHistoryGraphSpaceContextNodes = 0x3400347B;
    /// <summary>[MS-ONE] 2.2.87 lists no id; OneNote writes the same id as VersionHistoryGraphSpaceContextNodes.</summary>
    public const uint VersionContextNodes = 0x3400347B;
    public const uint DisplayedPageNumber = 0x14003480;
    public const uint NoteTagDefinitionOid = 0x20003488;
    public const uint NoteTagStates = 0x40003489;
    public const uint NextStyle = 0x1C00348A;
    public const uint SchemaRevisionInOrderToWrite = 0x1400348B;
    public const uint TextExtendedAscii = 0x1C003498;
    public const uint TextRunData = 0x40003499;
    public const uint SectionDisplayName = 0x1C00349B;
    public const uint EmbeddedInkStartX = 0x1400349E;
    public const uint EmbeddedInkStartY = 0x1400349F;
    public const uint EmbeddedInkWidth = 0x140034A0;
    public const uint EmbeddedInkHeight = 0x140034A1;
    public const uint EmbeddedInkOffsetHoriz = 0x140034A2;
    public const uint EmbeddedInkOffsetVert = 0x140034A3;
    public const uint WebPictureContainer14 = 0x200034C8;
    public const uint ImageUploadState = 0x140034CB;
    public const uint PictureWidth = 0x140034CD;
    public const uint PictureHeight = 0x140034CE;
    public const uint PageRecognizedTextContainer = 0x200035D7;
    public const uint RecognizedTextChildNodes = 0x240035D9;
    public const uint RecognizedText = 0x1C0035DA;
    public const uint RecognizedTextLanguageId = 0x100035DC;
    public const uint RecognizedTextStrokeReferences = 0x1C0035DF;
    public const uint ImageEmbedType = 0x140035F2;
    public const uint ImageEmbeddedUrl = 0x1C0035F3;

    private static readonly Lazy<Dictionary<uint, string>> s_names = new(() =>
    {
        var map = new Dictionary<uint, string>();
        foreach (var f in typeof(OneNotePropertyNames).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.IsLiteral && f.FieldType == typeof(uint))
                map.TryAdd((uint)f.GetRawConstantValue()! & 0x3FFFFFF, f.Name);
        return map;
    });

    /// <summary>The [MS-ONE] name of a property, or null when it is not one this reader knows.</summary>
    public static string? NameOf(uint propertyId) => s_names.Value.TryGetValue(propertyId & 0x3FFFFFF, out var n) ? n : null;
}

/// <summary>JCIDs of the property sets and file data objects [MS-ONE] defines.</summary>
public static class OneNoteJcids
{
    public const uint PersistablePropertyContainerForTOC = 0x00020001;
    public const uint SectionNode = 0x00060007;
    public const uint PageSeriesNode = 0x00060008;
    public const uint PageNode = 0x0006000B;
    public const uint OutlineNode = 0x0006000C;
    public const uint OutlineElementNode = 0x0006000D;
    public const uint RichTextOENode = 0x0006000E;
    public const uint ImageNode = 0x00060011;
    public const uint NumberListNode = 0x00060012;
    public const uint InkContainer = 0x00060014;
    public const uint OutlineGroup = 0x00060019;
    public const uint TableNode = 0x00060022;
    public const uint TableRowNode = 0x00060023;
    public const uint TableCellNode = 0x00060024;
    public const uint TitleNode = 0x0006002C;
    public const uint PageMetaData = 0x00020030;
    public const uint SectionMetaData = 0x00020031;
    public const uint EmbeddedFileNode = 0x00060035;
    public const uint EmbeddedFileContainer = 0x00080036;
    public const uint PageManifestNode = 0x00060037;
    public const uint ConflictPageMetaData = 0x00020038;
    public const uint PictureContainer14 = 0x00080039;
    public const uint XpsContainer = 0x0008003A;
    public const uint InkDataNode = 0x0002003B;
    public const uint VersionHistoryContent = 0x0006003C;
    public const uint VersionProxy = 0x0006003D;
    public const uint NoteTagSharedDefinitionContainer = 0x00120043;
    public const uint RevisionMetaData = 0x00020044;
    public const uint VersionHistoryMetaData = 0x00020046;
    public const uint InkStrokeNode = 0x00020047;
    public const uint StrokePropertiesNode = 0x00120048;
    public const uint ParagraphStyleObject = 0x0012004D;
    public const uint ReadOnlyPersistablePropertyContainerForAuthor = 0x00120001;
    public const uint RecognizedTextRoot = 0x00020054;
    public const uint RecognizedTextLine = 0x00020055;
    public const uint RecognizedTextBlock = 0x00020056;
    public const uint RecognizedTextWord = 0x00020057;
    public const uint IFrameNode = 0x00060058;

    private static readonly Lazy<Dictionary<uint, string>> s_names = new(() =>
    {
        var map = new Dictionary<uint, string>();
        foreach (var f in typeof(OneNoteJcids).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.IsLiteral && f.FieldType == typeof(uint))
                map.TryAdd((uint)f.GetRawConstantValue()!, "jcid" + f.Name);
        return map;
    });

    public static string? NameOf(JcId jcid) => s_names.Value.TryGetValue(jcid.Value, out var n) ? n : null;
}

/// <summary>Well-known GUIDs of the two encodings.</summary>
public static class OneNoteGuids
{
    public static readonly Guid SectionFileType = new("7B5C52E4-D88C-4DA7-AEB1-5378D02996D3");
    public static readonly Guid TocFileType = new("43FF2FA1-EFD9-4C76-9EE2-10EA5722765F");
    public static readonly Guid RevisionStoreFormat = new("109ADD3F-911B-49F5-A5D0-1791EDC8AED8");
    public static readonly Guid PackageStoreFormat = new("638DE92F-A6D4-4BC1-9A36-B3FC2511A5B7");
    public static readonly Guid SectionCellSchema = new("1F937CB4-B26F-445F-B9F8-17E20160E461");
    public static readonly Guid TocCellSchema = new("E4DBFD38-E5C7-408B-A8A1-0E7B421E1F5F");

    /// <summary>[MS-ONE] 2.1.18: the context that labels a page's version-history revision.</summary>
    public static readonly ExGuid VersionHistoryContext = new(new Guid("7111497F-1B6B-4209-9491-C98B04CF4C5A"), 1);

    /// <summary>[MS-ONE] 2.2.81: XORed into a page node's GUID to name its metadata object.</summary>
    public static readonly Guid MetadataSalt = new("22A8C031-3600-42EE-B714-D7ACDA2435E8");
}

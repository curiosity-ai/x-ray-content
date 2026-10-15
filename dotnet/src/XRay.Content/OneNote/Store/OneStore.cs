// The encoding-neutral object model shared by the two [MS-ONESTORE] encodings: the revision
// store (2.3-2.6, OneNote desktop files) and the FSSHTTPB alternative packaging (2.7-2.8, the
// files OneDrive and SharePoint store and serve).

namespace XRay.Content.OneNote.Store;

/// <summary>Which of the two [MS-ONESTORE] encodings a file uses.</summary>
public enum OneStoreEncoding
{
    /// <summary>[MS-ONESTORE] 2.3: the revision store OneNote desktop writes.</summary>
    RevisionStore,

    /// <summary>[MS-ONESTORE] 2.8: the FSSHTTPB packaging OneDrive and SharePoint store.</summary>
    Fsshttpb,
}

/// <summary>Whether a file is a section or a notebook's table of contents.</summary>
public enum OneStoreFileKind
{
    Section,
    TableOfContents,
}

/// <summary>The well-known revision roles of [MS-ONE] 2.1.8.</summary>
public static class RevisionRoles
{
    public const uint DefaultContent = 1;
    public const uint Metadata = 2;
    public const uint EncryptionKey = 3;
    public const uint VersionMetadata = 4;
}

/// <summary>Where the bytes of a file data object live ([MS-ONESTORE] 2.5.27).</summary>
public enum FileDataLocation
{
    /// <summary>Inside the file: a FileDataStoreObject, or an FSSHTTPB object data BLOB.</summary>
    Embedded,

    /// <summary>A <c>.onebin</c> file in the section's <c>_onefiles</c> folder.</summary>
    External,

    /// <summary>The reference is <c>&lt;invfdo&gt;</c>: OneNote recorded that the data is lost.</summary>
    Invalid,

    /// <summary>The data was referenced but could not be found.</summary>
    Missing,
}

/// <summary>The payload of a file data object (an image, an attachment, an ink or XPS blob).</summary>
public sealed class FileDataReference
{
    private readonly Func<byte[]?>? _load;

    internal FileDataReference(FileDataLocation location, string? reference, Guid? id, string? extension, long? length, Func<byte[]?>? load)
    {
        Location = location;
        Reference = reference;
        Id = id;
        Extension = extension;
        Length = length;
        _load = load;
    }

    public FileDataLocation Location { get; }

    /// <summary>The raw <c>FileDataReference</c> string, when the encoding carries one.</summary>
    public string? Reference { get; }

    /// <summary>The data object's identity: the <c>&lt;ifndf&gt;</c> GUID, or the name of the
    /// external <c>.onebin</c> file.</summary>
    public Guid? Id { get; }

    /// <summary>The file extension OneNote recorded for the data, including the dot.</summary>
    public string? Extension { get; }

    /// <summary>The size of the data in bytes, when known without reading it.</summary>
    public long? Length { get; }

    /// <summary>The name of the external file for <see cref="FileDataLocation.External"/>.</summary>
    public string? ExternalFileName => Location == FileDataLocation.External && Reference is { } r && r.StartsWith("<file>", StringComparison.Ordinal) ? r[6..] : null;

    /// <summary>Reads the bytes, or returns null when they are not available.</summary>
    public byte[]? ReadAll() => _load?.Invoke();
}

/// <summary>An object of an object space ([MS-ONESTORE] 2.1.5): a typed property set, and for
/// file data objects a payload.</summary>
public sealed class StoreObject
{
    internal StoreObject(ExGuid id, JcId jcid, PropertySet properties, FileDataReference? fileData)
    {
        Id = id;
        JcId = jcid;
        Properties = properties;
        FileData = fileData;
    }

    public ExGuid Id { get; }
    public JcId JcId { get; }
    public PropertySet Properties { get; }
    public FileDataReference? FileData { get; }

    public override string ToString() => $"{OneNoteJcids.NameOf(JcId) ?? JcId.ToString()} {Id}";
}

/// <summary>One revision of an object space: the root objects by role, and every object
/// reachable in that state (dependency revisions already folded in).</summary>
public sealed class StoreRevision
{
    private readonly Dictionary<ExGuid, StoreObject> _objects;
    private readonly Dictionary<uint, ExGuid> _roots;

    internal StoreRevision(ExGuid id, ExGuid context, Dictionary<uint, ExGuid> roots, Dictionary<ExGuid, StoreObject> objects, bool isEncrypted)
    {
        Id = id;
        Context = context;
        _roots = roots;
        _objects = objects;
        IsEncrypted = isEncrypted;
    }

    public ExGuid Id { get; }
    public ExGuid Context { get; }

    /// <summary>True when the revision's property sets are encrypted (a password-protected
    /// section); its objects are then present but carry no properties.</summary>
    public bool IsEncrypted { get; }

    public IReadOnlyDictionary<uint, ExGuid> Roots => _roots;
    public IReadOnlyCollection<StoreObject> Objects => _objects.Values;

    public StoreObject? GetObject(ExGuid id) => _objects.GetValueOrDefault(id);

    public StoreObject? GetRoot(uint role) => _roots.TryGetValue(role, out var id) ? GetObject(id) : null;
}

/// <summary>An object space ([MS-ONESTORE] 2.1.4). Its revisions are labelled by (context,
/// role) pairs; the one a reader normally wants is the default context's content role.</summary>
public sealed class StoreObjectSpace
{
    private readonly Func<ExGuid, uint, StoreRevision?> _resolve;
    private readonly Dictionary<(ExGuid, uint), StoreRevision?> _cache = new();
    private readonly object _gate = new();

    internal StoreObjectSpace(ExGuid id, IReadOnlyList<ExGuid> contexts, Func<ExGuid, uint, StoreRevision?> resolve)
    {
        Id = id;
        Contexts = contexts;
        _resolve = resolve;
    }

    public ExGuid Id { get; }

    /// <summary>Every context some revision of this object space is labelled with.</summary>
    public IReadOnlyList<ExGuid> Contexts { get; }

    /// <summary>The current revision for a context and role, or null when none is labelled so.</summary>
    public StoreRevision? GetRevision(ExGuid context, uint role = RevisionRoles.DefaultContent)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((context, role), out var cached)) return cached;
            var r = _resolve(context, role);
            _cache[(context, role)] = r;
            return r;
        }
    }

    public StoreRevision? Current => GetRevision(ExGuid.Nil);
}

/// <summary>Options for reading a store.</summary>
public sealed class OneStoreReadOptions
{
    /// <summary>Resolves a <c>&lt;file&gt;</c> reference to the bytes of a <c>.onebin</c> in the
    /// section's <c>_onefiles</c> folder. The argument is the file name.</summary>
    public Func<string, byte[]?>? ExternalFileResolver { get; init; }

    /// <summary>Upper bound on the number of objects read from one file; guards against a
    /// crafted file that declares millions of them.</summary>
    public int MaxObjects { get; init; } = 2_000_000;
}

/// <summary>A parsed <c>.one</c> or <c>.onetoc2</c> file, in either encoding.</summary>
public sealed class OneStore
{
    internal OneStore(
        OneStoreEncoding encoding,
        OneStoreFileKind kind,
        Guid fileGuid,
        Guid ancestorGuid,
        uint? fileNameCrc,
        uint? lastCodeVersion,
        StoreObjectSpace root,
        IReadOnlyDictionary<ExGuid, StoreObjectSpace> objectSpaces,
        IReadOnlyList<string> warnings)
    {
        Encoding = encoding;
        Kind = kind;
        FileGuid = fileGuid;
        AncestorGuid = ancestorGuid;
        FileNameCrc = fileNameCrc;
        LastCodeVersion = lastCodeVersion;
        Root = root;
        ObjectSpaces = objectSpaces;
        Warnings = warnings;
    }

    public OneStoreEncoding Encoding { get; }
    public OneStoreFileKind Kind { get; }

    /// <summary>The file's identity (<c>Header.guidFile</c>, or the FSSHTTPB header cell's
    /// FileIdentityGuid). For a section this is the GUID OneNote links address it by.</summary>
    public Guid FileGuid { get; }

    /// <summary>The <c>guidFile</c> of the notebook's table of contents, or empty.</summary>
    public Guid AncestorGuid { get; }

    /// <summary>The CRC of the file's name as OneNote last saved it ([MS-ONESTORE] 2.3.1).</summary>
    public uint? FileNameCrc { get; }

    public uint? LastCodeVersion { get; }

    /// <summary>The root object space: the section, or the notebook's table of contents.</summary>
    public StoreObjectSpace Root { get; }

    public IReadOnlyDictionary<ExGuid, StoreObjectSpace> ObjectSpaces { get; }

    /// <summary>Non-fatal problems met while reading.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public StoreObjectSpace? GetObjectSpace(ExGuid id) => ObjectSpaces.GetValueOrDefault(id);

    /// <summary>Reads a store, detecting the encoding from the header.</summary>
    public static OneStore Read(byte[] data, OneStoreReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new OneStoreReadOptions();
        if (data.Length < 64) throw new OneNoteFormatException("The file is too short to be a OneNote file.");
        var fileType = new Guid(data.AsSpan(0, 16));
        if (fileType != OneNoteGuids.SectionFileType && fileType != OneNoteGuids.TocFileType)
            throw new OneNoteFormatException($"Unknown OneNote file type {fileType:B}.");
        var format = new Guid(data.AsSpan(48, 16));
        if (format == OneNoteGuids.RevisionStoreFormat) return RevisionStoreReader.Read(data, options);
        if (format == OneNoteGuids.PackageStoreFormat) return FsshttpbPackageReader.Read(data, 0, options);
        throw new OneNoteFormatException($"Unknown OneNote file format {format:B}.");
    }

    /// <summary>True when the bytes start like a <c>.one</c> or <c>.onetoc2</c> file.</summary>
    public static bool IsOneStore(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64) return false;
        var fileType = new Guid(data[..16]);
        var format = new Guid(data.Slice(48, 16));
        return (fileType == OneNoteGuids.SectionFileType || fileType == OneNoteGuids.TocFileType)
            && (format == OneNoteGuids.RevisionStoreFormat || format == OneNoteGuids.PackageStoreFormat);
    }
}

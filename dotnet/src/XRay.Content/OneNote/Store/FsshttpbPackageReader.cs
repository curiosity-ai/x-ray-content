// [MS-ONESTORE] 2.7 and 2.8: a revision store transmitted, and stored, as an [MS-FSSHTTPB]
// data element package. This is the format OneDrive and SharePoint keep .one and .onetoc2
// files in, and the bytes the Graph drive API returns for them. [MS-FSSHTTPB] 2.2.1 defines the
// stream objects, compact integers, extended GUIDs, cell ids and data elements read here.

namespace XRay.Content.OneNote.Store;

internal static class FsshttpbPackageReader
{
    private static readonly ExGuid HeaderCellRoot = new(new Guid("1A5A319C-C26B-41AA-B9C5-9BD8C44E07D4"), 1);
    private static readonly ExGuid DataRoot = new(new Guid("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073"), 2);
    private static readonly ExGuid DefaultContext = new(new Guid("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073"), 1);
    private static readonly Guid RootRoleGuid = new("4A3717F8-1C14-49E7-9526-81D942DE1741");

    private const int TypeDataElement = 0x01;
    private const int TypeObjectDataBlob = 0x02;
    private const int TypeObjectGroupDataExcluded = 0x03;
    private const int TypeObjectGroupDataBlob = 0x05;
    private const int TypeRevisionManifestRoot = 0x0A;
    private const int TypeCellManifest = 0x0B;
    private const int TypeStorageManifest = 0x0C;
    private const int TypeStorageIndexRevisionMapping = 0x0D;
    private const int TypeStorageIndexCellMapping = 0x0E;
    private const int TypeStorageIndexManifestMapping = 0x11;
    private const int TypeDataElementPackage = 0x15;
    private const int TypeObjectGroupDataObject = 0x16;
    private const int TypeStorageManifestRoot = 0x07;
    private const int TypeObjectGroupObject = 0x18;
    private const int TypeRevisionManifestGroupReference = 0x19;
    private const int TypeRevisionManifest = 0x1A;
    private const int TypeObjectGroupBlobReference = 0x1C;
    private const int TypeObjectGroupDeclaration = 0x1D;
    private const int TypeObjectGroupData = 0x1E;
    private const int TypeDataElementFragment = 0x6A;
    private const int TypeObjectGroupMetadata = 0x78;
    private const int TypeObjectGroupMetadataBlock = 0x79;
    private const int TypeOneNotePackaging = 0x7A;

    private sealed record RevisionManifest(ExGuid RevisionId, ExGuid BaseRevisionId, List<(ExGuid Root, ExGuid Object)> Roots, List<ExGuid> Groups);

    private sealed record GroupDeclaration(ExGuid ObjectId, ulong Partition, ExGuid? BlobId);

    private abstract record GroupData(ExGuid[] Objects, CellId[] Cells);
    private sealed record GroupObject(ExGuid[] Objects, CellId[] Cells, int Offset, int Length) : GroupData(Objects, Cells);
    private sealed record GroupExcluded(ExGuid[] Objects, CellId[] Cells) : GroupData(Objects, Cells);
    private sealed record GroupBlobReference(ExGuid[] Objects, CellId[] Cells, ExGuid Blob) : GroupData(Objects, Cells);

    private sealed record ObjectGroup(List<GroupDeclaration> Declarations, List<GroupData> Data);

    private sealed class Package
    {
        public required byte[] Data;
        public readonly Dictionary<ExGuid, (List<(CellId Cell, ExGuid Id)> Cells, Dictionary<ExGuid, ExGuid> Revisions)> StorageIndexes = new();
        public readonly List<(Guid Schema, Dictionary<ExGuid, CellId> Roots)> StorageManifests = new();
        public readonly Dictionary<ExGuid, ExGuid> CellManifests = new();
        public readonly Dictionary<ExGuid, RevisionManifest> RevisionManifests = new();
        public readonly Dictionary<ExGuid, ObjectGroup> ObjectGroups = new();
        public readonly Dictionary<ExGuid, (int Offset, int Length)> Blobs = new();
        public readonly Dictionary<ExGuid, List<(ulong Offset, byte[] Data)>> Fragments = new();
        public readonly List<string> Warnings = new();
        public int ObjectCount;
        public int MaxObjects;
    }

    public static OneStore Read(byte[] data, int offset, OneStoreReadOptions options)
    {
        var r = new ByteReader(data);
        r.Position = offset;
        var fileType = r.ReadGuid();
        var fileGuid = r.ReadGuid();
        r.Skip(16);
        if (r.ReadGuid() != OneNoteGuids.PackageStoreFormat) throw new OneNoteFormatException("Not an FSSHTTPB package store.");
        r.Skip(4);
        ExpectStart(ref r, TypeOneNotePackaging);
        var storageIndexId = ReadExGuid(ref r);
        var cellSchema = r.ReadGuid();

        var package = new Package { Data = data, MaxObjects = options.MaxObjects };
        ReadDataElementPackage(ref r, package);
        AssembleFragments(package);

        var (cellMappings, revisionMappings) = package.StorageIndexes.TryGetValue(storageIndexId, out var si)
            ? si
            : package.StorageIndexes.Values.FirstOrDefault();
        if (cellMappings is null) throw new OneNoteFormatException("The package has no storage index.");
        if (package.StorageManifests.Count == 0) throw new OneNoteFormatException("The package has no storage manifest.");
        var manifest = package.StorageManifests[0];

        var cellToElement = new Dictionary<CellId, ExGuid>();
        foreach (var (cell, id) in cellMappings) cellToElement[cell] = id;

        ExGuid? ResolveRevision(ExGuid revisionId)
        {
            if (revisionMappings.TryGetValue(revisionId, out var element)) return element;
            return package.RevisionManifests.ContainsKey(revisionId) ? revisionId : null;
        }

        ExGuid? CellRevision(CellId cell)
        {
            if (!cellToElement.TryGetValue(cell, out var element) || element.IsNil) return null;
            if (package.CellManifests.TryGetValue(element, out var revision)) return revision.IsNil ? null : ResolveRevision(revision);
            return ResolveRevision(element);
        }

        var objectCache = new Dictionary<(ExGuid Space, ExGuid Group), Dictionary<ExGuid, StoreObject>>();
        var revisionCache = new Dictionary<(ExGuid Space, ExGuid Manifest), (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots)>();

        (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots)? Materialize(ExGuid space, ExGuid manifestId)
        {
            var chain = new List<RevisionManifest>();
            var seen = new HashSet<ExGuid>();
            ExGuid? cursor = manifestId;
            (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots)? baseState = null;
            while (cursor is { } id && package.RevisionManifests.TryGetValue(id, out var rm))
            {
                if (!seen.Add(id)) break;
                if (revisionCache.TryGetValue((space, id), out var cached))
                {
                    baseState = cached;
                    break;
                }
                chain.Add(rm);
                cursor = rm.BaseRevisionId.IsNil ? null : ResolveRevision(rm.BaseRevisionId);
            }
            if (chain.Count == 0 && baseState is null) return null;

            var objects = baseState is { } b ? new Dictionary<ExGuid, StoreObject>(b.Objects) : new Dictionary<ExGuid, StoreObject>();
            var roots = baseState is { } b2 ? new Dictionary<uint, ExGuid>(b2.Roots) : new Dictionary<uint, ExGuid>();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var rm = chain[i];
                foreach (var groupId in rm.Groups)
                {
                    if (!objectCache.TryGetValue((space, groupId), out var groupObjects))
                    {
                        groupObjects = package.ObjectGroups.TryGetValue(groupId, out var group)
                            ? ReadObjects(package, space, group)
                            : new Dictionary<ExGuid, StoreObject>();
                        if (group is null) package.Warnings.Add($"Object group {groupId} is missing.");
                        objectCache[(space, groupId)] = groupObjects;
                    }
                    foreach (var (k, v) in groupObjects) objects[k] = v;
                }
                foreach (var (root, obj) in rm.Roots)
                    if (root.Guid == RootRoleGuid) roots[root.N] = obj;
            }
            revisionCache[(space, manifestId)] = (objects, roots);
            return (objects, roots);
        }

        var spaces = new Dictionary<ExGuid, StoreObjectSpace>();
        CellId? headerCell = manifest.Roots.TryGetValue(HeaderCellRoot, out var hc) ? hc : null;
        foreach (var group in cellMappings.Select(c => c.Cell).Where(c => c != headerCell).GroupBy(c => c.ObjectSpace))
        {
            var osid = group.Key;
            var contexts = group.Select(c => Normalize(c.Context)).Distinct().ToList();
            spaces[osid] = new StoreObjectSpace(osid, contexts, (context, role) =>
            {
                var cell = new CellId(context.IsNil ? DefaultContext : context, osid);
                if (CellRevision(cell) is not { } manifestId)
                {
                    cell = new CellId(context, osid);
                    if (CellRevision(cell) is not { } other) return null;
                    manifestId = other;
                }
                var m = Materialize(osid, manifestId);
                if (m is null || !m.Value.Roots.ContainsKey(role) && role != RevisionRoles.DefaultContent) return null;
                return new StoreRevision(manifestId, context, m.Value.Roots, m.Value.Objects, false);
            });
        }

        Guid identity = fileGuid;
        Guid ancestor = Guid.Empty;
        uint? crc = null;
        uint? lastCode = null;
        if (headerCell is { } headerCellId && CellRevision(headerCellId) is { } headerManifest
            && package.RevisionManifests.TryGetValue(headerManifest, out var hrm))
        {
            foreach (var groupId in hrm.Groups)
            {
                if (!package.ObjectGroups.TryGetValue(groupId, out var group)) continue;
                for (int i = 0; i < group.Declarations.Count && i < group.Data.Count; i++)
                {
                    if (group.Declarations[i].Partition != 1 || group.Data[i] is not GroupObject o) continue;
                    var props = ObjectPropSetParser.Parse(data.AsSpan(o.Offset, o.Length), NullResolver.Instance);
                    identity = props.GetGuid(OneNotePropertyNames.FileIdentityGuid) ?? identity;
                    ancestor = props.GetGuid(OneNotePropertyNames.FileAncestorIdentityGuid) ?? ancestor;
                    crc = props.GetUInt32(OneNotePropertyNames.FileNameCRC) ?? crc;
                    lastCode = props.GetUInt32(OneNotePropertyNames.FileLastCodeVersionThatWroteToIt) ?? lastCode;
                }
            }
        }
        else
        {
            package.Warnings.Add("The package has no header cell.");
        }

        StoreObjectSpace? root = null;
        if (manifest.Roots.TryGetValue(DataRoot, out var dataCell)) spaces.TryGetValue(dataCell.ObjectSpace, out root);
        root ??= spaces.Values.FirstOrDefault() ?? throw new OneNoteFormatException("The package holds no object space.");

        bool isToc = manifest.Schema == OneNoteGuids.TocCellSchema || cellSchema == OneNoteGuids.TocCellSchema || fileType == OneNoteGuids.TocFileType && manifest.Schema != OneNoteGuids.SectionCellSchema;
        return new OneStore(
            OneStoreEncoding.Fsshttpb,
            isToc ? OneStoreFileKind.TableOfContents : OneStoreFileKind.Section,
            identity,
            ancestor,
            crc,
            lastCode,
            root,
            spaces,
            package.Warnings);
    }

    private static ExGuid Normalize(ExGuid context) => context == DefaultContext ? ExGuid.Nil : context;

    private sealed class NullResolver : IReferenceResolver
    {
        public static readonly NullResolver Instance = new();
        public ExGuid ResolveObject(int index, uint compactId) => ExGuid.Nil;
        public CellId ResolveObjectSpace(int index, uint compactId) => default;
        public ExGuid ResolveContext(int index, uint compactId) => ExGuid.Nil;
    }

    /// <summary>[MS-ONESTORE] 2.7.8: an FSSHTTPB object's references are the extended GUIDs and
    /// cell ids carried beside its data, in the order its property set consumes them.</summary>
    private sealed class PositionalResolver : IReferenceResolver
    {
        private readonly ExGuid[] _objects;
        private readonly int[] _objectIndex;
        private readonly CellId[] _spaces;
        private readonly ExGuid[] _contexts;

        public PositionalResolver(uint[] oids, ExGuid[] objects, CellId[] spaces, ExGuid[] contexts)
        {
            _objects = objects;
            _spaces = spaces;
            _contexts = contexts;
            _objectIndex = new int[oids.Length];
            if (oids.Length == objects.Length)
            {
                for (int i = 0; i < oids.Length; i++) _objectIndex[i] = i;
            }
            else
            {
                int next = 0;
                for (int i = 0; i < oids.Length; i++) _objectIndex[i] = oids[i] == 0 ? -1 : next++;
            }
        }

        public ExGuid ResolveObject(int index, uint compactId)
        {
            int at = index < _objectIndex.Length ? _objectIndex[index] : -1;
            return at >= 0 && at < _objects.Length ? _objects[at] : ExGuid.Nil;
        }

        public CellId ResolveObjectSpace(int index, uint compactId)
        {
            if (index >= _spaces.Length) return default;
            var c = _spaces[index];
            return new CellId(Normalize(c.Context), c.ObjectSpace);
        }

        public ExGuid ResolveContext(int index, uint compactId) => index < _contexts.Length ? Normalize(_contexts[index]) : ExGuid.Nil;
    }

    private static Dictionary<ExGuid, StoreObject> ReadObjects(Package package, ExGuid space, ObjectGroup group)
    {
        var parts = new Dictionary<(ExGuid, ulong), GroupData>();
        var order = new List<ExGuid>();
        int n = Math.Min(group.Declarations.Count, group.Data.Count);
        if (group.Declarations.Count != group.Data.Count) package.Warnings.Add("An object group's declarations and data do not pair up.");
        for (int i = 0; i < n; i++)
        {
            var d = group.Declarations[i];
            parts[(d.ObjectId, d.Partition)] = group.Data[i];
            if (d.Partition == 1) order.Add(d.ObjectId);
        }

        var result = new Dictionary<ExGuid, StoreObject>();
        foreach (var id in order)
        {
            if (++package.ObjectCount > package.MaxObjects) throw new OneNoteFormatException($"The package declares more than {package.MaxObjects} objects.");
            if (parts[(id, 1)] is not GroupObject body) continue;
            uint jcidValue = parts.TryGetValue((id, 4), out var meta) && meta is GroupObject m && m.Length >= 4
                ? BitConverter.ToUInt32(package.Data, m.Offset)
                : 0x00020000;
            var jcid = new JcId(jcidValue);
            var span = package.Data.AsSpan(body.Offset, body.Length);

            PropertySet props = PropertySet.Empty;
            try
            {
                var streams = ObjectPropSetParser.ReadStreams(span);
                var contexts = body.Cells.Where(c => c.ObjectSpace == space).Select(c => c.Context).ToArray();
                var spaces = body.Cells.Where(c => c.ObjectSpace != space).ToArray();
                props = ObjectPropSetParser.Parse(span, new PositionalResolver(streams.Oids, body.Objects, spaces, contexts));
            }
            catch (OneNoteFormatException ex)
            {
                package.Warnings.Add($"Object {id} has an unreadable property set: {ex.Message}");
            }

            FileDataReference? fileData = null;
            if (jcid.IsFileData || parts.ContainsKey((id, 2)))
            {
                string? ext = props.GetString(OneNotePropertyNames.FileDataObjectExtension);
                Guid? guid = props.GetGuid(OneNotePropertyNames.FileDataObjectGuid);
                if (props.GetBool(OneNotePropertyNames.FileDataObjectInvalidData) == true)
                {
                    fileData = new FileDataReference(FileDataLocation.Invalid, "<invfdo>", guid, ext, 0, null);
                }
                else if (parts.TryGetValue((id, 2), out var blobPart) && blobPart is GroupBlobReference br && package.Blobs.TryGetValue(br.Blob, out var blob))
                {
                    var data = package.Data;
                    fileData = new FileDataReference(FileDataLocation.Embedded, guid is { } g ? $"<ifndf>{g:B}" : null, guid, ext, blob.Length, () => data.AsSpan(blob.Offset, blob.Length).ToArray());
                }
                else
                {
                    fileData = new FileDataReference(FileDataLocation.Missing, null, guid, ext, null, null);
                }
            }
            result[id] = new StoreObject(id, jcid, props, fileData);
        }
        return result;
    }

    // ------------------------------------------------------------------- data elements

    private static void ReadDataElementPackage(ref ByteReader r, Package package)
    {
        ExpectStart(ref r, TypeDataElementPackage);
        r.Skip(1);
        while (!IsEnd8(ref r, TypeDataElementPackage))
            ReadDataElement(ref r, package);
        ExpectEnd8(ref r, TypeDataElementPackage);
    }

    private static void ReadDataElement(ref ByteReader r, Package package)
    {
        ExpectStart(ref r, TypeDataElement);
        var id = ReadExGuid(ref r);
        ReadSerialNumber(ref r);
        ulong type = ReadCompactU64(ref r);
        switch (type)
        {
            case 0x01: ReadStorageIndex(ref r, package, id); break;
            case 0x02: ReadStorageManifest(ref r, package); break;
            case 0x03:
                ExpectStart(ref r, TypeCellManifest);
                package.CellManifests[id] = ReadExGuid(ref r);
                ExpectEnd8(ref r, TypeDataElement);
                break;
            case 0x04: ReadRevisionManifest(ref r, package, id); break;
            case 0x05: ReadObjectGroup(ref r, package, id); break;
            case 0x06: ReadFragment(ref r, package); break;
            case 0x0A:
            {
                ExpectStart(ref r, TypeObjectDataBlob);
                ulong size = ReadCompactU64(ref r);
                if (size > (ulong)r.Remaining) throw new OneNoteFormatException("An object data BLOB overruns the package.");
                package.Blobs[id] = (r.Position, (int)size);
                r.Skip((int)size);
                ExpectEnd8(ref r, TypeDataElement);
                break;
            }
            default:
                throw new OneNoteFormatException($"Unknown data element type 0x{type:X}.");
        }
    }

    private static void ReadStorageIndex(ref ByteReader r, Package package, ExGuid id)
    {
        var cells = new List<(CellId, ExGuid)>();
        var revisions = new Dictionary<ExGuid, ExGuid>();
        while (!IsEnd8(ref r, TypeDataElement))
        {
            var h = ReadStart(ref r);
            switch (h.Type)
            {
                case TypeStorageIndexManifestMapping:
                    ReadExGuid(ref r);
                    ReadSerialNumber(ref r);
                    break;
                case TypeStorageIndexCellMapping:
                {
                    var cell = ReadCellId(ref r);
                    var element = ReadExGuid(ref r);
                    ReadSerialNumber(ref r);
                    cells.Add((cell, element));
                    break;
                }
                case TypeStorageIndexRevisionMapping:
                {
                    var revision = ReadExGuid(ref r);
                    var element = ReadExGuid(ref r);
                    ReadSerialNumber(ref r);
                    revisions[revision] = element;
                    break;
                }
                default:
                    throw new OneNoteFormatException($"Unexpected stream object 0x{h.Type:X} in a storage index.");
            }
        }
        ExpectEnd8(ref r, TypeDataElement);
        package.StorageIndexes[id] = (cells, revisions);
    }

    private static void ReadStorageManifest(ref ByteReader r, Package package)
    {
        ExpectStart(ref r, TypeStorageManifest);
        var schema = r.ReadGuid();
        var roots = new Dictionary<ExGuid, CellId>();
        while (!IsEnd8(ref r, TypeDataElement))
        {
            ExpectStart(ref r, TypeStorageManifestRoot);
            var root = ReadExGuid(ref r);
            roots[root] = ReadCellId(ref r);
        }
        ExpectEnd8(ref r, TypeDataElement);
        package.StorageManifests.Add((schema, roots));
    }

    private static void ReadRevisionManifest(ref ByteReader r, Package package, ExGuid id)
    {
        ExpectStart(ref r, TypeRevisionManifest);
        var revision = ReadExGuid(ref r);
        var baseRevision = ReadExGuid(ref r);
        var roots = new List<(ExGuid, ExGuid)>();
        var groups = new List<ExGuid>();
        while (!IsEnd8(ref r, TypeDataElement))
        {
            var h = ReadStart(ref r);
            if (h.Type == TypeRevisionManifestRoot)
            {
                var root = ReadExGuid(ref r);
                roots.Add((root, ReadExGuid(ref r)));
            }
            else if (h.Type == TypeRevisionManifestGroupReference)
            {
                groups.Add(ReadExGuid(ref r));
            }
            else
            {
                throw new OneNoteFormatException($"Unexpected stream object 0x{h.Type:X} in a revision manifest.");
            }
        }
        ExpectEnd8(ref r, TypeDataElement);
        package.RevisionManifests[id] = new RevisionManifest(revision, baseRevision, roots, groups);
    }

    private static void ReadObjectGroup(ref ByteReader r, Package package, ExGuid id)
    {
        ExpectStart(ref r, TypeObjectGroupDeclaration);
        var declarations = new List<GroupDeclaration>();
        while (!IsEnd8(ref r, TypeObjectGroupDeclaration))
        {
            var h = ReadStart(ref r);
            if (h.Type == TypeObjectGroupObject)
            {
                var objectId = ReadExGuid(ref r);
                ulong partition = ReadCompactU64(ref r);
                ReadCompactU64(ref r);
                ReadCompactU64(ref r);
                ReadCompactU64(ref r);
                declarations.Add(new GroupDeclaration(objectId, partition, null));
            }
            else if (h.Type == TypeObjectGroupDataBlob)
            {
                var objectId = ReadExGuid(ref r);
                var blob = ReadExGuid(ref r);
                ulong partition = ReadCompactU64(ref r);
                ReadCompactU64(ref r);
                ReadCompactU64(ref r);
                declarations.Add(new GroupDeclaration(objectId, partition, blob));
            }
            else
            {
                throw new OneNoteFormatException($"Unexpected stream object 0x{h.Type:X} in an object group declaration.");
            }
        }
        ExpectEnd8(ref r, TypeObjectGroupDeclaration);

        var next = ReadStart(ref r);
        if (next.Type == TypeObjectGroupMetadataBlock)
        {
            while (!IsEnd8(ref r, TypeObjectGroupMetadataBlock))
            {
                ExpectStart(ref r, TypeObjectGroupMetadata);
                ReadCompactU64(ref r);
            }
            ExpectEnd8(ref r, TypeObjectGroupMetadataBlock);
            next = ReadStart(ref r);
        }
        if (next.Type != TypeObjectGroupData) throw new OneNoteFormatException($"Unexpected stream object 0x{next.Type:X} where object group data belongs.");

        var objects = new List<GroupData>();
        while (!IsEnd8(ref r, TypeObjectGroupData))
        {
            var h = ReadStart(ref r);
            switch (h.Type)
            {
                case TypeObjectGroupDataObject:
                {
                    var refs = ReadExGuidArray(ref r);
                    var cells = ReadCellIdArray(ref r);
                    ulong size = ReadCompactU64(ref r);
                    if (size > (ulong)r.Remaining) throw new OneNoteFormatException("Object data overruns the package.");
                    objects.Add(new GroupObject(refs, cells, r.Position, (int)size));
                    r.Skip((int)size);
                    break;
                }
                case TypeObjectGroupDataExcluded:
                {
                    var refs = ReadExGuidArray(ref r);
                    var cells = ReadCellIdArray(ref r);
                    ReadCompactU64(ref r);
                    objects.Add(new GroupExcluded(refs, cells));
                    break;
                }
                case TypeObjectGroupBlobReference:
                {
                    var refs = ReadExGuidArray(ref r);
                    var cells = ReadCellIdArray(ref r);
                    objects.Add(new GroupBlobReference(refs, cells, ReadExGuid(ref r)));
                    break;
                }
                default:
                    throw new OneNoteFormatException($"Unexpected stream object 0x{h.Type:X} in object group data.");
            }
        }
        ExpectEnd8(ref r, TypeObjectGroupData);
        ExpectEnd8(ref r, TypeDataElement);
        package.ObjectGroups[id] = new ObjectGroup(declarations, objects);
    }

    private static void ReadFragment(ref ByteReader r, Package package)
    {
        ExpectStart(ref r, TypeDataElementFragment);
        var target = ReadExGuid(ref r);
        ReadCompactU64(ref r);
        ulong offset = ReadCompactU64(ref r);
        ulong length = ReadCompactU64(ref r);
        if (length > (ulong)r.Remaining) throw new OneNoteFormatException("A data element fragment overruns the package.");
        var bytes = r.ReadBytes((int)length).ToArray();
        ExpectEnd8(ref r, TypeDataElement);
        if (!package.Fragments.TryGetValue(target, out var list)) package.Fragments[target] = list = new();
        list.Add((offset, bytes));
    }

    /// <summary>[MS-FSSHTTPB] 2.2.1.12.7: a data element too large for one stream object is
    /// split into fragments; once reassembled it is an ordinary serialized data element.</summary>
    private static void AssembleFragments(Package package)
    {
        foreach (var (target, parts) in package.Fragments)
        {
            try
            {
                long total = parts.Max(p => (long)p.Offset + p.Data.Length);
                if (total > int.MaxValue) throw new OneNoteFormatException("too large");
                var buffer = new byte[total];
                foreach (var (offset, data) in parts) data.CopyTo(buffer, (long)offset);
                var r = new ByteReader(buffer);
                var sub = new Package { Data = buffer, MaxObjects = package.MaxObjects };
                ReadDataElement(ref r, sub);
                package.Warnings.Add($"Data element {target} was stored in {parts.Count} fragments and has been reassembled.");
                foreach (var (k, v) in sub.RevisionManifests) package.RevisionManifests[k] = v;
                foreach (var (k, v) in sub.CellManifests) package.CellManifests[k] = v;
                foreach (var (k, v) in sub.StorageIndexes) package.StorageIndexes[k] = v;
                package.StorageManifests.AddRange(sub.StorageManifests);
                if (sub.ObjectGroups.Count > 0 || sub.Blobs.Count > 0)
                    package.Warnings.Add($"Fragmented data element {target} holds object data, which is not supported.");
            }
            catch (OneNoteFormatException ex)
            {
                package.Warnings.Add($"Fragmented data element {target} could not be reassembled: {ex.Message}");
            }
        }
    }

    // ----------------------------------------------------------- [MS-FSSHTTPB] primitives

    private readonly record struct StreamHeader(int Type, bool Compound, ulong Length);

    private static StreamHeader ReadStart(ref ByteReader r)
    {
        byte first = r.PeekU8();
        switch (first & 0x3)
        {
            case 0:
            {
                ushort v = r.ReadU16();
                return new StreamHeader((v >> 3) & 0x3F, (v & 0x4) != 0, (ulong)(v >> 9));
            }
            case 2:
            {
                uint v = r.ReadU32();
                ulong length = v >> 17;
                if (length == 0x7FFF) length = ReadCompactU64(ref r);
                return new StreamHeader((int)((v >> 3) & 0x3FFF), (v & 0x4) != 0, length);
            }
            default:
                throw new OneNoteFormatException($"Expected a stream object start at {r.Position}, found 0x{first:X2}.");
        }
    }

    private static void ExpectStart(ref ByteReader r, int type)
    {
        var h = ReadStart(ref r);
        if (h.Type != type) throw new OneNoteFormatException($"Expected stream object 0x{type:X}, found 0x{h.Type:X} at {r.Position}.");
    }

    private static bool IsEnd8(ref ByteReader r, int type)
    {
        byte b = r.PeekU8();
        return (b & 0x3) == 1 && b >> 2 == type;
    }

    private static void ExpectEnd8(ref ByteReader r, int type)
    {
        byte b = r.PeekU8();
        if ((b & 0x3) == 1 && b >> 2 == type)
        {
            r.Skip(1);
            return;
        }
        if ((b & 0x3) == 3)
        {
            ushort v = r.ReadU16();
            if (v >> 2 == type) return;
        }
        throw new OneNoteFormatException($"Expected the end of stream object 0x{type:X} at {r.Position}.");
    }

    internal static ulong ReadCompactU64(ref ByteReader r)
    {
        byte first = r.PeekU8();
        if (first == 0)
        {
            r.Skip(1);
            return 0;
        }
        for (int width = 1; width <= 7; width++)
        {
            if ((first & (1 << (width - 1))) == 0) continue;
            var bytes = r.ReadBytes(width);
            ulong v = 0;
            for (int i = width - 1; i >= 0; i--) v = (v << 8) | bytes[i];
            return v >> width;
        }
        r.Skip(1);
        return r.ReadU64();
    }

    internal static ExGuid ReadExGuid(ref ByteReader r)
    {
        byte first = r.ReadU8();
        if (first == 0) return ExGuid.Nil;
        if ((first & 0x7) == 4) return new ExGuid(r.ReadGuid(), (uint)(first >> 3));
        if ((first & 0x3F) == 32)
        {
            uint value = ((uint)r.ReadU8() << 2) | (uint)(first >> 6);
            return new ExGuid(r.ReadGuid(), value);
        }
        if ((first & 0x7F) == 64)
        {
            uint value = ((uint)r.ReadU16() << 1) | (uint)(first >> 7);
            return new ExGuid(r.ReadGuid(), value);
        }
        if (first == 128)
        {
            uint value = r.ReadU32();
            return new ExGuid(r.ReadGuid(), value);
        }
        throw new OneNoteFormatException($"Invalid extended GUID marker 0x{first:X2}.");
    }

    private static ExGuid[] ReadExGuidArray(ref ByteReader r)
    {
        ulong count = ReadCompactU64(ref r);
        if (count > (ulong)r.Remaining) throw new OneNoteFormatException("An extended GUID array overruns the package.");
        var result = new ExGuid[count];
        for (ulong i = 0; i < count; i++) result[i] = ReadExGuid(ref r);
        return result;
    }

    private static CellId ReadCellId(ref ByteReader r)
    {
        var a = ReadExGuid(ref r);
        return new CellId(a, ReadExGuid(ref r));
    }

    private static CellId[] ReadCellIdArray(ref ByteReader r)
    {
        ulong count = ReadCompactU64(ref r);
        if (count > (ulong)r.Remaining) throw new OneNoteFormatException("A cell id array overruns the package.");
        var result = new CellId[count];
        for (ulong i = 0; i < count; i++) result[i] = ReadCellId(ref r);
        return result;
    }

    private static void ReadSerialNumber(ref ByteReader r)
    {
        if (r.ReadU8() == 0) return;
        r.Skip(16 + 8);
    }
}

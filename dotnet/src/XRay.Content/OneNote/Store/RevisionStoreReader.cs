// [MS-ONESTORE] 2.1-2.6: the revision store file format OneNote desktop writes. Sections 2.3.1
// (header), 2.3.3 (transaction log), 2.4 (file node lists), 2.5 (file node types), 2.1.9-2.1.12
// (revision manifests, contexts and roles) and 2.1.3 (global identification tables).

using System.Text;

namespace XRay.Content.OneNote.Store;

internal static class RevisionStoreReader
{
    private const ulong FileNodeListHeaderMagic = 0xA4567AB1F5F7F4C4;
    private const ulong FileNodeListFooterMagic = 0x8BC215C38233BA4B;
    private const int MaxFragments = 1_000_000;
    private static readonly Guid FileDataStoreHeader = new("BDE316E7-2665-4511-A4C4-8D4D0B7A9EAC");

    private readonly record struct ChunkRef(ulong Stp, ulong Cb)
    {
        public bool IsNil(int stpSize) => stpSize switch
        {
            8 => Stp == ulong.MaxValue,
            4 => Stp == uint.MaxValue,
            _ => Stp == ushort.MaxValue,
        } && Cb == 0;

        public bool IsZero => Stp == 0 && Cb == 0;
    }

    /// <summary>A FileNode: its type, where its fnd data starts, and the chunk it references.</summary>
    private readonly record struct FileNode(int Id, int BaseType, long DataStart, int DataLength, ChunkRef Ref, bool RefIsNil);

    private sealed class Manifest
    {
        public ExGuid Rid;
        public ExGuid Dependent;
        public uint Role;
        public ExGuid Context;
        public bool Encrypted;
        public Dictionary<uint, Guid>? Table;
        public readonly Dictionary<uint, ExGuid> Roots = new();
        public readonly List<(uint Cid, uint Role)> PendingRoots = new();
        public readonly List<Declared> Objects = new();
    }

    /// <summary>An object a manifest declares; <see cref="JcId"/> is null for a revision
    /// (0x041/0x042), which keeps the type of the object it revises.</summary>
    private sealed record Declared(ExGuid Id, JcId? JcId, PropertySet Properties, FileDataReference? FileData);

    private sealed class State
    {
        public required byte[] Data;
        public required bool IsToc;
        public required OneStoreReadOptions Options;
        public readonly Dictionary<uint, uint> CommittedNodeCounts = new();
        public readonly Dictionary<Guid, (long Offset, long Length)> FileDataStore = new();
        public readonly List<string> Warnings = new();
        public int ObjectCount;
    }

    public static OneStore Read(byte[] data, OneStoreReadOptions options)
    {
        if (data.Length < 1024) throw new OneNoteFormatException("The file is shorter than a revision store header.");
        var h = new ByteReader(data);
        var fileType = h.ReadGuid();
        var fileGuid = h.ReadGuid();
        h.Skip(16);
        h.Skip(16);
        uint ffvLastCode = h.ReadU32();
        h.Position = 96;
        uint transactionCount = h.ReadU32();
        h.Position = 128;
        var ancestor = h.ReadGuid();
        uint crcName = h.ReadU32();
        h.Position = 160;
        var fcrTransactionLog = new ChunkRef(h.ReadU64(), h.ReadU32());
        var fcrRoot = new ChunkRef(h.ReadU64(), h.ReadU32());

        ulong packaged = fcrTransactionLog.Stp + fcrTransactionLog.Cb;
        if (packaged + 64 <= (ulong)data.Length && new Guid(data.AsSpan((int)packaged + 48, 16)) == OneNoteGuids.PackageStoreFormat)
            return FsshttpbPackageReader.Read(data, (int)packaged, options);

        var state = new State { Data = data, IsToc = fileType == OneNoteGuids.TocFileType, Options = options };
        ReadTransactionLog(state, fcrTransactionLog, transactionCount);

        var spaceRefs = new List<(ExGuid Gosid, ChunkRef Ref)>();
        ExGuid rootGosid = ExGuid.Nil;
        foreach (var node in ReadFileNodeList(state, fcrRoot))
        {
            switch (node.Id)
            {
                case 0x008:
                    spaceRefs.Add((ReadAt(state, node).ReadExGuid(), node.Ref));
                    break;
                case 0x004:
                    rootGosid = ReadAt(state, node).ReadExGuid();
                    break;
                case 0x090:
                    ReadFileDataStore(state, node.Ref);
                    break;
            }
        }
        if (spaceRefs.Count == 0) throw new OneNoteFormatException("The root file node list declares no object spaces.");

        var spaces = new Dictionary<ExGuid, StoreObjectSpace>();
        foreach (var (gosid, reference) in spaceRefs)
        {
            try
            {
                spaces[gosid] = ReadObjectSpace(state, gosid, reference);
            }
            catch (OneNoteFormatException ex)
            {
                state.Warnings.Add($"Object space {gosid} could not be read: {ex.Message}");
            }
        }
        if (!spaces.TryGetValue(rootGosid, out var root))
        {
            if (spaces.Count == 0) throw new OneNoteFormatException("No object space could be read. " + string.Join(" ", state.Warnings));
            state.Warnings.Add($"The root object space {rootGosid} is missing; using the first object space instead.");
            root = spaces.Values.First();
        }

        return new OneStore(
            OneStoreEncoding.RevisionStore,
            state.IsToc ? OneStoreFileKind.TableOfContents : OneStoreFileKind.Section,
            fileGuid,
            ancestor,
            crcName,
            ffvLastCode,
            root,
            spaces,
            state.Warnings);
    }

    // ---------------------------------------------------------------- transaction log, 2.3.3

    private static void ReadTransactionLog(State state, ChunkRef first, uint transactionCount)
    {
        var reference = first;
        uint committed = 0;
        int fragments = 0;
        var pending = new Dictionary<uint, uint>();
        while (committed < transactionCount && !reference.IsNil(8) && !reference.IsZero)
        {
            if (++fragments > MaxFragments) throw new OneNoteFormatException("The transaction log does not terminate.");
            var r = Slice(state, reference);
            int entries = (int)((reference.Cb - 12) / 8);
            for (int i = 0; i < entries && committed < transactionCount; i++)
            {
                uint srcId = r.ReadU32();
                uint value = r.ReadU32();
                if (srcId == 1)
                {
                    foreach (var (k, v) in pending) state.CommittedNodeCounts[k] = v;
                    pending.Clear();
                    committed++;
                }
                else if (srcId != 0)
                {
                    pending[srcId] = value;
                }
            }
            if (committed >= transactionCount) break;
            r.Position = entries * 8;
            reference = new ChunkRef(r.ReadU64(), r.ReadU32());
            if (reference.Cb < 12 || reference.Stp > (ulong)state.Data.Length || reference.Cb > (ulong)state.Data.Length - reference.Stp) break;
        }
        foreach (var (k, v) in pending) state.CommittedNodeCounts.TryAdd(k, v);
    }

    // --------------------------------------------------------------- file node lists, 2.4

    private static List<FileNode> ReadFileNodeList(State state, ChunkRef first)
    {
        var data = state.Data;
        var reference = first;
        uint? listId = null;
        int read = 0;
        int fragments = 0;
        uint expectedSequence = 0;
        var result = new List<FileNode>();
        while (!reference.IsNil(8) && !reference.IsZero)
        {
            if (++fragments > MaxFragments) throw new OneNoteFormatException("A file node list does not terminate.");
            CheckRange(state, reference);
            long start = (long)reference.Stp;
            long end = start + (long)reference.Cb;
            if (reference.Cb < 36) throw new OneNoteFormatException($"File node list fragment at {start} is too small.");

            var r = new ByteReader(data.AsSpan((int)start, (int)reference.Cb));
            if (r.ReadU64() != FileNodeListHeaderMagic) throw new OneNoteFormatException($"Missing file node list header at {start}.");
            uint id = r.ReadU32();
            uint sequence = r.ReadU32();
            if (listId is null) listId = id;
            else if (id != listId) throw new OneNoteFormatException($"Fragment at {start} belongs to list {id}, expected {listId}.");
            if (sequence != expectedSequence) state.Warnings.Add($"File node list {id} fragment {sequence} arrived out of order.");
            expectedSequence = sequence + 1;

            int limit = state.CommittedNodeCounts.TryGetValue(id, out var c) ? (int)c : int.MaxValue;
            int nextOffset = (int)reference.Cb - 20;
            bool terminated = false;
            var nodes = result;
            while (read < limit && nextOffset - r.Position >= 4)
            {
                int nodeStart = r.Position;
                uint header = r.ReadU32();
                int fileNodeId = (int)(header & 0x3FF);
                int size = (int)((header >> 10) & 0x1FFF);
                if (fileNodeId == 0xFF)
                {
                    terminated = true;
                    break;
                }
                if (fileNodeId == 0 && size == 0) break;
                if (size < 4 || nodeStart + size > nextOffset) throw new OneNoteFormatException($"File node 0x{fileNodeId:X3} at {start + nodeStart} has an invalid size {size}.");

                int stpFormat = (int)((header >> 23) & 0x3);
                int cbFormat = (int)((header >> 25) & 0x3);
                int baseType = (int)((header >> 27) & 0xF);
                var chunk = default(ChunkRef);
                bool nil = true;
                if (baseType is 1 or 2)
                {
                    int stpSize = stpFormat switch { 0 => 8, 1 => 4, 2 => 2, _ => 4 };
                    int cbSize = cbFormat switch { 0 => 4, 1 => 8, 2 => 1, _ => 2 };
                    ulong stp = r.ReadUIntOfSize(stpSize);
                    ulong cb = r.ReadUIntOfSize(cbSize);
                    nil = new ChunkRef(stp, cb).IsNil(stpSize);
                    if (stpFormat >= 2) stp *= 8;
                    if (cbFormat >= 2) cb *= 8;
                    chunk = new ChunkRef(stp, cb);
                }
                nodes.Add(new FileNode(fileNodeId, baseType, start + r.Position, size - (r.Position - nodeStart), chunk, nil));
                r.Position = nodeStart + size;
                read++;
            }

            if (read >= limit) return result;

            r.Position = nextOffset;
            var next = new ChunkRef(r.ReadU64(), r.ReadU32());
            if (r.ReadU64() != FileNodeListFooterMagic) state.Warnings.Add($"File node list fragment at {start} has a bad footer.");
            if (!terminated && next.IsNil(8)) return result;
            if (end > data.Length) return result;
            reference = next;
        }
        return result;
    }

    // ------------------------------------------------------------- file data store, 2.6.13

    private static void ReadFileDataStore(State state, ChunkRef listRef)
    {
        foreach (var node in ReadFileNodeList(state, listRef))
        {
            if (node.Id != 0x094) continue;
            var r = ReadAt(state, node);
            var guid = r.ReadGuid();
            try
            {
                var o = Slice(state, node.Ref);
                if (o.ReadGuid() != FileDataStoreHeader) throw new OneNoteFormatException("bad header GUID");
                ulong length = o.ReadU64();
                o.Skip(12);
                long offset = (long)node.Ref.Stp + 36;
                if (length > node.Ref.Cb - 36) throw new OneNoteFormatException("data overruns its chunk");
                state.FileDataStore[guid] = (offset, (long)length);
            }
            catch (OneNoteFormatException ex)
            {
                state.Warnings.Add($"File data object {guid:B} is unreadable: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------- object spaces

    private static StoreObjectSpace ReadObjectSpace(State state, ExGuid gosid, ChunkRef manifestListRef)
    {
        ChunkRef? revisionListRef = null;
        foreach (var node in ReadFileNodeList(state, manifestListRef))
            if (node.Id == 0x010) revisionListRef = node.Ref;
        if (revisionListRef is null) throw new OneNoteFormatException("its manifest list names no revision manifest list");

        var manifests = new Dictionary<ExGuid, Manifest>();
        var labels = new Dictionary<(ExGuid Context, uint Role), ExGuid>();
        Manifest? current = null;
        Dictionary<uint, Guid>? building = null;

        foreach (var node in ReadFileNodeList(state, revisionListRef.Value))
        {
            var r = ReadAt(state, node);
            switch (node.Id)
            {
                case 0x014:
                    break;
                case 0x01B:
                case 0x01E:
                case 0x01F:
                {
                    current = new Manifest { Rid = r.ReadExGuid(), Dependent = r.ReadExGuid() };
                    if (node.Id == 0x01B) r.Skip(8);
                    current.Role = r.ReadU32();
                    ushort odcs = r.ReadU16();
                    current.Encrypted = odcs == 2;
                    if (node.Id == 0x01F) current.Context = r.ReadExGuid();
                    labels[(current.Context, current.Role)] = current.Rid;
                    building = null;
                    break;
                }
                case 0x01C:
                    if (current is not null)
                    {
                        FinishManifest(current, manifests);
                        manifests[current.Rid] = current;
                    }
                    current = null;
                    break;
                case 0x05C:
                {
                    var rid = r.ReadExGuid();
                    labels[(ExGuid.Nil, r.ReadU32())] = rid;
                    break;
                }
                case 0x05D:
                {
                    var rid = r.ReadExGuid();
                    uint role = r.ReadU32();
                    labels[(r.ReadExGuid(), role)] = rid;
                    break;
                }
                case 0x07C:
                    if (current is not null) current.Encrypted = true;
                    break;
                case 0x0B0:
                    if (current is not null) ReadObjectGroup(state, gosid, node.Ref, current);
                    break;
                case 0x021:
                case 0x022:
                    building = new Dictionary<uint, Guid>();
                    break;
                case 0x024:
                case 0x025:
                case 0x026:
                    if (building is not null && current is not null) ReadGlobalIdEntry(node.Id, ref r, building, current, manifests);
                    break;
                case 0x028:
                    if (current is not null && building is not null) current.Table = building;
                    building = null;
                    break;
                case 0x059:
                {
                    uint cid = r.ReadU32();
                    uint role = r.ReadU32();
                    current?.PendingRoots.Add((cid, role));
                    break;
                }
                case 0x05A:
                    if (current is not null)
                    {
                        var oid = r.ReadExGuid();
                        current.Roots[r.ReadU32()] = oid;
                    }
                    break;
                case 0x02D:
                case 0x02E:
                case 0x041:
                case 0x042:
                case 0x0A4:
                case 0x0A5:
                case 0x0C4:
                case 0x0C5:
                case 0x072:
                case 0x073:
                    if (current is not null)
                    {
                        var table = building ?? current.Table ?? DependencyTable(current, manifests) ?? new Dictionary<uint, Guid>();
                        var declared = ReadDeclaration(state, gosid, node, table, current.Encrypted);
                        if (declared is not null) current.Objects.Add(declared);
                    }
                    break;
            }
        }

        var contexts = labels.Keys.Select(k => k.Context).Distinct().ToList();
        var materialized = new Dictionary<ExGuid, (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots, bool Encrypted)>();

        StoreRevision? Resolve(ExGuid context, uint role)
        {
            if (!labels.TryGetValue((context, role), out var rid)) return null;
            var m = Materialize(state, rid, manifests, materialized);
            if (m is null) return null;
            return new StoreRevision(rid, context, m.Value.Roots, m.Value.Objects, m.Value.Encrypted);
        }

        return new StoreObjectSpace(gosid, contexts, Resolve);
    }

    private static Dictionary<uint, Guid>? DependencyTable(Manifest m, Dictionary<ExGuid, Manifest> manifests)
    {
        var dep = m.Dependent;
        int guard = 0;
        while (!dep.IsNil && manifests.TryGetValue(dep, out var d) && guard++ < 100_000)
        {
            if (d.Table is not null) return d.Table;
            dep = d.Dependent;
        }
        return null;
    }

    private static void FinishManifest(Manifest m, Dictionary<ExGuid, Manifest> manifests)
    {
        if (m.PendingRoots.Count == 0) return;
        var table = m.Table ?? DependencyTable(m, manifests) ?? new Dictionary<uint, Guid>();
        foreach (var (cid, role) in m.PendingRoots) m.Roots[role] = ResolveCompact(table, cid);
    }

    private static void ReadGlobalIdEntry(int nodeId, ref ByteReader r, Dictionary<uint, Guid> building, Manifest current, Dictionary<ExGuid, Manifest> manifests)
    {
        switch (nodeId)
        {
            case 0x024:
            {
                uint index = r.ReadU32();
                building[index] = r.ReadGuid();
                break;
            }
            case 0x025:
            {
                uint from = r.ReadU32();
                uint to = r.ReadU32();
                var dep = DependencyTable(current, manifests);
                if (dep is not null && dep.TryGetValue(from, out var g)) building[to] = g;
                break;
            }
            case 0x026:
            {
                uint from = r.ReadU32();
                uint count = r.ReadU32();
                uint to = r.ReadU32();
                var dep = DependencyTable(current, manifests);
                if (dep is null || count > 0xFFFFFF) break;
                for (uint i = 0; i < count; i++)
                    if (dep.TryGetValue(from + i, out var g)) building[to + i] = g;
                break;
            }
        }
    }

    // ------------------------------------------------------------- object groups, 2.1.13

    private static void ReadObjectGroup(State state, ExGuid gosid, ChunkRef groupRef, Manifest manifest)
    {
        Dictionary<uint, Guid>? table = null;
        Dictionary<uint, Guid>? building = null;
        foreach (var node in ReadFileNodeList(state, groupRef))
        {
            switch (node.Id)
            {
                case 0x022:
                case 0x021:
                    building = new Dictionary<uint, Guid>();
                    break;
                case 0x024:
                case 0x025:
                case 0x026:
                    if (building is not null)
                    {
                        var r = ReadAt(state, node);
                        ReadGlobalIdEntry(node.Id, ref r, building, manifest, new Dictionary<ExGuid, Manifest>());
                    }
                    break;
                case 0x028:
                    table = building;
                    building = null;
                    break;
                case 0x0A4:
                case 0x0A5:
                case 0x0C4:
                case 0x0C5:
                case 0x072:
                case 0x073:
                case 0x02D:
                case 0x02E:
                case 0x041:
                case 0x042:
                {
                    var declared = ReadDeclaration(state, gosid, node, table ?? building ?? new Dictionary<uint, Guid>(), manifest.Encrypted);
                    if (declared is not null) manifest.Objects.Add(declared);
                    break;
                }
            }
        }
    }

    private static ExGuid ResolveCompact(Dictionary<uint, Guid> table, uint compactId)
    {
        uint n = compactId & 0xFF;
        uint index = compactId >> 8;
        return table.TryGetValue(index, out var g) ? new ExGuid(g, n) : ExGuid.Nil;
    }

    private sealed class TableResolver : IReferenceResolver
    {
        private readonly Dictionary<uint, Guid> _table;
        private readonly ExGuid _space;

        public TableResolver(Dictionary<uint, Guid> table, ExGuid space)
        {
            _table = table;
            _space = space;
        }

        public ExGuid ResolveObject(int index, uint compactId) => ResolveCompact(_table, compactId);
        public CellId ResolveObjectSpace(int index, uint compactId) => new(ExGuid.Nil, ResolveCompact(_table, compactId));
        public ExGuid ResolveContext(int index, uint compactId) => ResolveCompact(_table, compactId);
    }

    private static Declared? ReadDeclaration(State state, ExGuid gosid, FileNode node, Dictionary<uint, Guid> table, bool encrypted)
    {
        if (++state.ObjectCount > state.Options.MaxObjects) throw new OneNoteFormatException($"The file declares more than {state.Options.MaxObjects} objects.");
        var r = ReadAt(state, node);
        uint oidCid;
        JcId? jcid;
        FileDataReference? fileData = null;
        bool hasPropSet = true;

        switch (node.Id)
        {
            case 0x02D:
            case 0x02E:
            {
                oidCid = r.ReadU32();
                ulong bits = r.ReadU16() | ((ulong)r.ReadU32() << 16);
                jcid = new JcId(0x00020000u | (uint)(bits & 0x3FF));
                break;
            }
            case 0x041:
            case 0x042:
                oidCid = r.ReadU32();
                jcid = null;
                break;
            case 0x0A4:
            case 0x0A5:
            case 0x0C4:
            case 0x0C5:
                oidCid = r.ReadU32();
                jcid = new JcId(r.ReadU32());
                break;
            case 0x072:
            case 0x073:
            {
                oidCid = r.ReadU32();
                jcid = new JcId(r.ReadU32());
                if (node.Id == 0x072) r.Skip(1); else r.Skip(4);
                string reference = ReadStorageString(ref r);
                string extension = ReadStorageString(ref r);
                fileData = ResolveFileData(state, reference, extension);
                hasPropSet = false;
                break;
            }
            default:
                return null;
        }

        var id = ResolveCompact(table, oidCid);
        PropertySet props = PropertySet.Empty;
        if (hasPropSet && !encrypted && !node.RefIsNil && node.Ref.Cb > 0 && (jcid is null || jcid.Value.IsPropertySet || !jcid.Value.IsFileData))
        {
            try
            {
                CheckRange(state, node.Ref);
                props = ObjectPropSetParser.Parse(state.Data.AsSpan((int)node.Ref.Stp, (int)node.Ref.Cb), new TableResolver(table, gosid));
            }
            catch (OneNoteFormatException ex)
            {
                state.Warnings.Add($"Object {id} has an unreadable property set: {ex.Message}");
            }
        }
        return new Declared(id, jcid, props, fileData);
    }

    private static string ReadStorageString(ref ByteReader r)
    {
        uint cch = r.ReadU32();
        if (cch > (uint)r.Remaining / 2) throw new OneNoteFormatException("A StringInStorageBuffer overruns its file node.");
        return Encoding.Unicode.GetString(r.ReadBytes((int)cch * 2));
    }

    private static FileDataReference ResolveFileData(State state, string reference, string extension)
    {
        string? ext = string.IsNullOrEmpty(extension) ? null : extension;
        if (reference.StartsWith("<ifndf>", StringComparison.Ordinal) && Guid.TryParse(reference.AsSpan(7), out var guid))
        {
            if (state.FileDataStore.TryGetValue(guid, out var loc))
            {
                var data = state.Data;
                return new FileDataReference(FileDataLocation.Embedded, reference, guid, ext, loc.Length, () => data.AsSpan((int)loc.Offset, (int)loc.Length).ToArray());
            }
            return new FileDataReference(FileDataLocation.Missing, reference, guid, ext, null, null);
        }
        if (reference.StartsWith("<file>", StringComparison.Ordinal))
        {
            string name = reference[6..];
            Guid? id = Guid.TryParse(Path.GetFileNameWithoutExtension(name), out var g) ? g : null;
            var resolver = state.Options.ExternalFileResolver;
            return new FileDataReference(FileDataLocation.External, reference, id, ext, null, resolver is null ? null : () => resolver(name));
        }
        return new FileDataReference(FileDataLocation.Invalid, reference, null, ext, 0, null);
    }

    // --------------------------------------------------------- revisions, 2.1.8 / 2.1.9

    private static (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots, bool Encrypted)? Materialize(
        State state,
        ExGuid rid,
        Dictionary<ExGuid, Manifest> manifests,
        Dictionary<ExGuid, (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots, bool Encrypted)> cache)
    {
        if (cache.TryGetValue(rid, out var hit)) return hit;
        if (!manifests.ContainsKey(rid)) return null;

        var chain = new List<Manifest>();
        var seen = new HashSet<ExGuid>();
        var cursor = rid;
        (Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, ExGuid> Roots, bool Encrypted)? baseState = null;
        while (!cursor.IsNil && manifests.TryGetValue(cursor, out var m))
        {
            if (!seen.Add(cursor))
            {
                state.Warnings.Add($"Revision {rid} has a cyclic dependency chain.");
                break;
            }
            if (cache.TryGetValue(cursor, out var cached))
            {
                baseState = cached;
                break;
            }
            chain.Add(m);
            cursor = m.Dependent;
        }

        var objects = baseState is { } b ? new Dictionary<ExGuid, StoreObject>(b.Objects) : new Dictionary<ExGuid, StoreObject>();
        var roots = baseState is { } b2 ? new Dictionary<uint, ExGuid>(b2.Roots) : new Dictionary<uint, ExGuid>();
        bool encrypted = baseState?.Encrypted ?? false;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var m = chain[i];
            encrypted |= m.Encrypted;
            foreach (var d in m.Objects)
            {
                JcId jcid;
                if (d.JcId is { } j) jcid = j;
                else if (objects.TryGetValue(d.Id, out var previous)) jcid = previous.JcId;
                else jcid = new JcId(0x00020000);
                objects[d.Id] = new StoreObject(d.Id, jcid, d.Properties, d.FileData);
            }
            foreach (var (role, id) in m.Roots) roots[role] = id;
        }

        var result = (objects, roots, encrypted);
        cache[rid] = result;
        return result;
    }

    // ------------------------------------------------------------------------- helpers

    private static void CheckRange(State state, ChunkRef reference)
    {
        if (reference.Stp > (ulong)state.Data.Length || reference.Cb > (ulong)state.Data.Length - reference.Stp)
            throw new OneNoteFormatException($"Chunk ({reference.Stp}, {reference.Cb}) lies outside the {state.Data.Length}-byte file.");
    }

    private static ByteReader Slice(State state, ChunkRef reference)
    {
        CheckRange(state, reference);
        return new ByteReader(state.Data.AsSpan((int)reference.Stp, (int)reference.Cb));
    }

    private static ByteReader ReadAt(State state, FileNode node) => new(state.Data.AsSpan((int)node.DataStart, Math.Max(0, node.DataLength)));
}

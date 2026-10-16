// [MS-ONESTORE] 2.6.1 ObjectSpaceObjectPropSet, 2.6.2-2.6.5 the reference streams,
// 2.6.7 PropertySet, 2.6.8 prtFourBytesOfLengthFollowedByData, 2.6.9 prtArrayOfPropertyValues.

using System.Buffers.Binary;
using System.Text;

namespace XRay.Content.OneNote.Store;

/// <summary>The value of one property. Exactly one payload member is set, chosen by
/// <see cref="Type"/>; object, object-space and context references are already resolved to
/// their extended GUIDs.</summary>
public sealed class PropertyValue
{
    public PropertyType Type { get; }
    public bool Bool { get; private init; }
    public ulong Integer { get; private init; }
    public byte[]? Bytes { get; private init; }
    public IReadOnlyList<ExGuid>? ObjectIds { get; private init; }
    public IReadOnlyList<CellId>? ObjectSpaceIds { get; private init; }
    public IReadOnlyList<ExGuid>? ContextIds { get; private init; }
    public IReadOnlyList<PropertySet>? PropertySets { get; private init; }
    public PropertySet? PropertySet { get; private init; }

    private PropertyValue(PropertyType type) => Type = type;

    internal static PropertyValue NoData() => new(PropertyType.NoData);
    internal static PropertyValue OfBool(bool v) => new(PropertyType.Bool) { Bool = v };
    internal static PropertyValue OfInteger(PropertyType t, ulong v) => new(t) { Integer = v };
    internal static PropertyValue OfBytes(byte[] v) => new(PropertyType.FourBytesOfLengthFollowedByData) { Bytes = v };
    internal static PropertyValue OfObjects(PropertyType t, ExGuid[] v) => new(t) { ObjectIds = v };
    internal static PropertyValue OfObjectSpaces(PropertyType t, CellId[] v) => new(t) { ObjectSpaceIds = v };
    internal static PropertyValue OfContexts(PropertyType t, ExGuid[] v) => new(t) { ContextIds = v };
    internal static PropertyValue OfArray(PropertySet[] v) => new(PropertyType.ArrayOfPropertyValues) { PropertySets = v };
    internal static PropertyValue OfSet(PropertySet v) => new(PropertyType.PropertySet) { PropertySet = v };

    public override string ToString() => Type switch
    {
        PropertyType.NoData => "(no data)",
        PropertyType.Bool => Bool ? "true" : "false",
        PropertyType.FourBytesOfLengthFollowedByData => $"{Bytes!.Length} bytes",
        PropertyType.ObjectId or PropertyType.ArrayOfObjectIds => $"[{string.Join(", ", ObjectIds!)}]",
        PropertyType.ObjectSpaceId or PropertyType.ArrayOfObjectSpaceIds => $"[{string.Join(", ", ObjectSpaceIds!)}]",
        PropertyType.ContextId or PropertyType.ArrayOfContextIds => $"[{string.Join(", ", ContextIds!)}]",
        PropertyType.ArrayOfPropertyValues => $"{PropertySets!.Count} property sets",
        PropertyType.PropertySet => "property set",
        _ => $"0x{Integer:X}",
    };
}

/// <summary>One entry of a <see cref="PropertySet"/>.</summary>
public readonly record struct Property(PropertyId Id, PropertyValue Value);

/// <summary>[MS-ONESTORE] 2.6.7: an ordered collection of properties. Lookups match the 26-bit
/// property identity, so a caller can pass either the [MS-ONE] constant or the bare id.</summary>
public sealed class PropertySet
{
    private readonly Property[] _properties;

    internal PropertySet(Property[] properties) => _properties = properties;

    public static readonly PropertySet Empty = new(Array.Empty<Property>());

    public IReadOnlyList<Property> Properties => _properties;

    public PropertyValue? Get(uint propertyId)
    {
        uint id = propertyId & 0x3FFFFFF;
        foreach (var p in _properties)
            if (p.Id.Id == id) return p.Value;
        return null;
    }

    public bool Has(uint propertyId) => Get(propertyId) is not null;

    public bool? GetBool(uint id) => Get(id) is { Type: PropertyType.Bool } v ? v.Bool : null;

    public ulong? GetInteger(uint id) => Get(id) switch
    {
        { Type: PropertyType.OneByteOfData or PropertyType.TwoBytesOfData or PropertyType.FourBytesOfData or PropertyType.EightBytesOfData } v => v.Integer,
        _ => null,
    };

    public uint? GetUInt32(uint id) => GetInteger(id) is { } v ? (uint)v : null;

    /// <summary>A 4-byte property reinterpreted as an IEEE single, as [MS-ONE] stores lengths.</summary>
    public float? GetFloat(uint id) => Get(id) is { Type: PropertyType.FourBytesOfData } v
        ? BitConverter.UInt32BitsToSingle((uint)v.Integer)
        : null;

    public byte[]? GetBytes(uint id) => Get(id)?.Bytes;

    /// <summary>A WzInAtom: UTF-16LE, with any terminating NULs dropped.</summary>
    public string? GetString(uint id) => GetBytes(id) is { } b ? DecodeWz(b) : null;

    public Guid? GetGuid(uint id) => GetBytes(id) is { Length: >= 16 } b ? new Guid(b.AsSpan(0, 16)) : null;

    public IReadOnlyList<ExGuid> GetObjectIds(uint id) => Get(id)?.ObjectIds ?? Array.Empty<ExGuid>();

    public ExGuid? GetObjectId(uint id) => Get(id)?.ObjectIds is { Count: > 0 } ids ? ids[0] : null;

    public IReadOnlyList<CellId> GetObjectSpaceIds(uint id) => Get(id)?.ObjectSpaceIds ?? Array.Empty<CellId>();

    public IReadOnlyList<ExGuid> GetContextIds(uint id) => Get(id)?.ContextIds ?? Array.Empty<ExGuid>();

    public IReadOnlyList<PropertySet> GetPropertySets(uint id) => Get(id)?.PropertySets ?? Array.Empty<PropertySet>();

    /// <summary>An ArrayOfUINT32s property ([MS-ONE] 2.2.11).</summary>
    public uint[] GetUInt32Array(uint id)
    {
        var b = GetBytes(id);
        if (b is null) return Array.Empty<uint>();
        var result = new uint[b.Length / 4];
        for (int i = 0; i < result.Length; i++) result[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4));
        return result;
    }

    internal static string DecodeWz(byte[] bytes)
    {
        int len = bytes.Length & ~1;
        string s = Encoding.Unicode.GetString(bytes, 0, len);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}

/// <summary>Turns the CompactIDs of an object's reference streams into identities. The index is
/// the position of the reference within its stream, which is what the FSSHTTPB encoding keys on;
/// the revision-store encoding resolves the CompactID through a global identification table.</summary>
internal interface IReferenceResolver
{
    ExGuid ResolveObject(int index, uint compactId);
    CellId ResolveObjectSpace(int index, uint compactId);
    ExGuid ResolveContext(int index, uint compactId);
}

/// <summary>[MS-ONESTORE] 2.6.1 ObjectSpaceObjectPropSet parser.</summary>
internal static class ObjectPropSetParser
{
    private const int MaxDepth = 32;

    internal readonly record struct Streams(uint[] Oids, uint[] Osids, uint[] ContextIds);

    public static PropertySet Parse(ReadOnlySpan<byte> data, IReferenceResolver resolver)
    {
        var r = new ByteReader(data);
        var streams = ReadStreams(ref r);
        var cursor = new Cursor(streams, resolver);
        return ReadPropertySet(ref r, ref cursor, 0);
    }

    /// <summary>Reads only the three reference streams, for callers that must know how many
    /// references an object carries before they can resolve them.</summary>
    public static Streams ReadStreams(ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        return ReadStreams(ref r);
    }

    private static Streams ReadStreams(ref ByteReader r)
    {
        uint header = r.ReadU32();
        var oids = ReadIds(ref r, header);
        uint[] osids = Array.Empty<uint>();
        uint[] ctx = Array.Empty<uint>();
        if ((header & 0x80000000) == 0)
        {
            uint osHeader = r.ReadU32();
            osids = ReadIds(ref r, osHeader);
            if ((osHeader & 0x40000000) != 0)
            {
                uint ctxHeader = r.ReadU32();
                ctx = ReadIds(ref r, ctxHeader);
            }
        }
        return new Streams(oids, osids, ctx);
    }

    private static uint[] ReadIds(ref ByteReader r, uint header)
    {
        int count = (int)(header & 0xFFFFFF);
        if (count > r.Remaining / 4) throw new OneNoteFormatException($"Reference stream of {count} ids overruns the property set.");
        var ids = new uint[count];
        for (int i = 0; i < count; i++) ids[i] = r.ReadU32();
        return ids;
    }

    private struct Cursor
    {
        private readonly Streams _s;
        private readonly IReferenceResolver _resolver;
        private int _oid, _osid, _ctx;

        public Cursor(Streams s, IReferenceResolver resolver)
        {
            _s = s;
            _resolver = resolver;
        }

        public ExGuid[] TakeObjects(int n)
        {
            if (n < 0 || _oid + n > _s.Oids.Length) throw new OneNoteFormatException("Property set references more objects than its OID stream holds.");
            var result = new ExGuid[n];
            for (int i = 0; i < n; i++, _oid++) result[i] = _resolver.ResolveObject(_oid, _s.Oids[_oid]);
            return result;
        }

        public CellId[] TakeObjectSpaces(int n)
        {
            if (n < 0 || _osid + n > _s.Osids.Length) throw new OneNoteFormatException("Property set references more object spaces than its OSID stream holds.");
            var result = new CellId[n];
            for (int i = 0; i < n; i++, _osid++) result[i] = _resolver.ResolveObjectSpace(_osid, _s.Osids[_osid]);
            return result;
        }

        public ExGuid[] TakeContexts(int n)
        {
            if (n < 0 || _ctx + n > _s.ContextIds.Length) throw new OneNoteFormatException("Property set references more contexts than its context stream holds.");
            var result = new ExGuid[n];
            for (int i = 0; i < n; i++, _ctx++) result[i] = _resolver.ResolveContext(_ctx, _s.ContextIds[_ctx]);
            return result;
        }
    }

    private static PropertySet ReadPropertySet(ref ByteReader r, ref Cursor cursor, int depth)
    {
        if (depth > MaxDepth) throw new OneNoteFormatException("Property sets are nested too deeply.");
        int count = r.ReadU16();
        if (count > r.Remaining / 4) throw new OneNoteFormatException($"Property set declares {count} properties but holds fewer.");
        var ids = new PropertyId[count];
        for (int i = 0; i < count; i++) ids[i] = new PropertyId(r.ReadU32());

        var props = new Property[count];
        for (int i = 0; i < count; i++)
        {
            var id = ids[i];
            props[i] = new Property(id, ReadValue(ref r, ref cursor, id, depth));
        }
        return new PropertySet(props);
    }

    private static PropertyValue ReadValue(ref ByteReader r, ref Cursor cursor, PropertyId id, int depth)
    {
        switch (id.Type)
        {
            case PropertyType.NoData: return PropertyValue.NoData();
            case PropertyType.Bool: return PropertyValue.OfBool(id.BoolValue);
            case PropertyType.OneByteOfData: return PropertyValue.OfInteger(id.Type, r.ReadU8());
            case PropertyType.TwoBytesOfData: return PropertyValue.OfInteger(id.Type, r.ReadU16());
            case PropertyType.FourBytesOfData: return PropertyValue.OfInteger(id.Type, r.ReadU32());
            case PropertyType.EightBytesOfData: return PropertyValue.OfInteger(id.Type, r.ReadU64());
            case PropertyType.FourBytesOfLengthFollowedByData:
            {
                uint cb = r.ReadU32();
                if (cb > (uint)r.Remaining) throw new OneNoteFormatException($"Property {id} declares {cb} bytes but only {r.Remaining} remain.");
                return PropertyValue.OfBytes(r.ReadBytes((int)cb).ToArray());
            }
            case PropertyType.ObjectId: return PropertyValue.OfObjects(id.Type, cursor.TakeObjects(1));
            case PropertyType.ArrayOfObjectIds: return PropertyValue.OfObjects(id.Type, cursor.TakeObjects(CheckedCount(r.ReadU32())));
            case PropertyType.ObjectSpaceId: return PropertyValue.OfObjectSpaces(id.Type, cursor.TakeObjectSpaces(1));
            case PropertyType.ArrayOfObjectSpaceIds: return PropertyValue.OfObjectSpaces(id.Type, cursor.TakeObjectSpaces(CheckedCount(r.ReadU32())));
            case PropertyType.ContextId: return PropertyValue.OfContexts(id.Type, cursor.TakeContexts(1));
            case PropertyType.ArrayOfContextIds: return PropertyValue.OfContexts(id.Type, cursor.TakeContexts(CheckedCount(r.ReadU32())));
            case PropertyType.ArrayOfPropertyValues:
            {
                uint n = r.ReadU32();
                if (n == 0) return PropertyValue.OfArray(Array.Empty<PropertySet>());
                var prid = new PropertyId(r.ReadU32());
                if (prid.Type != PropertyType.PropertySet) throw new OneNoteFormatException($"Array property {id} holds type {prid.Type}, not property sets.");
                if (n > (uint)r.Remaining / 2) throw new OneNoteFormatException($"Array property {id} declares {n} elements but holds fewer.");
                var sets = new PropertySet[n];
                for (int i = 0; i < n; i++) sets[i] = ReadPropertySet(ref r, ref cursor, depth + 1);
                return PropertyValue.OfArray(sets);
            }
            case PropertyType.PropertySet: return PropertyValue.OfSet(ReadPropertySet(ref r, ref cursor, depth + 1));
            default: throw new OneNoteFormatException($"Property {id} has unknown type 0x{(int)id.Type:X}.");
        }
    }

    private static int CheckedCount(uint n) => n > 0xFFFFFF ? throw new OneNoteFormatException($"Reference array of {n} entries is implausible.") : (int)n;
}

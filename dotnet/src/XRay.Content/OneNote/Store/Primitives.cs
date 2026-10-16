// [MS-ONESTORE] 2.2.1 ExtendedGUID, 2.6.6 PropertyID, 2.6.14 JCID;
// [MS-FSSHTTPB] 2.2.1.10 Cell ID.

using System.Buffers.Binary;

namespace XRay.Content.OneNote.Store;

/// <summary>An [MS-ONESTORE] ExtendedGUID: a GUID plus a 32-bit discriminator.</summary>
public readonly record struct ExGuid(Guid Guid, uint N)
{
    public static readonly ExGuid Nil = default;

    public bool IsNil => Guid == Guid.Empty && N == 0;

    public static ExGuid operator ^(ExGuid a, ExGuid b) => new(XorGuid(a.Guid, b.Guid), a.N ^ b.N);

    internal static Guid XorGuid(Guid a, Guid b)
    {
        Span<byte> x = stackalloc byte[16];
        Span<byte> y = stackalloc byte[16];
        a.TryWriteBytes(x);
        b.TryWriteBytes(y);
        for (int i = 0; i < 16; i++) x[i] ^= y[i];
        return new Guid(x);
    }

    public override string ToString() => $"{{{Guid:B},{N}}}";
}

/// <summary>An [MS-FSSHTTPB] cell id: the (context, object space) pair that addresses one
/// revision of an object space. In the revision-store format the default context is
/// <see cref="ExGuid.Nil"/>.</summary>
public readonly record struct CellId(ExGuid Context, ExGuid ObjectSpace)
{
    public override string ToString() => $"({Context}, {ObjectSpace})";
}

/// <summary>[MS-ONESTORE] 2.6.14: the type of an object and of the data it carries.</summary>
public readonly record struct JcId(uint Value)
{
    public ushort Index => (ushort)(Value & 0xFFFF);
    public bool IsBinary => (Value & 0x10000) != 0;
    public bool IsPropertySet => (Value & 0x20000) != 0;
    public bool IsGraphNode => (Value & 0x40000) != 0;
    public bool IsFileData => (Value & 0x80000) != 0;
    public bool IsReadOnly => (Value & 0x100000) != 0;

    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>[MS-ONESTORE] 2.6.6 PropertyID.type.</summary>
public enum PropertyType : byte
{
    NoData = 0x1,
    Bool = 0x2,
    OneByteOfData = 0x3,
    TwoBytesOfData = 0x4,
    FourBytesOfData = 0x5,
    EightBytesOfData = 0x6,
    FourBytesOfLengthFollowedByData = 0x7,
    ObjectId = 0x8,
    ArrayOfObjectIds = 0x9,
    ObjectSpaceId = 0xA,
    ArrayOfObjectSpaceIds = 0xB,
    ContextId = 0xC,
    ArrayOfContextIds = 0xD,
    ArrayOfPropertyValues = 0x10,
    PropertySet = 0x11,
}

/// <summary>[MS-ONESTORE] 2.6.6: a property's identity (26 bits), its type (5 bits) and, for
/// <see cref="PropertyType.Bool"/>, its value (1 bit).</summary>
public readonly record struct PropertyId(uint Value)
{
    public uint Id => Value & 0x3FFFFFF;
    public PropertyType Type => (PropertyType)((Value >> 26) & 0x1F);
    public bool BoolValue => (Value & 0x80000000) != 0;

    /// <summary>The value without the bool bit — the form [MS-ONE] tabulates.</summary>
    public uint Key => Value & 0x7FFFFFFF;

    public override string ToString() => OneNotePropertyNames.NameOf(Key) is { } name ? name : $"0x{Key:X8}";
}

/// <summary>Thrown when a OneNote file is not well formed.</summary>
public sealed class OneNoteFormatException : Exception
{
    public OneNoteFormatException(string message) : base(message) { }
    public OneNoteFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a section is password protected; its content is encrypted and the
/// store carries no key material a reader can use.</summary>
public sealed class OneNoteEncryptedException : Exception
{
    public OneNoteEncryptedException(string message) : base(message) { }
}

/// <summary>Bounds-checked little-endian cursor over a byte buffer.</summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ByteReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position
    {
        readonly get => _pos;
        set
        {
            if (value < 0 || value > _data.Length) throw new OneNoteFormatException($"Seek to {value} outside a {_data.Length}-byte buffer.");
            _pos = value;
        }
    }

    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - _pos;

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > Remaining) throw new OneNoteFormatException($"Read of {count} bytes at {_pos} overruns a {_data.Length}-byte buffer.");
        var span = _data.Slice(_pos, count);
        _pos += count;
        return span;
    }

    public void Skip(int count) => ReadBytes(count);

    public readonly byte PeekU8()
    {
        if (Remaining < 1) throw new OneNoteFormatException($"Read at {_pos} overruns a {_data.Length}-byte buffer.");
        return _data[_pos];
    }

    public byte ReadU8() => ReadBytes(1)[0];
    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
    public Guid ReadGuid() => new(ReadBytes(16));
    public ExGuid ReadExGuid() => new(ReadGuid(), ReadU32());

    public ulong ReadUIntOfSize(int size) => size switch
    {
        1 => ReadU8(),
        2 => ReadU16(),
        4 => ReadU32(),
        8 => ReadU64(),
        _ => throw new OneNoteFormatException($"Unsupported integer size {size}."),
    };
}

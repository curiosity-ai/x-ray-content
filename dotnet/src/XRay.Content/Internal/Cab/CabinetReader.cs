// Managed Microsoft Cabinet (.cab) reader — implements the container layout of [MS-CAB]
// "Cabinet File Format" (CFHEADER / CFFOLDER / CFFILE / CFDATA) and the three codecs a cabinet
// folder can carry: stored (type 0), MSZIP (type 1, [MS-MCI] — a deflate stream per CFDATA block
// whose 32 KB dictionary carries across the blocks of a folder, RFC 1951) and LZX (type 3,
// [MS-PATCH] "LZX DELTA Compression and Decompression", see LzxDecoder). Quantum (type 2) is
// recognised but not decoded. OneNote `.onepkg` notebook packages are cabinets, which is the
// reason this reader exists.

using System.Text;
using System.Text.Unicode;
using XRay.Content.Internal.Cfb;

namespace XRay.Content.Internal.Cab;

/// <summary>The compression method of one cabinet folder (the low nibble of <c>typeCompress</c>).</summary>
internal enum CabinetCompression
{
    /// <summary>Stored: every CFDATA block holds its bytes verbatim.</summary>
    None = 0,

    /// <summary>MSZIP: "CK" plus a deflate stream per CFDATA block.</summary>
    MsZip = 1,

    /// <summary>Quantum: listed, but its contents cannot be extracted.</summary>
    Quantum = 2,

    /// <summary>LZX with a window of 2^15 to 2^21 bytes.</summary>
    Lzx = 3,
}

/// <summary>CFFILE attribute bits.</summary>
[Flags]
internal enum CabinetFileAttributes : ushort
{
    None = 0,
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    Archive = 0x20,
    Execute = 0x40,
    NameIsUtf8 = 0x80,
}

/// <summary>One CFFOLDER: a run of CFDATA blocks compressed as a single stream.</summary>
internal sealed class CabinetFolder
{
    internal CabinetFolder(
        int index,
        CabinetCompression compression,
        int lzxWindowBits,
        ushort rawCompressionType,
        CabinetDataBlock[] blocks,
        long uncompressedSize,
        bool isSplit)
    {
        Index = index;
        Compression = compression;
        LzxWindowBits = lzxWindowBits;
        RawCompressionType = rawCompressionType;
        Blocks = blocks;
        UncompressedSize = uncompressedSize;
        IsSplit = isSplit;
    }

    /// <summary>Position of the folder in the cabinet's folder table.</summary>
    public int Index { get; }

    /// <summary>The folder's compression method.</summary>
    public CabinetCompression Compression { get; }

    /// <summary>log2 of the LZX window size (15..21); 0 for the other methods.</summary>
    public int LzxWindowBits { get; }

    /// <summary>The folder's <c>typeCompress</c> field as stored.</summary>
    public ushort RawCompressionType { get; }

    /// <summary>Number of CFDATA blocks in the folder.</summary>
    public int BlockCount => Blocks.Length;

    /// <summary>Sum of the CFDATA blocks' uncompressed sizes.</summary>
    public long UncompressedSize { get; }

    /// <summary>True when the folder continues in another cabinet of a set, so it cannot be decoded alone.</summary>
    public bool IsSplit { get; }

    internal CabinetDataBlock[] Blocks { get; }
}

/// <summary>One CFDATA block: where its compressed bytes are and how many bytes they expand to.</summary>
internal readonly record struct CabinetDataBlock(int DataOffset, int CompressedSize, int UncompressedSize);

/// <summary>One CFFILE: a file stored at a byte range of a folder's uncompressed stream.</summary>
internal sealed class CabinetEntry
{
    internal CabinetEntry(
        string path,
        long size,
        DateTime? lastModified,
        CabinetFileAttributes attributes,
        int folderIndex,
        long folderOffset,
        bool isContinued)
    {
        Path = path;
        Size = size;
        LastModified = lastModified;
        Attributes = attributes;
        FolderIndex = folderIndex;
        FolderOffset = folderOffset;
        IsContinued = isContinued;
    }

    /// <summary>The stored name with <c>\</c> separators turned into <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>Uncompressed size in bytes (<c>cbFile</c>).</summary>
    public long Size { get; }

    /// <summary>The DOS date and time of the entry, or null when the stored value is not a valid date.</summary>
    public DateTime? LastModified { get; }

    /// <summary>The CFFILE attribute bits.</summary>
    public CabinetFileAttributes Attributes { get; }

    /// <summary>Index of the folder holding the file, or -1 when it lives in another cabinet of a set.</summary>
    public int FolderIndex { get; }

    /// <summary>Offset of the file in its folder's uncompressed stream (<c>uoffFolderStart</c>).</summary>
    public long FolderOffset { get; }

    /// <summary>True when the file is continued from or into another cabinet of a set.</summary>
    public bool IsContinued { get; }
}

/// <summary>A parsed cabinet: its folders and files, and the decompressed bytes of each file on demand.</summary>
internal sealed class CabinetArchive
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _cachedFolder = -1;
    private byte[]? _cachedFolderData;

    internal CabinetArchive(
        ReadOnlyMemory<byte> data,
        ushort setId,
        ushort setIndex,
        string? previousCabinet,
        string? nextCabinet,
        IReadOnlyList<CabinetFolder> folders,
        IReadOnlyList<CabinetEntry> entries)
    {
        _data = data;
        SetId = setId;
        SetIndex = setIndex;
        PreviousCabinet = previousCabinet;
        NextCabinet = nextCabinet;
        Folders = folders;
        Entries = entries;
    }

    /// <summary>The <c>setID</c> shared by the cabinets of a multi-cabinet set.</summary>
    public ushort SetId { get; }

    /// <summary>The position of this cabinet in its set (<c>iCabinet</c>).</summary>
    public ushort SetIndex { get; }

    /// <summary>File name of the previous cabinet in the set, when the header names one.</summary>
    public string? PreviousCabinet { get; }

    /// <summary>File name of the next cabinet in the set, when the header names one.</summary>
    public string? NextCabinet { get; }

    /// <summary>The folders, in header order.</summary>
    public IReadOnlyList<CabinetFolder> Folders { get; }

    /// <summary>The files, in header order.</summary>
    public IReadOnlyList<CabinetEntry> Entries { get; }

    /// <summary>
    /// Decompress one entry. The entry's folder is decoded once and kept until an entry of
    /// another folder is read, so reading every file of a folder in turn decodes it once.
    /// </summary>
    /// <exception cref="InvalidDataException">The compressed data is corrupt.</exception>
    /// <exception cref="NotSupportedException">The folder is Quantum-compressed or spans cabinets.</exception>
    public byte[] Extract(CabinetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsContinued || entry.FolderIndex < 0)
        {
            throw new NotSupportedException($"Cabinet entry '{entry.Path}' spans several cabinets of a set and cannot be extracted from this one alone.");
        }

        if (entry.Size == 0) return Array.Empty<byte>();

        var folderData = DecompressFolder(entry.FolderIndex);
        if (entry.FolderOffset + entry.Size > folderData.Length)
        {
            throw new InvalidDataException($"Cabinet entry '{entry.Path}' lies outside its folder's data.");
        }

        return folderData.AsSpan((int)entry.FolderOffset, (int)entry.Size).ToArray();
    }

    /// <summary>Decompress a whole folder's stream.</summary>
    public byte[] DecompressFolder(int folderIndex)
    {
        if ((uint)folderIndex >= (uint)Folders.Count) throw new ArgumentOutOfRangeException(nameof(folderIndex));
        if (_cachedFolder == folderIndex && _cachedFolderData is not null) return _cachedFolderData;

        var folder = Folders[folderIndex];
        if (folder.IsSplit)
        {
            throw new NotSupportedException($"Cabinet folder {folderIndex} continues in another cabinet of a set and cannot be decoded alone.");
        }

        var output = new byte[folder.UncompressedSize];
        var data = _data.Span;
        switch (folder.Compression)
        {
            case CabinetCompression.None:
            {
                int pos = 0;
                foreach (var block in folder.Blocks)
                {
                    if (block.CompressedSize != block.UncompressedSize)
                    {
                        throw new InvalidDataException("Stored cabinet block has differing compressed and uncompressed sizes.");
                    }

                    data.Slice(block.DataOffset, block.CompressedSize).CopyTo(output.AsSpan(pos));
                    pos += block.UncompressedSize;
                }

                break;
            }
            case CabinetCompression.MsZip:
            {
                var decoder = new MsZipDecoder(output);
                foreach (var block in folder.Blocks)
                {
                    decoder.DecodeBlock(data.Slice(block.DataOffset, block.CompressedSize), block.UncompressedSize);
                }

                break;
            }
            case CabinetCompression.Lzx:
            {
                var decoder = new LzxDecoder(folder.LzxWindowBits, output);
                foreach (var block in folder.Blocks)
                {
                    decoder.DecodeBlock(data.Slice(block.DataOffset, block.CompressedSize), block.UncompressedSize);
                }

                decoder.Finish();
                break;
            }
            case CabinetCompression.Quantum:
                throw new NotSupportedException("Quantum-compressed cabinet folders are not supported.");
            default:
                throw new InvalidDataException($"Unknown cabinet compression type 0x{folder.RawCompressionType:X4}.");
        }

        _cachedFolder = folderIndex;
        _cachedFolderData = output;
        return output;
    }
}

/// <summary>Parses Microsoft Cabinet files ([MS-CAB]).</summary>
internal sealed class CabinetReader
{
    /// <summary>Default cap on the declared uncompressed size of a cabinet (1 GiB).</summary>
    public const long DefaultMaxTotalUncompressed = 1L << 30;

    private const ushort FlagPrevCabinet = 0x0001;
    private const ushort FlagNextCabinet = 0x0002;
    private const ushort FlagReservePresent = 0x0004;

    private const ushort FolderContinuedFromPrev = 0xFFFD;
    private const ushort FolderContinuedToNext = 0xFFFE;
    private const ushort FolderContinuedPrevAndNext = 0xFFFF;

    private const int HeaderSize = 36;
    private const int MaxBlockUncompressed = 32768;
    private const int MaxNameBytes = 256;

    private CabinetReader() { }

    /// <summary>True when <paramref name="data"/> starts with the cabinet signature <c>MSCF</c>.</summary>
    public static bool IsCabinet(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'M' && data[1] == (byte)'S' && data[2] == (byte)'C' && data[3] == (byte)'F';

    /// <summary>Parse a cabinet held in a span; the bytes are copied.</summary>
    /// <inheritdoc cref="Read(ReadOnlyMemory{byte}, long)"/>
    public static CabinetArchive Read(ReadOnlySpan<byte> data, long maxTotalUncompressed = DefaultMaxTotalUncompressed) =>
        Read(new ReadOnlyMemory<byte>(data.ToArray()), maxTotalUncompressed);

    /// <summary>Parse a cabinet held in an array, without copying it.</summary>
    /// <inheritdoc cref="Read(ReadOnlyMemory{byte}, long)"/>
    public static CabinetArchive Read(byte[] data, long maxTotalUncompressed = DefaultMaxTotalUncompressed) =>
        Read(new ReadOnlyMemory<byte>(data), maxTotalUncompressed);

    /// <summary>
    /// Parse a cabinet's header, folder, file and data-block tables. Nothing is decompressed until
    /// <see cref="CabinetArchive.Extract"/> is called.
    /// </summary>
    /// <param name="data">The whole cabinet. It must stay unchanged while the archive is used.</param>
    /// <param name="maxTotalUncompressed">
    /// Upper bound on both the folders' declared uncompressed size and the files' total size; a
    /// cabinet declaring more is rejected before anything is allocated.
    /// </param>
    /// <exception cref="InvalidDataException">The data is not a cabinet, is truncated or is inconsistent, or exceeds the size limit.</exception>
    public static CabinetArchive Read(ReadOnlyMemory<byte> data, long maxTotalUncompressed = DefaultMaxTotalUncompressed)
    {
        var span = data.Span;
        if (!IsCabinet(span)) throw new InvalidDataException("Not a cabinet file: missing 'MSCF' signature.");
        if (span.Length < HeaderSize) throw new InvalidDataException("Cabinet header is truncated.");

        uint coffFiles = ReadU32(span, 16);
        byte versionMajor = span[25];
        ushort folderCount = ReadU16(span, 26);
        ushort fileCount = ReadU16(span, 28);
        ushort flags = ReadU16(span, 30);
        ushort setId = ReadU16(span, 32);
        ushort setIndex = ReadU16(span, 34);
        if (versionMajor != 1) throw new InvalidDataException($"Unsupported cabinet format version {versionMajor}.{span[24]}.");

        int pos = HeaderSize;
        int folderReserve = 0;
        int dataReserve = 0;
        if ((flags & FlagReservePresent) != 0)
        {
            Need(span, pos, 4, "CFHEADER reserve sizes");
            int headerReserve = ReadU16(span, pos);
            folderReserve = span[pos + 2];
            dataReserve = span[pos + 3];
            pos += 4;
            Need(span, pos, headerReserve, "CFHEADER reserve area");
            pos += headerReserve;
        }

        string? previousCabinet = null;
        string? nextCabinet = null;
        if ((flags & FlagPrevCabinet) != 0)
        {
            previousCabinet = ReadName(span, ref pos, utf8: false);
            ReadName(span, ref pos, utf8: false);
        }

        if ((flags & FlagNextCabinet) != 0)
        {
            nextCabinet = ReadName(span, ref pos, utf8: false);
            ReadName(span, ref pos, utf8: false);
        }

        var folders = new CabinetFolder[folderCount];
        long totalUncompressed = 0;
        for (int i = 0; i < folderCount; i++)
        {
            Need(span, pos, 8 + folderReserve, "CFFOLDER");
            uint coffCabStart = ReadU32(span, pos);
            ushort dataCount = ReadU16(span, pos + 4);
            ushort typeCompress = ReadU16(span, pos + 6);
            pos += 8 + folderReserve;

            var (compression, windowBits) = ParseCompression(typeCompress);
            var (blocks, size, isSplit) = ReadDataBlocks(span, coffCabStart, dataCount, dataReserve);
            totalUncompressed += size;
            if (totalUncompressed > maxTotalUncompressed)
            {
                throw new InvalidDataException($"Cabinet declares more than {maxTotalUncompressed} uncompressed bytes.");
            }

            if (size > Array.MaxLength) throw new InvalidDataException("Cabinet folder is too large to decode.");
            folders[i] = new CabinetFolder(i, compression, windowBits, typeCompress, blocks, size, isSplit);
        }

        var entries = new CabinetEntry[fileCount];
        long totalFileBytes = 0;
        pos = checked((int)Math.Min(coffFiles, int.MaxValue));
        for (int i = 0; i < fileCount; i++)
        {
            Need(span, pos, 16, "CFFILE");
            uint size = ReadU32(span, pos);
            uint folderOffset = ReadU32(span, pos + 4);
            ushort folderIndex = ReadU16(span, pos + 8);
            ushort date = ReadU16(span, pos + 10);
            ushort time = ReadU16(span, pos + 12);
            var attributes = (CabinetFileAttributes)ReadU16(span, pos + 14);
            pos += 16;
            string name = ReadName(span, ref pos, (attributes & CabinetFileAttributes.NameIsUtf8) != 0);

            totalFileBytes += size;
            if (totalFileBytes > maxTotalUncompressed)
            {
                throw new InvalidDataException($"Cabinet files total more than {maxTotalUncompressed} bytes.");
            }

            int resolved;
            bool continued = false;
            switch (folderIndex)
            {
                case FolderContinuedFromPrev:
                    resolved = folderCount > 0 ? 0 : -1;
                    continued = true;
                    break;
                case FolderContinuedToNext:
                case FolderContinuedPrevAndNext:
                    resolved = folderCount > 0 ? folderCount - 1 : -1;
                    continued = true;
                    break;
                default:
                    if (folderIndex >= folderCount)
                    {
                        throw new InvalidDataException($"Cabinet file '{name}' names folder {folderIndex} of {folderCount}.");
                    }

                    resolved = folderIndex;
                    var folder = folders[resolved];
                    if (!folder.IsSplit && (long)folderOffset + size > folder.UncompressedSize)
                    {
                        throw new InvalidDataException($"Cabinet file '{name}' lies outside its folder's data.");
                    }

                    break;
            }

            entries[i] = new CabinetEntry(
                name.Replace('\\', '/'),
                size,
                DosDateTime(date, time),
                attributes,
                resolved,
                folderOffset,
                continued);
        }

        return new CabinetArchive(data, setId, setIndex, previousCabinet, nextCabinet, folders, entries);
    }

    private static (CabinetCompression Compression, int WindowBits) ParseCompression(ushort typeCompress)
    {
        switch (typeCompress & 0x000F)
        {
            case 0: return (CabinetCompression.None, 0);
            case 1: return (CabinetCompression.MsZip, 0);
            case 2: return (CabinetCompression.Quantum, 0);
            case 3:
                int bits = (typeCompress >> 8) & 0x1F;
                if (bits < LzxDecoder.MinWindowBits || bits > LzxDecoder.MaxWindowBits)
                {
                    throw new InvalidDataException($"Invalid LZX window size 2^{bits} in cabinet folder.");
                }

                return (CabinetCompression.Lzx, bits);
            default:
                throw new InvalidDataException($"Unknown cabinet compression type 0x{typeCompress:X4}.");
        }
    }

    private static (CabinetDataBlock[] Blocks, long Size, bool IsSplit) ReadDataBlocks(
        ReadOnlySpan<byte> span,
        uint coffCabStart,
        int count,
        int dataReserve)
    {
        if (coffCabStart > (uint)span.Length) throw new InvalidDataException("Cabinet folder data starts past the end of the file.");

        var blocks = new List<CabinetDataBlock>(count);
        int pos = (int)coffCabStart;
        long size = 0;
        bool isSplit = false;
        for (int i = 0; i < count; i++)
        {
            Need(span, pos, 8 + dataReserve, "CFDATA");
            int compressed = ReadU16(span, pos + 4);
            int uncompressed = ReadU16(span, pos + 6);
            pos += 8 + dataReserve;
            Need(span, pos, compressed, "CFDATA payload");
            if (uncompressed > MaxBlockUncompressed)
            {
                throw new InvalidDataException($"Cabinet data block expands to {uncompressed} bytes, more than {MaxBlockUncompressed}.");
            }

            if (uncompressed == 0)
            {
                isSplit = true;
                break;
            }

            blocks.Add(new CabinetDataBlock(pos, compressed, uncompressed));
            pos += compressed;
            size += uncompressed;
        }

        return (blocks.ToArray(), size, isSplit);
    }

    private static string ReadName(ReadOnlySpan<byte> span, ref int pos, bool utf8)
    {
        if (pos < 0 || pos >= span.Length) throw new InvalidDataException("Cabinet name is truncated.");
        var rest = span.Slice(pos, Math.Min(span.Length - pos, MaxNameBytes + 1));
        int len = rest.IndexOf((byte)0);
        if (len < 0) throw new InvalidDataException("Cabinet name is not terminated.");
        var bytes = rest[..len];
        pos += len + 1;
        return DecodeName(bytes, utf8);
    }

    /// <summary>
    /// Decode a stored name: UTF-8 when the entry says so, and also when the bytes are valid
    /// UTF-8 (OneNote writes UTF-8 names without setting the flag); Windows-1252 otherwise.
    /// </summary>
    internal static string DecodeName(ReadOnlySpan<byte> bytes, bool utf8)
    {
        if (utf8 || Utf8.IsValid(bytes)) return Encoding.UTF8.GetString(bytes);

        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes) sb.Append(OleUtil.Cp1252ToChar(b));
        return sb.ToString();
    }

    private static DateTime? DosDateTime(ushort date, ushort time)
    {
        int day = date & 0x1F;
        int month = (date >> 5) & 0x0F;
        int year = 1980 + (date >> 9);
        int second = (time & 0x1F) * 2;
        int minute = (time >> 5) & 0x3F;
        int hour = time >> 11;
        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        if (hour > 23 || minute > 59 || second > 59) return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    private static void Need(ReadOnlySpan<byte> span, int pos, int count, string what)
    {
        if (pos < 0 || count < 0 || (long)pos + count > span.Length)
        {
            throw new InvalidDataException($"Cabinet is truncated: {what} extends past the end of the file.");
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> span, int pos) => (ushort)(span[pos] | (span[pos + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> span, int pos) =>
        (uint)(span[pos] | (span[pos + 1] << 8) | (span[pos + 2] << 16) | (span[pos + 3] << 24));
}

/// <summary>
/// MSZIP decoder ([MS-MCI]): each CFDATA block is <c>"CK"</c> followed by a complete deflate
/// stream (RFC 1951), and every block may refer back into the previous 32 KB of the folder's
/// output. The folder's output buffer is the history, so the dictionary carries across blocks.
/// </summary>
internal sealed class MsZipDecoder
{
    private const int MaxDistance = 32768;
    private const int MaxCodeBits = 15;

    private static readonly ushort[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    };

    private static readonly byte[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    private static readonly ushort[] DistanceBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
        4097, 6145, 8193, 12289, 16385, 24577,
    };

    private static readonly byte[] DistanceExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    };

    private static readonly byte[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private static readonly Lazy<(HuffmanTable Literal, HuffmanTable Distance)> FixedTables = new(BuildFixedTables);

    private readonly byte[] _output;
    private int _outPos;

    private ulong _bitBuf;
    private int _bitCount;
    private int _inPos;
    private int _inEnd;

    /// <summary>Create a decoder that appends to <paramref name="output"/>, the whole folder's buffer.</summary>
    public MsZipDecoder(byte[] output)
    {
        _output = output;
    }

    /// <summary>Decode one CFDATA block's payload, which must expand to exactly <paramref name="uncompressedSize"/> bytes.</summary>
    public void DecodeBlock(ReadOnlySpan<byte> block, int uncompressedSize)
    {
        if (block.Length < 2 || block[0] != (byte)'C' || block[1] != (byte)'K')
        {
            throw new InvalidDataException("MSZIP block is missing its 'CK' signature.");
        }

        int blockEnd = _outPos + uncompressedSize;
        if (blockEnd > _output.Length) throw new InvalidDataException("MSZIP block expands past its folder's size.");

        _bitBuf = 0;
        _bitCount = 0;
        _inPos = 2;
        _inEnd = block.Length;

        bool final;
        do
        {
            final = ReadBits(block, 1) == 1;
            uint type = ReadBits(block, 2);
            switch (type)
            {
                case 0: CopyStored(block, blockEnd); break;
                case 1:
                {
                    var (literal, distance) = FixedTables.Value;
                    Inflate(block, literal, distance, blockEnd);
                    break;
                }
                case 2:
                {
                    var (literal, distance) = ReadDynamicTables(block);
                    Inflate(block, literal, distance, blockEnd);
                    break;
                }
                default:
                    throw new InvalidDataException("MSZIP block uses the reserved deflate block type 3.");
            }

            if (ConsumedPastEnd) throw new InvalidDataException("MSZIP block is truncated.");
        }
        while (!final);

        if (_outPos != blockEnd)
        {
            throw new InvalidDataException($"MSZIP block expanded to {_outPos - (blockEnd - uncompressedSize)} bytes, expected {uncompressedSize}.");
        }
    }

    private bool ConsumedPastEnd => (long)_inPos * 8 - _bitCount > (long)_inEnd * 8;

    private void CopyStored(ReadOnlySpan<byte> block, int blockEnd)
    {
        _bitBuf >>= _bitCount & 7;
        _bitCount -= _bitCount & 7;
        _inPos -= _bitCount / 8;
        _bitBuf = 0;
        _bitCount = 0;

        if (_inPos + 4 > _inEnd) throw new InvalidDataException("MSZIP stored block is truncated.");
        int len = block[_inPos] | (block[_inPos + 1] << 8);
        int nlen = block[_inPos + 2] | (block[_inPos + 3] << 8);
        _inPos += 4;
        if ((len ^ 0xFFFF) != nlen) throw new InvalidDataException("MSZIP stored block length check failed.");
        if (_inPos + len > _inEnd) throw new InvalidDataException("MSZIP stored block is truncated.");
        if (_outPos + len > blockEnd) throw new InvalidDataException("MSZIP block expands past its declared size.");

        block.Slice(_inPos, len).CopyTo(_output.AsSpan(_outPos));
        _inPos += len;
        _outPos += len;
    }

    private void Inflate(ReadOnlySpan<byte> block, HuffmanTable literal, HuffmanTable distance, int blockEnd)
    {
        while (true)
        {
            int sym = Decode(block, literal);
            if (sym < 256)
            {
                if (_outPos >= blockEnd) throw new InvalidDataException("MSZIP block expands past its declared size.");
                _output[_outPos++] = (byte)sym;
                continue;
            }

            if (sym == 256) return;

            sym -= 257;
            if (sym >= LengthBase.Length) throw new InvalidDataException("MSZIP stream has an invalid length code.");
            int length = LengthBase[sym] + (int)ReadBits(block, LengthExtra[sym]);

            int dsym = Decode(block, distance);
            if (dsym >= DistanceBase.Length) throw new InvalidDataException("MSZIP stream has an invalid distance code.");
            int dist = DistanceBase[dsym] + (int)ReadBits(block, DistanceExtra[dsym]);

            if (dist > _outPos || dist > MaxDistance) throw new InvalidDataException("MSZIP match refers before the start of the data.");
            if (_outPos + length > blockEnd) throw new InvalidDataException("MSZIP block expands past its declared size.");

            CopyMatch(_output, _outPos, dist, length);
            _outPos += length;

            if (ConsumedPastEnd) throw new InvalidDataException("MSZIP block is truncated.");
        }
    }

    private (HuffmanTable Literal, HuffmanTable Distance) ReadDynamicTables(ReadOnlySpan<byte> block)
    {
        int hlit = (int)ReadBits(block, 5) + 257;
        int hdist = (int)ReadBits(block, 5) + 1;
        int hclen = (int)ReadBits(block, 4) + 4;
        if (hlit > 286 || hdist > 30) throw new InvalidDataException("MSZIP dynamic block declares too many codes.");

        var codeLengthLengths = new byte[19];
        for (int i = 0; i < hclen; i++) codeLengthLengths[CodeLengthOrder[i]] = (byte)ReadBits(block, 3);
        var codeLengthTable = HuffmanTable.Build(codeLengthLengths, codeLengthLengths.Length);

        var lengths = new byte[hlit + hdist];
        int n = 0;
        while (n < lengths.Length)
        {
            int sym = Decode(block, codeLengthTable);
            if (sym < 16)
            {
                lengths[n++] = (byte)sym;
                continue;
            }

            int repeat;
            byte value = 0;
            switch (sym)
            {
                case 16:
                    if (n == 0) throw new InvalidDataException("MSZIP code lengths repeat a length before the first.");
                    value = lengths[n - 1];
                    repeat = 3 + (int)ReadBits(block, 2);
                    break;
                case 17: repeat = 3 + (int)ReadBits(block, 3); break;
                default: repeat = 11 + (int)ReadBits(block, 7); break;
            }

            if (n + repeat > lengths.Length) throw new InvalidDataException("MSZIP code lengths overrun the table.");
            lengths.AsSpan(n, repeat).Fill(value);
            n += repeat;
        }

        if (lengths[256] == 0) throw new InvalidDataException("MSZIP dynamic block has no end-of-block code.");

        var literal = HuffmanTable.Build(lengths.AsSpan(0, hlit), hlit);
        var distance = HuffmanTable.Build(lengths.AsSpan(hlit, hdist), hdist);
        return (literal, distance);
    }

    private static (HuffmanTable, HuffmanTable) BuildFixedTables()
    {
        var lit = new byte[288];
        lit.AsSpan(0, 144).Fill(8);
        lit.AsSpan(144, 112).Fill(9);
        lit.AsSpan(256, 24).Fill(7);
        lit.AsSpan(280, 8).Fill(8);
        var dist = new byte[30];
        dist.AsSpan().Fill(5);
        return (HuffmanTable.Build(lit, lit.Length), HuffmanTable.Build(dist, dist.Length));
    }

    private int Decode(ReadOnlySpan<byte> block, HuffmanTable table)
    {
        if (table.Bits == 0) throw new InvalidDataException("MSZIP stream uses an empty Huffman table.");
        Fill(block, table.Bits);
        int entry = table.Entries[(int)(_bitBuf & ((1UL << table.Bits) - 1))];
        if (entry == 0) throw new InvalidDataException("MSZIP stream has an invalid Huffman code.");
        int len = entry >> 16;
        _bitBuf >>= len;
        _bitCount -= len;
        return entry & 0xFFFF;
    }

    private uint ReadBits(ReadOnlySpan<byte> block, int count)
    {
        if (count == 0) return 0;
        Fill(block, count);
        uint value = (uint)(_bitBuf & ((1UL << count) - 1));
        _bitBuf >>= count;
        _bitCount -= count;
        return value;
    }

    private void Fill(ReadOnlySpan<byte> block, int count)
    {
        while (_bitCount < count)
        {
            ulong b = _inPos < _inEnd ? block[_inPos] : 0UL;
            _inPos++;
            _bitBuf |= b << _bitCount;
            _bitCount += 8;
        }
    }

    internal static void CopyMatch(byte[] buffer, int pos, int distance, int length)
    {
        if (distance >= length)
        {
            Buffer.BlockCopy(buffer, pos - distance, buffer, pos, length);
            return;
        }

        int src = pos - distance;
        for (int i = 0; i < length; i++) buffer[pos + i] = buffer[src + i];
    }

    /// <summary>A canonical Huffman decoding table indexed by bit-reversed (LSB-first) codes.</summary>
    private sealed class HuffmanTable
    {
        private HuffmanTable(int bits, int[] entries)
        {
            Bits = bits;
            Entries = entries;
        }

        public int Bits { get; }

        public int[] Entries { get; }

        public static HuffmanTable Build(ReadOnlySpan<byte> lengths, int count)
        {
            Span<int> counts = stackalloc int[MaxCodeBits + 1];
            int maxLen = 0;
            for (int i = 0; i < count; i++)
            {
                counts[lengths[i]]++;
                if (lengths[i] > maxLen) maxLen = lengths[i];
            }

            if (maxLen == 0) return new HuffmanTable(0, Array.Empty<int>());

            counts[0] = 0;
            int left = 1;
            Span<int> next = stackalloc int[MaxCodeBits + 2];
            int code = 0;
            for (int len = 1; len <= MaxCodeBits; len++)
            {
                left = (left << 1) - counts[len];
                if (left < 0) throw new InvalidDataException("MSZIP Huffman table is over-subscribed.");
                code = (code + counts[len - 1]) << 1;
                next[len] = code;
            }

            var entries = new int[1 << maxLen];
            for (int sym = 0; sym < count; sym++)
            {
                int len = lengths[sym];
                if (len == 0) continue;
                int c = next[len]++;
                int reversed = 0;
                for (int b = 0; b < len; b++) reversed |= ((c >> b) & 1) << (len - 1 - b);
                int value = sym | (len << 16);
                for (int idx = reversed; idx < entries.Length; idx += 1 << len) entries[idx] = value;
            }

            return new HuffmanTable(maxLen, entries);
        }
    }
}

// Managed LZX decoder for Microsoft Cabinet folders of compression type 3. Implements the LZX
// bitstream of [MS-PATCH] "LZX DELTA Compression and Decompression" (section 2: verbatim,
// aligned-offset and uncompressed blocks, the pretree-coded delta path lengths, the R0/R1/R2
// repeated offsets and the E8 call-instruction translation) with the cabinet framing
// conventions documented by libmspack's lzxd and [MS-CAB]: one CFDATA block is one frame of at
// most 32 KB of output, the bitstream restarts word-aligned at each CFDATA block, and the window,
// trees and repeated offsets persist across the CFDATA blocks of one folder.

namespace XRay.Content.Internal.Cab;

/// <summary>
/// Decodes one cabinet folder's LZX stream into a caller-supplied buffer holding the whole
/// folder. The buffer doubles as the sliding window, so the E8 translation — which must not
/// disturb the history later matches read — is applied to every frame by <see cref="Finish"/>.
/// </summary>
internal sealed class LzxDecoder
{
    /// <summary>The smallest window a cabinet may declare (2^15 bytes).</summary>
    public const int MinWindowBits = 15;

    /// <summary>The largest window a cabinet may declare (2^21 bytes).</summary>
    public const int MaxWindowBits = 21;

    private const int NumChars = 256;
    private const int MinMatch = 2;
    private const int NumPrimaryLengths = 7;
    private const int NumSecondaryLengths = 249;
    private const int PretreeSize = 20;
    private const int AlignedSize = 8;
    private const int MaxCodeBits = 16;
    private const int FrameSize = 32768;
    private const long E8TranslationLimit = 0x40000000;

    private const int BlockVerbatim = 1;
    private const int BlockAligned = 2;
    private const int BlockUncompressed = 3;

    private static readonly byte[] ExtraBits = BuildExtraBits();
    private static readonly int[] PositionBase = BuildPositionBase();

    private readonly byte[] _output;
    private readonly int _windowSize;
    private readonly int _mainSize;
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[NumSecondaryLengths];
    private readonly byte[] _alignedLengths = new byte[AlignedSize];
    private readonly List<(int Start, int Length)> _frames = new();

    private HuffmanTable? _mainTable;
    private HuffmanTable? _lengthTable;
    private HuffmanTable? _alignedTable;

    private int _outPos;
    private int _frameEnd;
    private bool _headerRead;
    private int _e8TranslationSize;

    private uint _r0 = 1;
    private uint _r1 = 1;
    private uint _r2 = 1;

    private int _blockType;
    private int _blockSize;
    private int _blockRemaining;
    private bool _padPending;

    private ulong _bitBuf;
    private int _bitCount;
    private int _inPos;
    private int _inEnd;
    private bool _rawMode;

    /// <summary>Create a decoder for a window of 2^<paramref name="windowBits"/> bytes writing into <paramref name="output"/>.</summary>
    public LzxDecoder(int windowBits, byte[] output)
    {
        if (windowBits < MinWindowBits || windowBits > MaxWindowBits)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBits), "LZX window must be 2^15 to 2^21 bytes.");
        }

        _output = output;
        _windowSize = 1 << windowBits;
        int positionSlots = windowBits switch
        {
            20 => 42,
            21 => 50,
            _ => windowBits * 2,
        };
        _mainSize = NumChars + positionSlots * 8;
        _mainLengths = new byte[_mainSize];
    }

    /// <summary>Number of bytes decoded so far.</summary>
    public int Position => _outPos;

    /// <summary>Decode one CFDATA block's payload into the next <paramref name="uncompressedSize"/> bytes of the frame sequence.</summary>
    public void DecodeBlock(ReadOnlySpan<byte> block, int uncompressedSize)
    {
        if (uncompressedSize <= 0 || uncompressedSize > FrameSize)
        {
            throw new InvalidDataException($"LZX frame of {uncompressedSize} bytes is out of range.");
        }

        int frameStart = _frameEnd;
        _frameEnd += uncompressedSize;
        if (_frameEnd > _output.Length) throw new InvalidDataException("LZX data expands past its folder's size.");
        _frames.Add((frameStart, uncompressedSize));

        _inPos = 0;
        _inEnd = block.Length;
        _bitBuf = 0;
        _bitCount = 0;
        _rawMode = _blockType == BlockUncompressed && (_blockRemaining > 0 || _padPending);

        if (!_headerRead)
        {
            _headerRead = true;
            if (ReadBits(block, 1) == 1)
            {
                uint high = ReadBits(block, 16);
                uint low = ReadBits(block, 16);
                _e8TranslationSize = (int)((high << 16) | low);
            }
        }

        while (_outPos < _frameEnd)
        {
            if (_blockRemaining == 0) ReadBlockHeader(block);

            if (_blockType == BlockUncompressed)
            {
                int n = Math.Min(_blockRemaining, Math.Min(_frameEnd - _outPos, _inEnd - _inPos));
                if (n <= 0) throw new InvalidDataException("LZX uncompressed block is truncated.");
                block.Slice(_inPos, n).CopyTo(_output.AsSpan(_outPos));
                _inPos += n;
                _outPos += n;
                _blockRemaining -= n;
                if (_blockRemaining == 0 && (_blockSize & 1) != 0) _padPending = true;
                continue;
            }

            int produced = DecodeElement(block);
            if (produced > _blockRemaining) throw new InvalidDataException("LZX match runs past the end of its block.");
            _blockRemaining -= produced;
        }

        if (!_rawMode && (long)_inPos * 8 - _bitCount > (long)_inEnd * 8)
        {
            throw new InvalidDataException("LZX data block is truncated.");
        }
    }

    /// <summary>Apply the E8 call translation to every decoded frame. Call once, after the last block.</summary>
    public void Finish()
    {
        if (_e8TranslationSize == 0) return;

        foreach (var (start, length) in _frames)
        {
            if (start >= E8TranslationLimit) break;
            if (length <= 10) continue;
            TranslateE8(_output.AsSpan(start, length), start, _e8TranslationSize);
        }
    }

    internal static void TranslateE8(Span<byte> frame, int frameOffset, int translationSize)
    {
        int limit = frame.Length - 10;
        int i = 0;
        while (i < limit)
        {
            if (frame[i] != 0xE8)
            {
                i++;
                continue;
            }

            int current = frameOffset + i;
            int absolute = frame[i + 1] | (frame[i + 2] << 8) | (frame[i + 3] << 16) | (frame[i + 4] << 24);
            if (absolute >= -current && absolute < translationSize)
            {
                int relative = absolute >= 0 ? absolute - current : absolute + translationSize;
                frame[i + 1] = (byte)relative;
                frame[i + 2] = (byte)(relative >> 8);
                frame[i + 3] = (byte)(relative >> 16);
                frame[i + 4] = (byte)(relative >> 24);
            }

            i += 5;
        }
    }

    private void ReadBlockHeader(ReadOnlySpan<byte> block)
    {
        if (_rawMode)
        {
            if (_padPending) _inPos++;
            _padPending = false;
            _rawMode = false;
            _bitBuf = 0;
            _bitCount = 0;
        }

        _blockType = (int)ReadBits(block, 3);
        _blockSize = (int)ReadBits(block, 24);
        _blockRemaining = _blockSize;
        if (_blockSize == 0) throw new InvalidDataException("LZX block has zero length.");

        switch (_blockType)
        {
            case BlockAligned:
                for (int i = 0; i < AlignedSize; i++) _alignedLengths[i] = (byte)ReadBits(block, 3);
                _alignedTable = HuffmanTable.Build(_alignedLengths);
                ReadMainAndLengthTrees(block);
                break;
            case BlockVerbatim:
                ReadMainAndLengthTrees(block);
                break;
            case BlockUncompressed:
                EnterRawMode();
                if (_inPos + 12 > _inEnd) throw new InvalidDataException("LZX uncompressed block header is truncated.");
                _r0 = ReadU32(block, _inPos);
                _r1 = ReadU32(block, _inPos + 4);
                _r2 = ReadU32(block, _inPos + 8);
                _inPos += 12;
                break;
            default:
                throw new InvalidDataException($"LZX block type {_blockType} is invalid.");
        }
    }

    private void ReadMainAndLengthTrees(ReadOnlySpan<byte> block)
    {
        ReadLengths(block, _mainLengths, 0, NumChars);
        ReadLengths(block, _mainLengths, NumChars, _mainSize);
        _mainTable = HuffmanTable.Build(_mainLengths);
        if (_mainTable.Bits == 0) throw new InvalidDataException("LZX main tree is empty.");
        ReadLengths(block, _lengthLengths, 0, NumSecondaryLengths);
        _lengthTable = HuffmanTable.Build(_lengthLengths);
    }

    private void ReadLengths(ReadOnlySpan<byte> block, byte[] lengths, int first, int last)
    {
        Span<byte> preLengths = stackalloc byte[PretreeSize];
        for (int i = 0; i < PretreeSize; i++) preLengths[i] = (byte)ReadBits(block, 4);
        var pretree = HuffmanTable.Build(preLengths);

        int x = first;
        while (x < last)
        {
            int code = Decode(block, pretree);
            switch (code)
            {
                case 17:
                {
                    int run = 4 + (int)ReadBits(block, 4);
                    for (; run > 0 && x < last; run--) lengths[x++] = 0;
                    break;
                }
                case 18:
                {
                    int run = 20 + (int)ReadBits(block, 5);
                    for (; run > 0 && x < last; run--) lengths[x++] = 0;
                    break;
                }
                case 19:
                {
                    int run = 4 + (int)ReadBits(block, 1);
                    int delta = Decode(block, pretree);
                    if (delta > 16) throw new InvalidDataException("LZX pretree run carries an invalid length.");
                    byte value = (byte)((lengths[x] - delta + 17) % 17);
                    for (; run > 0 && x < last; run--) lengths[x++] = value;
                    break;
                }
                default:
                    lengths[x] = (byte)((lengths[x] - code + 17) % 17);
                    x++;
                    break;
            }
        }
    }

    private int DecodeElement(ReadOnlySpan<byte> block)
    {
        int sym = Decode(block, _mainTable!);
        if (sym < NumChars)
        {
            if (_outPos >= _output.Length) throw new InvalidDataException("LZX data expands past its folder's size.");
            _output[_outPos++] = (byte)sym;
            return 1;
        }

        sym -= NumChars;
        int length = sym & NumPrimaryLengths;
        if (length == NumPrimaryLengths)
        {
            if (_lengthTable is null || _lengthTable.Bits == 0) throw new InvalidDataException("LZX match needs an empty length tree.");
            length += Decode(block, _lengthTable);
        }

        length += MinMatch;

        int slot = sym >> 3;
        uint offset;
        switch (slot)
        {
            case 0:
                offset = _r0;
                break;
            case 1:
                offset = _r1;
                _r1 = _r0;
                _r0 = offset;
                break;
            case 2:
                offset = _r2;
                _r2 = _r0;
                _r0 = offset;
                break;
            default:
            {
                int extra = ExtraBits[slot];
                offset = (uint)(PositionBase[slot] - 2);
                if (_blockType == BlockAligned && extra >= 3)
                {
                    offset += ReadBits(block, extra - 3) << 3;
                    offset += (uint)Decode(block, _alignedTable!);
                }
                else if (extra > 0)
                {
                    offset += ReadBits(block, extra);
                }

                _r2 = _r1;
                _r1 = _r0;
                _r0 = offset;
                break;
            }
        }

        if (offset == 0 || offset > (uint)_outPos || offset > (uint)_windowSize)
        {
            throw new InvalidDataException("LZX match refers outside the window.");
        }

        if (_outPos + length > _output.Length) throw new InvalidDataException("LZX data expands past its folder's size.");
        MsZipDecoder.CopyMatch(_output, _outPos, (int)offset, length);
        _outPos += length;
        return length;
    }

    private void EnterRawMode()
    {
        int unconsumedWords = _bitCount / 16;
        int pos = _inPos - unconsumedWords * 2;
        if (_bitCount % 16 == 0) pos += 2;
        _inPos = pos;
        _bitBuf = 0;
        _bitCount = 0;
        _rawMode = true;
        if (_inPos > _inEnd) throw new InvalidDataException("LZX uncompressed block header is truncated.");
    }

    private int Decode(ReadOnlySpan<byte> block, HuffmanTable table)
    {
        if (table.Bits == 0) throw new InvalidDataException("LZX stream uses an empty Huffman tree.");
        Fill(block, table.Bits);
        int entry = table.Entries[(int)(_bitBuf >> (64 - table.Bits))];
        if (entry == 0) throw new InvalidDataException("LZX stream has an invalid Huffman code.");
        int len = entry >> 16;
        _bitBuf <<= len;
        _bitCount -= len;
        return entry & 0xFFFF;
    }

    private uint ReadBits(ReadOnlySpan<byte> block, int count)
    {
        if (count == 0) return 0;
        Fill(block, count);
        uint value = (uint)(_bitBuf >> (64 - count));
        _bitBuf <<= count;
        _bitCount -= count;
        return value;
    }

    private void Fill(ReadOnlySpan<byte> block, int count)
    {
        while (_bitCount < count)
        {
            int lo = _inPos < _inEnd ? block[_inPos] : 0;
            int hi = _inPos + 1 < _inEnd ? block[_inPos + 1] : 0;
            _inPos += 2;
            _bitBuf |= (ulong)(uint)(lo | (hi << 8)) << (48 - _bitCount);
            _bitCount += 16;
        }

        if (_inPos > _inEnd + 8)
        {
            throw new InvalidDataException("LZX data block is truncated.");
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> block, int pos) =>
        (uint)(block[pos] | (block[pos + 1] << 8) | (block[pos + 2] << 16) | (block[pos + 3] << 24));

    private static byte[] BuildExtraBits()
    {
        var bits = new byte[51];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)(i < 4 ? 0 : Math.Min((i - 2) >> 1, 17));
        return bits;
    }

    private static int[] BuildPositionBase()
    {
        var bases = new int[51];
        for (int i = 1; i < bases.Length; i++) bases[i] = bases[i - 1] + (1 << ExtraBits[i - 1]);
        return bases;
    }

    /// <summary>A canonical Huffman decoding table indexed by the next <see cref="Bits"/> bits, MSB first.</summary>
    private sealed class HuffmanTable
    {
        private HuffmanTable(int bits, int[] entries)
        {
            Bits = bits;
            Entries = entries;
        }

        public int Bits { get; }

        public int[] Entries { get; }

        public static HuffmanTable Build(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[MaxCodeBits + 1];
            int maxLen = 0;
            foreach (byte len in lengths)
            {
                if (len > MaxCodeBits) throw new InvalidDataException("LZX Huffman code is longer than 16 bits.");
                counts[len]++;
                if (len > maxLen) maxLen = len;
            }

            if (maxLen == 0) return new HuffmanTable(0, Array.Empty<int>());

            counts[0] = 0;
            Span<int> next = stackalloc int[MaxCodeBits + 1];
            int left = 1;
            int code = 0;
            for (int len = 1; len <= MaxCodeBits; len++)
            {
                left = (left << 1) - counts[len];
                if (left < 0) throw new InvalidDataException("LZX Huffman tree is over-subscribed.");
                code = (code + counts[len - 1]) << 1;
                next[len] = code;
            }

            var entries = new int[1 << maxLen];
            for (int sym = 0; sym < lengths.Length; sym++)
            {
                int len = lengths[sym];
                if (len == 0) continue;
                int start = next[len]++ << (maxLen - len);
                int span = 1 << (maxLen - len);
                entries.AsSpan(start, span).Fill(sym | (len << 16));
            }

            return new HuffmanTable(maxLen, entries);
        }
    }
}

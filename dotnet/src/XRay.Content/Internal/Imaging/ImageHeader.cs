using XRay.Content.Internal.Exif;

namespace XRay.Content.Internal.Imaging;

/// <summary>What an image file's headers say, read without decoding a single pixel.</summary>
/// <param name="Format">Upper-case format name as upstream spells it: PNG, JPEG, GIF, BMP, WEBP, TIFF.</param>
/// <param name="FrameCount">Frames a TIFF holds (its IFD chain); 1 for every other format.</param>
/// <param name="Exif">The TIFF-structured EXIF block, empty when the file carries none (a TIFF's own tags are not reported).</param>
internal readonly record struct ImageHeader(string Format, int Width, int Height, int FrameCount, ReadOnlyMemory<byte> Exif)
{
    /// <summary>Read the header of a PNG, JPEG, GIF, BMP, WebP or TIFF.</summary>
    /// <exception cref="InvalidDataException">The bytes are not one of those, or are cut off before the dimensions.</exception>
    public static ImageHeader Read(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return Png(b);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8) return Jpeg(b);
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return Gif(b);
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M') return Bmp(b);
        if (b.Length >= 16 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return WebP(b);
        if (TiffExif.HasHeader(b)) return Tiff(b);
        throw new InvalidDataException("Unrecognised image format");
    }

    private static InvalidDataException Truncated(string format) => new($"Truncated {format} image header");

    private static uint Be32(ReadOnlySpan<byte> b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
    private static int Be16(ReadOnlySpan<byte> b, int at) => b[at] << 8 | b[at + 1];
    private static int Le16(ReadOnlySpan<byte> b, int at) => b[at] | b[at + 1] << 8;
    private static int Le32(ReadOnlySpan<byte> b, int at) => b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24;

    private static ImageHeader Png(ReadOnlySpan<byte> b)
    {
        // IHDR is the first chunk: length, "IHDR", width, height.
        if (b.Length < 24 || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) throw Truncated("PNG");
        int width = (int)Math.Min(Be32(b, 16), int.MaxValue), height = (int)Math.Min(Be32(b, 20), int.MaxValue);

        byte[]? exif = null;
        int at = 8;
        while (at + 12 <= b.Length)
        {
            uint length = Be32(b, at);
            var type = b.Slice(at + 4, 4);
            if (length > (uint)(b.Length - at - 12)) break;
            if (type.SequenceEqual("eXIf"u8)) { exif = b.Slice(at + 8, (int)length).ToArray(); break; }
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) break;
            at += 12 + (int)length;
        }
        return new ImageHeader("PNG", width, height, 1, exif);
    }

    private static ImageHeader Jpeg(ReadOnlySpan<byte> b)
    {
        byte[]? exif = null;
        int at = 2;
        while (at + 4 <= b.Length)
        {
            if (b[at] != 0xFF) { at++; continue; }
            byte marker = b[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8)) { at += 2; continue; }
            if (marker is 0xD9 or 0xDA) break;

            int length = Be16(b, at + 2);
            if (length < 2 || at + 2 + length > b.Length) break;
            var payload = b.Slice(at + 4, length - 2);

            if (marker == 0xE1 && exif is null && payload.Length > 6 && payload.StartsWith("Exif\0\0"u8))
                exif = payload[6..].ToArray();

            bool startOfFrame = marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (startOfFrame)
            {
                if (payload.Length < 5) throw Truncated("JPEG");
                return new ImageHeader("JPEG", Be16(payload, 3), Be16(payload, 1), 1, exif);
            }
            at += 2 + length;
        }
        throw Truncated("JPEG");
    }

    private static ImageHeader Gif(ReadOnlySpan<byte> b) =>
        new("GIF", Le16(b, 6), Le16(b, 8), 1, default);

    private static ImageHeader Bmp(ReadOnlySpan<byte> b)
    {
        int headerSize = Le32(b, 14);
        if (headerSize == 12) return new ImageHeader("BMP", Le16(b, 18), Le16(b, 20), 1, default);
        if (headerSize < 40) throw Truncated("BMP");
        int height = Le32(b, 22);
        return new ImageHeader("BMP", Le32(b, 18), height == int.MinValue ? int.MaxValue : Math.Abs(height), 1, default);
    }

    private static ImageHeader WebP(ReadOnlySpan<byte> b)
    {
        int width = 0, height = 0;
        byte[]? exif = null;
        int at = 12;
        while (at + 8 <= b.Length)
        {
            var fourCc = b.Slice(at, 4);
            long size = (uint)Le32(b, at + 4);
            int data = at + 8;
            int available = (int)Math.Min(size, b.Length - data);

            if (fourCc.SequenceEqual("VP8X"u8) && available >= 10)
            {
                width = 1 + (b[data + 4] | b[data + 5] << 8 | b[data + 6] << 16);
                height = 1 + (b[data + 7] | b[data + 8] << 8 | b[data + 9] << 16);
            }
            else if (fourCc.SequenceEqual("VP8 "u8) && available >= 10 && width == 0)
            {
                width = Le16(b, data + 6) & 0x3FFF;
                height = Le16(b, data + 8) & 0x3FFF;
            }
            else if (fourCc.SequenceEqual("VP8L"u8) && available >= 5 && width == 0)
            {
                uint bits = (uint)Le32(b, data + 1);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (fourCc.SequenceEqual("EXIF"u8) && exif is null)
            {
                exif = TiffExif.Unwrap(b.Slice(data, available)).ToArray();
            }

            at = data + (int)Math.Min(size + (size & 1), b.Length - data);
        }
        if (width == 0 || height == 0) throw Truncated("WebP");
        return new ImageHeader("WEBP", width, height, 1, exif);
    }

    private static ImageHeader Tiff(ReadOnlySpan<byte> b)
    {
        bool le = b[0] == 'I';
        var seen = new HashSet<uint>();
        uint ifd = TiffExif.U32(b, 4, le);
        int frames = 0, width = 0, height = 0;

        while (ifd >= 8 && ifd <= (uint)b.Length - 2 && seen.Add(ifd) && frames < 100_000)
        {
            frames++;
            int count = TiffExif.U16(b, (int)ifd, le);
            long entries = ifd + 2;
            if (frames == 1)
            {
                for (int i = 0; i < count && entries + (i + 1) * 12L <= b.Length; i++)
                {
                    int at = (int)(entries + i * 12L);
                    ushort tag = TiffExif.U16(b, at, le), type = TiffExif.U16(b, at + 2, le);
                    if (tag is not (0x0100 or 0x0101) || TiffExif.U32(b, at + 4, le) != 1) continue;
                    int value = type == 3 ? TiffExif.U16(b, at + 8, le) : (int)Math.Min(TiffExif.U32(b, at + 8, le), int.MaxValue);
                    if (tag == 0x0100) width = value; else height = value;
                }
            }
            long next = entries + count * 12L;
            if (next + 4 > b.Length) break;
            ifd = TiffExif.U32(b, (int)next, le);
        }

        if (frames == 0 || width == 0 || height == 0) throw Truncated("TIFF");
        return new ImageHeader("TIFF", width, height, frames, default);
    }
}

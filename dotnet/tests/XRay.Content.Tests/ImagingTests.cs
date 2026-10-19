using SkiaSharp;
using XRay.Content.Internal.Exif;
using XRay.Content.Internal.Imaging;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>The header reader, the EXIF reader and the pixel paths that replaced the imaging library.</summary>
public class ImagingTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "PNG")]
    [InlineData(SKEncodedImageFormat.Jpeg, "JPEG")]
    [InlineData(SKEncodedImageFormat.Webp, "WEBP")]
    public void HeaderReportsSizeAndFormatOfEncodedImages(SKEncodedImageFormat encoded, string name)
    {
        var header = ImageHeader.Read(TestImages.Encode(37, 21, encoded, 90));

        Assert.Equal(name, header.Format);
        Assert.Equal(37, header.Width);
        Assert.Equal(21, header.Height);
        Assert.Equal(1, header.FrameCount);
    }

    [Fact]
    public void HeaderReadsGifAndBmpByHand()
    {
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 5, 0, 9, 0, 0, 0 };
        var gifHeader = ImageHeader.Read(gif);
        Assert.Equal(("GIF", 5, 9), (gifHeader.Format, gifHeader.Width, gifHeader.Height));

        var bmp = new byte[54];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        bmp[14] = 40;
        bmp[18] = 11;
        BitConverter.GetBytes(-7).CopyTo(bmp, 22); // a top-down bitmap stores a negative height
        var bmpHeader = ImageHeader.Read(bmp);
        Assert.Equal(("BMP", 11, 7), (bmpHeader.Format, bmpHeader.Width, bmpHeader.Height));
    }

    [Fact]
    public void HeaderCountsTiffFrames()
    {
        var tiff = Tiff(littleEndian: true, frames: 3, width: 40, height: 30);
        var header = ImageHeader.Read(tiff);

        Assert.Equal(("TIFF", 40, 30, 3), (header.Format, header.Width, header.Height, header.FrameCount));
    }

    [Fact]
    public void HeaderRejectsGarbageAndTruncation()
    {
        Assert.Throws<InvalidDataException>(() => ImageHeader.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }));
        Assert.Throws<InvalidDataException>(() => ImageHeader.Read(Array.Empty<byte>()));

        var png = TestImages.Png(8, 8);
        for (int cut = 0; cut < 24; cut++)
            Assert.Throws<InvalidDataException>(() => ImageHeader.Read(png[..cut]));
    }

    [Fact]
    public void HeaderSurvivesEveryTruncationOfAJpegAndAWebP()
    {
        foreach (var bytes in new[] { TestImages.Jpeg(16, 16), TestImages.Encode(16, 16, SKEncodedImageFormat.Webp, 80) })
            for (int cut = 0; cut < bytes.Length; cut += 3)
            {
                try { ImageHeader.Read(bytes[..cut]); }
                catch (InvalidDataException) { }
            }
    }

    [Fact]
    public void JpegCarriesItsExifBlock()
    {
        var exif = ExifBlock(littleEndian: true);
        var jpeg = WithApp1(TestImages.Jpeg(16, 8), "Exif\0\0"u8.ToArray().Concat(exif).ToArray());

        var header = ImageHeader.Read(jpeg);
        var map = ExifReader.Extract(header.Exif.Span);

        Assert.Equal((16, 8), (header.Width, header.Height));
        Assert.Equal("Canon", map["Make"]);
        Assert.Equal("1/500", map["ExposureTime"]);
        Assert.Equal("3", map["Orientation"]);
        Assert.Equal("52 1/2 30", map["GPSLatitude"]);
        Assert.Equal("N", map["GPSLatitudeRef"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExifReadsBothByteOrders(bool littleEndian)
    {
        var map = ExifReader.Extract(ExifBlock(littleEndian));

        Assert.Equal("Canon", map["Make"]);
        Assert.Equal("1/500", map["ExposureTime"]);
        Assert.Equal("100", map["XResolution"]);
        Assert.Equal("48 50 50 48", map["ExifVersion"]);
        Assert.Equal("3", map["Orientation"]);
    }

    [Fact]
    public void ExifSkipsEntriesPointingOutsideTheBlockAndNeverThrows()
    {
        var block = ExifBlock(littleEndian: true);
        for (int cut = 0; cut < block.Length; cut++)
            _ = ExifReader.Extract(block.AsSpan(0, cut));

        var corrupted = (byte[])block.Clone();
        for (int i = 8; i < corrupted.Length; i += 7) corrupted[i] ^= 0xFF;
        _ = ExifReader.Extract(corrupted);

        Assert.Empty(ExifReader.Extract(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ExifIfdLoopTerminates()
    {
        // IFD0 whose Exif-IFD pointer names IFD0 itself.
        var b = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0 };
        b.AddRange(Entry(0x8769, 4, 1, 8));
        b.AddRange(new byte[4]);

        Assert.Empty(ExifReader.Extract(b.ToArray()));
    }

    [Fact]
    public void ResizeKeepsAFlatImageFlatAndAveragesWhenShrinking()
    {
        var flat = new RgbImage(10, 10);
        Array.Fill(flat.Data, (byte)90);
        Assert.All(flat.ResizeTriangle(4, 7).Data, v => Assert.Equal(90, v));

        // Left half black, right half white, halved: the middle columns blend, the outer ones do not.
        var split = new RgbImage(4, 1);
        for (int x = 2; x < 4; x++) split.Data[x * 3] = split.Data[x * 3 + 1] = split.Data[x * 3 + 2] = 255;
        var halved = split.ResizeTriangle(2, 1);
        Assert.Equal(2, halved.Width);
        Assert.True(halved.Data[0] < 100);
        Assert.True(halved.Data[3] > 155);
    }

    [Fact]
    public void ResizeHonoursTheRequestedSizeAndIdentityCopies()
    {
        var image = new RgbImage(5, 3);
        image.Data[0] = 7;

        var same = image.ResizeTriangle(5, 3);
        Assert.NotSame(image.Data, same.Data);
        Assert.Equal(image.Data, same.Data);

        var larger = image.ResizeTriangle(11, 9);
        Assert.Equal((11, 9, 11 * 9 * 3), (larger.Width, larger.Height, larger.Data.Length));
    }

    [Fact]
    public void SkiaDecodesToRgbAndGrey()
    {
        var png = TestImages.Png(12, 5); // every pixel is (200, 30, 60)

        var rgb = SkiaImages.DecodeRgb(png);
        Assert.Equal((12, 5), (rgb.Width, rgb.Height));
        Assert.Equal(new byte[] { 200, 30, 60 }, rgb.Data[..3]);

        var (width, height, grey) = SkiaImages.DecodeGrey(png);
        Assert.Equal((12, 5, 60), (width, height, grey.Length));
        Assert.InRange(grey[0], 67, 69); // 0.2126*200 + 0.7152*30 + 0.0722*60 = 68.3
    }

    [Fact]
    public void SkiaRejectsGarbage() =>
        Assert.Throws<InvalidDataException>(() => SkiaImages.DecodeRgb(new byte[] { 1, 2, 3, 4 }));

    // ── builders ─────────────────────────────────────────────────────────────

    private static byte[] Entry(ushort tag, ushort type, uint count, uint value) =>
        [.. BitConverter.GetBytes(tag), .. BitConverter.GetBytes(type), .. BitConverter.GetBytes(count), .. BitConverter.GetBytes(value)];

    /// <summary>A TIFF with the given number of one-entry-per-dimension IFDs.</summary>
    private static byte[] Tiff(bool littleEndian, int frames, int width, int height)
    {
        Assert.True(littleEndian);
        var b = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
        for (int f = 0; f < frames; f++)
        {
            b.AddRange(BitConverter.GetBytes((ushort)2));
            b.AddRange(Entry(0x0100, 3, 1, (uint)width));
            b.AddRange(Entry(0x0101, 3, 1, (uint)height));
            b.AddRange(BitConverter.GetBytes(f + 1 < frames ? (uint)(b.Count + 4) : 0u));
        }
        return b.ToArray();
    }

    /// <summary>
    /// IFD0 (Make, Orientation, Exif pointer, GPS pointer), an Exif IFD (ExposureTime, ExifVersion,
    /// XResolution lives in IFD0), and a GPS IFD (latitude ref and a three-rational latitude).
    /// </summary>
    private static byte[] ExifBlock(bool littleEndian)
    {
        byte[] U16(int v) => littleEndian ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v];
        byte[] U32(uint v) => littleEndian
            ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]
            : [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        byte[] E(int tag, int type, uint count, byte[] inlineValue) =>
            [.. U16(tag), .. U16(type), .. U32(count), .. inlineValue, .. new byte[4 - inlineValue.Length]];

        // Layout: header(8) | IFD0 | Exif IFD | GPS IFD | data
        const int ifd0 = 8;
        int ifd0Size = 2 + 5 * 12 + 4;
        int exifIfd = ifd0 + ifd0Size;
        int exifSize = 2 + 2 * 12 + 4;
        int gpsIfd = exifIfd + exifSize;
        int gpsSize = 2 + 2 * 12 + 4;
        int dataAt = gpsIfd + gpsSize;

        var tail = new List<byte>();
        uint At() => (uint)(dataAt + tail.Count);
        uint Put(byte[] bytes) { uint at = At(); tail.AddRange(bytes); return at; }

        uint make = Put("Canon\0"u8.ToArray());
        uint xres = Put([.. U32(100), .. U32(1)]);
        uint exposure = Put([.. U32(1), .. U32(500)]);
        uint lat = Put([.. U32(52), .. U32(1), .. U32(1), .. U32(2), .. U32(30), .. U32(1)]);

        var block = new List<byte>();
        block.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        block.AddRange(U16(42));
        block.AddRange(U32(ifd0));

        block.AddRange(U16(5));
        block.AddRange(E(0x010F, 2, 6, U32(make)));
        block.AddRange(E(0x0112, 3, 1, U16(3)));
        block.AddRange(E(0x011A, 5, 1, U32(xres)));
        block.AddRange(E(0x8769, 4, 1, U32((uint)exifIfd)));
        block.AddRange(E(0x8825, 4, 1, U32((uint)gpsIfd)));
        block.AddRange(U32(0));

        block.AddRange(U16(2));
        block.AddRange(E(0x829A, 5, 1, U32(exposure)));
        block.AddRange(E(0x9000, 7, 4, "0220"u8.ToArray()));
        block.AddRange(U32(0));

        block.AddRange(U16(2));
        block.AddRange(E(0x0001, 2, 2, "N\0"u8.ToArray()));
        block.AddRange(E(0x0002, 5, 3, U32(lat)));
        block.AddRange(U32(0));

        block.AddRange(tail);
        return block.ToArray();
    }

    /// <summary>Insert an APP1 segment straight after the SOI.</summary>
    private static byte[] WithApp1(byte[] jpeg, byte[] payload)
    {
        int length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }
}

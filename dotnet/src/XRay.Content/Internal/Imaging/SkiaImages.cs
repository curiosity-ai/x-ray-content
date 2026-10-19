using SkiaSharp;

namespace XRay.Content.Internal.Imaging;

/// <summary>
/// Decodes pixels through SkiaSharp. This is the only place the package reads them: dimensions,
/// format and EXIF come from <see cref="ImageHeader"/> without loading Skia at all, so only a QR
/// scan (opt-in) and a caller handing the layout models a page ever reach the native library.
/// </summary>
internal static class SkiaImages
{
    /// <summary>Decode an image to RGB, dropping alpha without compositing (transparent pixels keep their colour).</summary>
    /// <exception cref="InvalidDataException">Skia cannot decode the bytes.</exception>
    public static RgbImage DecodeRgb(ReadOnlySpan<byte> bytes)
    {
        var (width, height, rgba) = DecodeRgba(bytes);
        var rgb = new byte[checked(width * height * 3)];
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        {
            rgb[i] = rgba[o];
            rgb[i + 1] = rgba[o + 1];
            rgb[i + 2] = rgba[o + 2];
        }
        return new RgbImage(width, height, rgb);
    }

    /// <summary>Decode an image to 8-bit greyscale using the BT.709 luma weights.</summary>
    /// <exception cref="InvalidDataException">Skia cannot decode the bytes.</exception>
    public static (int Width, int Height, byte[] Grey) DecodeGrey(ReadOnlySpan<byte> bytes)
    {
        var (width, height, rgba) = DecodeRgba(bytes);
        var grey = new byte[checked(width * height)];
        for (int i = 0, o = 0; i < grey.Length; i++, o += 4)
            grey[i] = (byte)((2126 * rgba[o] + 7152 * rgba[o + 1] + 722 * rgba[o + 2] + 5000) / 10000);
        return (width, height, grey);
    }

    private static (int Width, int Height, byte[] Rgba) DecodeRgba(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Image could not be decoded");

        // Unpremultiplied, so a transparent pixel keeps its colour channels rather than going black.
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Image could not be decoded ({result})");

        return (info.Width, info.Height, bitmap.GetPixelSpan().ToArray());
    }
}

using SkiaSharp;

namespace XRay.Content.Tests;

/// <summary>Tiny encoded images for tests, made with SkiaSharp (the package's own decoder).</summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png, 100);

    public static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg, 90);

    public static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(new SKColor(200, 30, 60));
        return Encode(bitmap, format, quality);
    }

    /// <summary>An 8-bit greyscale PNG from tightly packed luma bytes.</summary>
    public static byte[] GreyPng(byte[] grey, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (int i = 0, o = 0; i < grey.Length; i++, o += 4)
        {
            pixels[o] = pixels[o + 1] = pixels[o + 2] = grey[i];
            pixels[o + 3] = 255;
        }
        return Encode(bitmap, SKEncodedImageFormat.Png, 100);
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException("Skia could not encode the image");
        return data.ToArray();
    }
}

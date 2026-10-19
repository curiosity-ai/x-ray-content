using System.Runtime.InteropServices;

namespace XRay.Content.Internal.Imaging;

/// <summary>One 8-bit RGB pixel.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct Rgb24(byte r, byte g, byte b)
{
    public readonly byte R = r;
    public readonly byte G = g;
    public readonly byte B = b;
}

/// <summary>
/// A decoded page or table crop as the layout models read it: width, height and tightly packed
/// 8-bit RGB. Managed and dependency-free, so the models stay pure managed; decoding a file into
/// one is <see cref="SkiaImages"/>'s job.
/// </summary>
internal sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public RgbImage(int width, int height) : this(width, height, new byte[checked(width * height * 3)]) { }

    public RgbImage(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "An image has at least one pixel.");
        if (data.Length != checked(width * height * 3)) throw new ArgumentException("Pixel data does not match the dimensions.", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public Span<Rgb24> GetRowSpan(int y) =>
        MemoryMarshal.Cast<byte, Rgb24>(Data.AsSpan(y * Width * 3, Width * 3));

    /// <summary>The rows, for the preprocessing loops that walk the image top to bottom.</summary>
    public void ProcessPixelRows(Action<PixelRows> action) => action(new PixelRows(this));

    public readonly struct PixelRows(RgbImage image)
    {
        public int Height => image.Height;
        public Span<Rgb24> GetRowSpan(int y) => image.GetRowSpan(y);
    }

    /// <summary>
    /// Bilinear (triangle filter) resize to the given size, ported from <c>image::imageops::resize</c>
    /// with <c>FilterType::Triangle</c>: the vertical pass first, into floating point, then the
    /// horizontal pass, rounding once at the end. When shrinking, the filter widens by the scale
    /// ratio so every source pixel contributes (area averaging), as it does there.
    /// </summary>
    public RgbImage ResizeTriangle(int newWidth, int newHeight)
    {
        newWidth = Math.Max(newWidth, 1);
        newHeight = Math.Max(newHeight, 1);
        if (newWidth == Width && newHeight == Height) return new RgbImage(Width, Height, (byte[])Data.Clone());

        var vertical = new float[Width * newHeight * 3];
        VerticalPass(vertical, newHeight);

        var result = new byte[newWidth * newHeight * 3];
        HorizontalPass(vertical, result, newWidth, newHeight);
        return new RgbImage(newWidth, newHeight, result);
    }

    private static float Triangle(float x)
    {
        x = MathF.Abs(x);
        return x < 1f ? 1f - x : 0f;
    }

    private void VerticalPass(float[] destination, int newHeight)
    {
        float ratio = Height / (float)newHeight;
        float sratio = ratio < 1f ? 1f : ratio;
        float support = sratio; // the triangle's support is 1.0
        var weights = new float[(int)MathF.Ceiling(support * 2f) + 2];

        for (int outY = 0; outY < newHeight; outY++)
        {
            float inputY = (outY + 0.5f) * ratio;
            int left = (int)Math.Clamp(MathF.Floor(inputY - support), 0, Height - 1);
            int right = (int)Math.Clamp(MathF.Ceiling(inputY + support), left + 1, Height);

            float sum = 0f;
            for (int i = left; i < right; i++)
            {
                float w = Triangle((i - inputY + 0.5f) / sratio);
                weights[i - left] = w;
                sum += w;
            }
            if (sum <= 0f) { weights[0] = 1f; sum = 1f; right = left + 1; }

            for (int x = 0; x < Width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int i = left; i < right; i++)
                {
                    float w = weights[i - left];
                    int at = (i * Width + x) * 3;
                    r += Data[at] * w;
                    g += Data[at + 1] * w;
                    b += Data[at + 2] * w;
                }
                int to = (outY * Width + x) * 3;
                destination[to] = r / sum;
                destination[to + 1] = g / sum;
                destination[to + 2] = b / sum;
            }
        }
    }

    private void HorizontalPass(float[] source, byte[] destination, int newWidth, int rows)
    {
        float ratio = Width / (float)newWidth;
        float sratio = ratio < 1f ? 1f : ratio;
        float support = sratio;
        var weights = new float[(int)MathF.Ceiling(support * 2f) + 2];

        for (int outX = 0; outX < newWidth; outX++)
        {
            float inputX = (outX + 0.5f) * ratio;
            int left = (int)Math.Clamp(MathF.Floor(inputX - support), 0, Width - 1);
            int right = (int)Math.Clamp(MathF.Ceiling(inputX + support), left + 1, Width);

            float sum = 0f;
            for (int i = left; i < right; i++)
            {
                float w = Triangle((i - inputX + 0.5f) / sratio);
                weights[i - left] = w;
                sum += w;
            }
            if (sum <= 0f) { weights[0] = 1f; sum = 1f; right = left + 1; }

            for (int y = 0; y < rows; y++)
            {
                float r = 0, g = 0, b = 0;
                for (int i = left; i < right; i++)
                {
                    float w = weights[i - left];
                    int at = (y * Width + i) * 3;
                    r += source[at] * w;
                    g += source[at + 1] * w;
                    b += source[at + 2] * w;
                }
                int to = (y * newWidth + outX) * 3;
                destination[to] = ToByte(r / sum);
                destination[to + 1] = ToByte(g / sum);
                destination[to + 2] = ToByte(b / sum);
            }
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v), 0f, 255f);
}

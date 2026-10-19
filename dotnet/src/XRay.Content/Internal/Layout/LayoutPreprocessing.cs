using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>
/// Image preprocessing shared by the layout models, ported from Rust
/// <c>layout::preprocessing</c>.
/// </summary>
internal static class LayoutPreprocessing
{
    /// <summary>
    /// Rescale-only preprocessing: bilinear resize to a square, divide by 255, NCHW.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things here are easy to get wrong and both shift every predicted box. The resize does
    /// <em>not</em> preserve aspect ratio — the model is told the true page geometry separately
    /// and undoes the distortion itself — and there is no ImageNet mean/standard-deviation
    /// normalisation, unlike most detection exports.
    /// </para>
    /// <para>
    /// This is also the preprocessing contract of the original Docling Heron ONNX export.
    /// </para>
    /// </remarks>
    internal static void PreprocessRescale(
        RgbImage page, float[] destination, int targetSize, int offset = 0)
    {
        var resized = page.ResizeTriangle(targetSize, targetSize);

        int plane = targetSize * targetSize;
        const float scale = 1f / 255f;

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = offset + y * targetSize;
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    destination[rowOffset + x] = pixel.R * scale;
                    destination[plane + rowOffset + x] = pixel.G * scale;
                    destination[2 * plane + rowOffset + x] = pixel.B * scale;
                }
            }
        });
    }

    /// <summary>
    /// Letterbox preprocessing for YOLOX-style models.
    /// </summary>
    /// <remarks>
    /// Resizes to fit inside the target while preserving aspect ratio and pads the remainder with
    /// the raw value 114, which is what YOLOX was trained against. Values stay in 0-255: unlike
    /// most exports this one takes raw pixels, so dividing by 255 here would halve every
    /// activation. Returns the scale ratio, which the caller needs to map detections back.
    /// </remarks>
    internal static float PreprocessLetterbox(
        RgbImage page, float[] destination, int targetWidth, int targetHeight, int offset = 0)
    {
        float originalWidth = page.Width;
        float originalHeight = page.Height;
        float scale = MathF.Min(targetHeight / originalHeight, targetWidth / originalWidth);
        int newWidth = (int)(originalWidth * scale);
        int newHeight = (int)(originalHeight * scale);

        var resized = page.ResizeTriangle(Math.Max(newWidth, 1), Math.Max(newHeight, 1));

        int plane = targetHeight * targetWidth;
        // The pad value fills the whole canvas first, so the region the resized image does not
        // cover is padding rather than black.
        for (int c = 0; c < 3; c++)
            Array.Fill(destination, 114.0f, offset + c * plane, plane);

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = offset + y * targetWidth;
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    destination[rowOffset + x] = pixel.R;
                    destination[plane + rowOffset + x] = pixel.G;
                    destination[2 * plane + rowOffset + x] = pixel.B;
                }
            }
        });

        return scale;
    }
}

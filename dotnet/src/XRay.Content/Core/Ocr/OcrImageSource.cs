using System.IO.Compression;
using XRay.Content.Types;

namespace XRay.Content.Core.Ocr;

/// <summary>
/// Makes an extractor's images readable by the OCR pass.
/// </summary>
/// <remarks>
/// <para>
/// The pass recognises what is in <see cref="InternalDocument.Images"/>, and it needs bytes:
/// an entry that carries none is skipped. Most extractors record their images as references —
/// an index, an alt text, a part name — because that is all the renderers need, and reading
/// every embedded picture out of a container costs memory that an ordinary extraction has no
/// use for. So the bytes are attached here, and only when
/// <see cref="OcrMode.AllImages"/> asked for them.
/// </para>
/// <para>
/// That gate is also what keeps extraction output unchanged: with OCR off, or in
/// <see cref="OcrMode.ScanOnly"/>, nothing below runs and the documents are byte-identical to
/// what the golden fixtures pin.
/// </para>
/// </remarks>
internal static class OcrImageSource
{
    /// <summary>Whether the configured pass will read embedded images at all.</summary>
    public static bool WantsEmbeddedImages(ExtractionConfig config) =>
        config.Ocr is { Mode: OcrMode.AllImages };

    /// <summary>
    /// Fill in the bytes of the images the document already lists, reading each one's part from
    /// the container it came out of.
    /// </summary>
    /// <param name="doc">The document whose <see cref="ExtractedImage.Data"/> is filled in.</param>
    /// <param name="config">Extraction settings; nothing happens unless OCR asked for images.</param>
    /// <param name="read">
    /// Reads one part by the <see cref="ExtractedImage.SourcePath"/> recorded on the image, or
    /// returns null when there is no such part.
    /// </param>
    public static void AttachBytes(
        InternalDocument doc, ExtractionConfig config, Func<string, byte[]?> read)
    {
        if (!WantsEmbeddedImages(config)) return;

        foreach (var image in doc.Images)
        {
            if (image.Data.Length > 0) continue;
            if (image.SourcePath is not { Length: > 0 } path) continue;
            if (read(path) is { Length: > 0 } bytes) image.Data = bytes;
        }
    }

    /// <summary>
    /// Add every picture stored under one of <paramref name="folders"/> that the document does
    /// not already carry.
    /// </summary>
    /// <remarks>
    /// For the containers whose extractor records no images at all — a presentation, a workbook,
    /// an ODF document — this is the only way the pass can see them. They arrive unreferenced by
    /// any element, so their recognised text is appended rather than inlined; that is the
    /// documented behaviour for an image no element references, and it is what makes the mode
    /// work at all for these formats.
    /// </remarks>
    /// <param name="doc">The document the images are added to.</param>
    /// <param name="content">The container's bytes.</param>
    /// <param name="config">Extraction settings; nothing happens unless OCR asked for images.</param>
    /// <param name="folders">Part-name prefixes to look under, e.g. <c>ppt/media/</c>.</param>
    public static void AddPicturesFromZip(
        InternalDocument doc, ReadOnlySpan<byte> content, ExtractionConfig config, params string[] folders)
    {
        if (!WantsEmbeddedImages(config)) return;

        var already = new HashSet<string>(
            doc.Images.Select(i => i.SourcePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = new ZipArchive(new MemoryStream(content.ToArray()), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (already.Contains(entry.FullName)) continue;
                if (!folders.Any(f => entry.FullName.StartsWith(f, StringComparison.OrdinalIgnoreCase))) continue;
                if (!IsRecognisableImage(entry.FullName)) continue;

                byte[] bytes;
                using (var stream = entry.Open())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }
                if (bytes.Length == 0) continue;

                doc.Images.Add(new ExtractedImage
                {
                    ImageIndex = (uint)doc.Images.Count,
                    Data = bytes,
                    Format = Extension(entry.FullName),
                    SourcePath = entry.FullName,
                });
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            // The extractor already read this container successfully, so a failure here is about
            // one entry rather than the document. Missing OCR is not a reason to fail extraction.
        }
    }

    /// <summary>
    /// Formats the recognizer's decoder reads. A vector drawing or a metafile is skipped: it
    /// would have to be rasterised first, which the port does not do.
    /// </summary>
    private static bool IsRecognisableImage(string partName) => Extension(partName) switch
    {
        "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp" or "tif" or "tiff" => true,
        _ => false,
    };

    private static string Extension(string partName)
    {
        int dot = partName.LastIndexOf('.');
        return dot < 0 ? "" : partName[(dot + 1)..].ToLowerInvariant();
    }
}

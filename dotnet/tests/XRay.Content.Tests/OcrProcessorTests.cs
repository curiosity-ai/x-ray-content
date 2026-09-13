using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using XRay.Content.Core;
using XRay.Content.Core.Ocr;
using XRay.Content.Extractors;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// The optional OCR pass: which mode recognises what, where the text lands, and what happens
/// when the recognizer cannot run.
/// </summary>
/// <remarks>
/// Driven through a fake recognizer rather than PaddleOCR. The shipped one needs a multi-gigabyte
/// checkpoint, so every assertion here would otherwise be unrunnable — or, worse, silently
/// skipped, which is how a wrong expectation survived an entire sync in this suite once already.
/// What is under test is the flow: gating, placement, budgets and failure handling. That the
/// recognizer recognises is PaddleOCR's own business.
/// </remarks>
public sealed class OcrProcessorTests
{
    private const string PdfMime = "application/pdf";
    private const string PngMime = "image/png";

    /// <summary>A recognizer that returns a fixed answer and counts what it was asked.</summary>
    private sealed class FakeEngine : IOcrEngine
    {
        private readonly Func<int, OcrImageResult> _answer;
        public FakeEngine(Func<int, OcrImageResult>? answer = null) =>
            _answer = answer ?? (_ => new OcrImageResult("recognised text", 1));

        public List<int> Calls { get; } = new();
        public bool Disposed { get; private set; }

        public OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default)
        {
            Calls.Add(imageBytes.Length);
            return _answer(Calls.Count - 1);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A recognizer that cannot run at all, as a missing checkpoint would be.</summary>
    private sealed class UnavailableEngine : IOcrEngine
    {
        public int Calls { get; private set; }
        public OcrImageResult Recognize(ReadOnlySpan<byte> imageBytes, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new OcrUnavailableException("no checkpoint at '/nowhere'");
        }
        public void Dispose() { }
    }

    /// <summary>A document carrying one image, referenced by an image element between two
    /// paragraphs — the shape that makes inline placement observable.</summary>
    private static InternalDocument DocumentWithImage(int imageBytes = 64 * 64 * 3)
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "before", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(0), "", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "after", 0));
        doc.Images.Add(new ExtractedImage
        {
            ImageIndex = 0,
            Format = "png",
            Data = new byte[imageBytes],
            Width = 200,
            Height = 200,
        });
        return doc;
    }

    /// <summary>A PDF whose metadata flags the given pages as scans.</summary>
    private static InternalDocument ScannedPdf(params uint[] pages)
    {
        var doc = new InternalDocument("pdf") { MimeType = PdfMime };
        doc.Metadata.Format = new FormatMetadata
        {
            FormatType = "pdf",
            Payload = new PdfMetadata { ScannedPages = pages.ToList(), ScannedConfidence = 0.9f },
        };
        return doc;
    }

    private static List<string> OcrTexts(InternalDocument doc) =>
        doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.OcrText).Select(e => e.Text).ToList();

    // ── Disabled ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The default does nothing, and never even builds a recognizer.
    /// </summary>
    /// <remarks>
    /// The scanned PDF is the case that matters. An ordinary image document is also left alone by
    /// the later "is there any work?" check, so testing only that would pass even with the mode
    /// gate gone — and a document Disabled was meant to protect would be recognised anyway.
    /// </remarks>
    [Fact]
    public void DisabledRecognisesNothingAndLoadsNoModel()
    {
        foreach (var options in new OcrOptions?[] { null, new OcrOptions { Mode = OcrMode.Disabled } })
        {
            foreach (var (doc, bytes, mime) in new (InternalDocument, byte[], string)[]
                     {
                         (DocumentWithImage(), Array.Empty<byte>(), PngMime),
                         (ScannedPdf(1, 2), new byte[] { 1, 2, 3 }, PdfMime),
                     })
            {
                var engine = new FakeEngine();

                OcrProcessor.Process(doc, bytes, mime,
                    new ExtractionConfig { Ocr = options }, _ => engine);

                Assert.Empty(engine.Calls);
                Assert.Empty(OcrTexts(doc));
                Assert.Empty(doc.ProcessingWarnings);
                Assert.False(doc.Metadata.OcrUsed);
            }
        }
    }

    // ── AllImages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An image's text is inserted immediately after the element that references it, so it reads
    /// inline rather than being appended to the document.
    /// </summary>
    [Fact]
    public void AllImagesInlinesTheTextAtTheImagesPosition()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        var kinds = doc.Elements.Select(e => e.Kind.Tag).ToList();
        Assert.Equal(
            new[]
            {
                ElementKindTag.Paragraph, ElementKindTag.Image,
                ElementKindTag.OcrText, ElementKindTag.Paragraph,
            },
            kinds);
        Assert.Equal("recognised text", doc.Elements[2].Text);
        Assert.True(doc.Metadata.OcrUsed);
    }

    /// <summary>Rendering the document puts the recognised text between the surrounding
    /// paragraphs, which is what "inline" has to mean for a consumer.</summary>
    [Fact]
    public void TheInlinedTextRendersBetweenItsNeighbours()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        string content = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain).Content;

        Assert.Matches(@"before[\s\S]*recognised text[\s\S]*after", content);
    }

    /// <summary>
    /// Two referenced images each get their own text, each after its own image — the case that
    /// catches insertion order, since inserting the first one front-to-back would shift the
    /// second's position and land its text in the wrong place.
    /// </summary>
    [Fact]
    public void EachImagesTextFollowsItsOwnImage()
    {
        var doc = new InternalDocument("docx") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(0), "", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "between", 0));
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Image(1), "", 0));
        for (int i = 0; i < 2; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(call => new OcrImageResult($"text {call}", 1)));

        Assert.Equal(
            new[] { "", "text 0", "between", "", "text 1" },
            doc.Elements.Select(e => e.Text).ToList());
    }

    /// <summary>
    /// An image no element references — several extractors collect images separately from the
    /// text — still contributes its text, appended rather than dropped.
    /// </summary>
    [Fact]
    public void AnUnreferencedImagesTextIsAppendedRatherThanDropped()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "body", 0));
        doc.Images.Add(new ExtractedImage { ImageIndex = 0, Data = new byte[4096], Width = 200, Height = 200 });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.Equal(new[] { "recognised text" }, OcrTexts(doc));
    }

    /// <summary>An image too small to hold text is not worth recognising: icons, bullets, rules
    /// and spacer GIFs are images too.</summary>
    [Fact]
    public void AnImageBelowTheSizeFloorIsSkipped()
    {
        var doc = DocumentWithImage();
        doc.Images[0].Width = 16;
        doc.Images[0].Height = 16;
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(OcrTexts(doc));
    }

    /// <summary>An image of unknown size is recognised rather than skipped — several extractors
    /// record no dimensions, and dropping those would silently disable the mode for them.</summary>
    [Fact]
    public void AnImageOfUnknownSizeIsStillRecognised()
    {
        var doc = DocumentWithImage();
        doc.Images[0].Width = null;
        doc.Images[0].Height = null;

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.Single(OcrTexts(doc));
    }

    /// <summary>The per-document ceiling bounds the work a figure-heavy document can demand.</summary>
    [Fact]
    public void MaxImagesBoundsTheWork()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        for (int i = 0; i < 10; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages, MaxImages = 3 } },
            _ => engine);

        Assert.Equal(3, engine.Calls.Count);
    }

    /// <summary>An image that holds no text is a successful result, not a failure: no element,
    /// no warning, and OCR is not claimed to have been used.</summary>
    [Fact]
    public void AnImageWithNoTextAddsNothing()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(_ => OcrImageResult.Empty));

        Assert.Empty(OcrTexts(doc));
        Assert.Empty(doc.ProcessingWarnings);
        Assert.False(doc.Metadata.OcrUsed);
    }

    // ── ScanOnly ──────────────────────────────────────────────────────────────

    /// <summary>ScanOnly ignores ordinary images: that is the whole distinction from AllImages.
    /// </summary>
    [Fact]
    public void ScanOnlyDoesNotRecogniseOrdinaryImages()
    {
        var doc = DocumentWithImage();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(OcrTexts(doc));
    }

    /// <summary>A PDF with no page flagged as a scan is left alone, and no model is loaded.</summary>
    [Fact]
    public void APdfWithNoScannedPageLoadsNoModel()
    {
        var doc = ScannedPdf();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, new byte[] { 1, 2, 3 }, PdfMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } }, _ => engine);

        Assert.Empty(engine.Calls);
        Assert.Empty(doc.ProcessingWarnings);
    }

    /// <summary>
    /// A flagged page that cannot be rasterised — here because the bytes are not a PDF at all —
    /// warns and leaves the document intact rather than failing the extraction.
    /// </summary>
    [Fact]
    public void APageThatCannotBeRasterisedWarnsAndKeepsTheDocument()
    {
        var doc = ScannedPdf(1);
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "native text", 0));

        OcrProcessor.Process(doc, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, PdfMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } },
            _ => new FakeEngine());

        Assert.Contains(doc.ProcessingWarnings, w => w.Source == "ocr");
        Assert.Contains(doc.Elements, e => e.Text == "native text");
        Assert.Empty(OcrTexts(doc));
    }

    // ── failure handling ──────────────────────────────────────────────────────

    /// <summary>
    /// A missing checkpoint warns once, naming what was looked for, and the document keeps its
    /// native text. It also stops the flow asking again for every remaining image.
    /// </summary>
    [Fact]
    public void AMissingModelWarnsOnceAndKeepsNativeText()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        doc.Elements.Add(InternalElement.TextElement(ElementKind.Paragraph, "native text", 0));
        for (int i = 0; i < 5; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });
        var engine = new UnavailableEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        var warning = Assert.Single(doc.ProcessingWarnings);
        Assert.Equal("ocr", warning.Source);
        Assert.Contains("/nowhere", warning.Message, StringComparison.Ordinal);
        Assert.Equal(1, engine.Calls);
        Assert.Contains(doc.Elements, e => e.Text == "native text");
        Assert.False(doc.Metadata.OcrUsed);
    }

    /// <summary>One image failing does not stop the others: the rest of the document is still
    /// recognised, and the failure is reported.</summary>
    [Fact]
    public void OneFailedImageDoesNotStopTheRest()
    {
        var doc = new InternalDocument("png") { MimeType = PngMime };
        for (int i = 0; i < 3; i++)
            doc.Images.Add(new ExtractedImage
            {
                ImageIndex = (uint)i, Data = new byte[4096], Width = 200, Height = 200,
            });

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine(call => call == 1
                ? throw new InvalidOperationException("decoder blew up")
                : new OcrImageResult($"text {call}", 1)));

        Assert.Equal(new[] { "text 0", "text 2" }, OcrTexts(doc));
        Assert.Contains(doc.ProcessingWarnings, w => w.Message.Contains("decoder blew up", StringComparison.Ordinal));
    }

    /// <summary>The recognizer is disposed even when it was never usable — weights are large
    /// enough that leaking one matters.</summary>
    [Fact]
    public void TheEngineIsDisposed()
    {
        var doc = DocumentWithImage();
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } }, _ => engine);

        Assert.True(engine.Disposed);
    }

    /// <summary>
    /// When OCR contributed, the document says so — and says "mixed" rather than "ocr", because
    /// the native text is still there and a consumer treating the whole document as machine-read
    /// would be wrong about most of it.
    /// </summary>
    [Fact]
    public void AContributingPassIsRecordedAsMixed()
    {
        var doc = DocumentWithImage();

        OcrProcessor.Process(doc, Array.Empty<byte>(), PngMime,
            new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } },
            _ => new FakeEngine());

        Assert.True(doc.Metadata.OcrUsed);
        Assert.Equal("mixed", doc.Metadata.Additional["extraction_method"].GetString());

        // And it survives to the public result, which is where a consumer reads it.
        var result = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain);
        Assert.Equal(ExtractionMethod.Mixed, result.ExtractionMethod);
    }

    // ── The extractor/pass seam for a standalone image ───────────────────────

    /// <summary>
    /// A real PNG, so the extractor under test is the one that ships. The default size clears
    /// <see cref="OcrOptions.MinImagePixels"/> (64x64), below which the pass rightly skips an
    /// image as an icon or a spacer.
    /// </summary>
    private static byte[] Png(int width = 200, int height = 150)
    {
        using var image = new Image<Rgb24>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// A standalone image file carries no image data unless something asks for it.
    /// </summary>
    /// <remarks>
    /// The default has to stay empty: the bytes are the whole file, so attaching them
    /// unconditionally would put every image extraction's input into its own output. Upstream
    /// defaults the same way — its `images` config is an Option whose None means no image data.
    /// </remarks>
    [Fact]
    public void AStandaloneImageCarriesNoDataByDefault()
    {
        var doc = new ImageExtractor().Extract(Png(), PngMime, new ExtractionConfig());

        Assert.Empty(doc.Images);
        Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Image);
    }

    /// <summary>Asking for image data is what attaches it, with its dimensions.</summary>
    [Fact]
    public void AStandaloneImageCarriesItsDataWhenAsked()
    {
        byte[] png = Png();

        var doc = new ImageExtractor().Extract(png, PngMime,
            new ExtractionConfig { ExtractImages = true });

        var image = Assert.Single(doc.Images);
        Assert.Equal(png, image.Data);
        Assert.Equal("PNG", image.Format);
        Assert.Equal(200u, image.Width);
        Assert.Equal(150u, image.Height);
        Assert.Single(doc.Elements, e => e.Kind.Tag == ElementKindTag.Image);
    }

    /// <summary>QR decoding reads the same bytes, so it asks for them too.</summary>
    [Fact]
    public void QrDecodingAlsoAsksForTheData()
    {
        var doc = new ImageExtractor().Extract(Png(), PngMime,
            new ExtractionConfig { QrCodes = true });

        Assert.Single(doc.Images);
    }

    /// <summary>
    /// AllImages recognises a standalone image file — the case the two halves of this port used
    /// to miss between them.
    /// </summary>
    /// <remarks>
    /// The extractor produced an Image element with no bytes behind it and the pass walks
    /// `doc.Images` looking for bytes, so a plain `.png` came back with no text however the
    /// options were set — no warning, no error, just nothing. Upstream never had the gap because
    /// it recognises inside its image extractor; here the two are separate, so the config has to
    /// carry the need across. Drive it end to end rather than from a hand-built document: a
    /// document built by hand has the images the test gave it, which is precisely what was
    /// wrong.
    /// </remarks>
    [Fact]
    public void AllImagesRecognisesAStandaloneImageFile()
    {
        byte[] png = Png();
        var config = new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.AllImages } };

        var doc = new ImageExtractor().Extract(png, PngMime, config);
        var engine = new FakeEngine();

        OcrProcessor.Process(doc, png, PngMime, config, _ => engine);

        Assert.Equal(new[] { png.Length }, engine.Calls);
        Assert.Contains(doc.Elements, e => e.Text == "recognised text");

        var result = Derive.DeriveExtractionResult(
            doc, includeDocumentStructure: false, OutputFormat.Plain);
        Assert.Contains("recognised text", result.Content);
        Assert.Equal(ExtractionMethod.Mixed, result.ExtractionMethod);
    }

    /// <summary>ScanOnly reads pages from the PDF bytes, so it must not start hauling images.</summary>
    [Fact]
    public void ScanOnlyDoesNotAskForImageData()
    {
        var config = new ExtractionConfig { Ocr = new OcrOptions { Mode = OcrMode.ScanOnly } };

        Assert.False(config.NeedsImageData());
        Assert.Empty(new ImageExtractor().Extract(Png(), PngMime, config).Images);
    }
}

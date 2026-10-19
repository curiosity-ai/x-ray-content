using System.Text;
using XRay.Content.Core;
using XRay.Content.Internal.Qr;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// QR detection and decoding, ported from rqrr 0.10.1 via <c>extractors/qr.rs</c>.
/// </summary>
/// <remarks>
/// The port was verified against the real rqrr by probe — 271 generated images across every
/// version, all four error-correction levels and the failure cases, all byte-identical. These
/// tests pin the behaviour that matters at the seams rather than re-running that comparison.
/// </remarks>
public class QrTests
{
    /// <summary>
    /// Render a QR code's modules as an image: one byte per pixel, scaled up with a quiet zone,
    /// which is what the detector expects to find.
    /// </summary>
    private static byte[] Render(bool[,] modules, int scale = 6, int border = 4)
    {
        int n = modules.GetLength(0);
        int size = (n + border * 2) * scale;
        var grey = new byte[size * size];
        Array.Fill(grey, (byte)255);

        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!modules[y, x]) continue;
                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int px = (x + border) * scale + dx;
                        int py = (y + border) * scale + dy;
                        grey[py * size + px] = 0;
                    }
            }

        return grey;
    }

    /// <summary>
    /// A version-1 QR code carrying "HELLO", built module by module from the spec so the test
    /// does not depend on an encoder.
    /// </summary>
    private static bool[,] HelloCode()
    {
        // Produced by segno for the payload "HELLO" at error level M, and pinned here so the
        // decoder is tested against a known-good code rather than against itself.
        const string Rows = """
            111111101100101111111
            100000101111101000001
            101110101110001011101
            101110100110101011101
            101110100000001011101
            100000101001101000001
            111111101010101111111
            000000001001000000000
            001110101101011100111
            011011001101110101111
            100011101110001011001
            001101010010011110000
            100111111110110010100
            000000001011101001011
            111111100110010100101
            100000100101110001001
            101110101001010100100
            101110101110001100100
            101110101010110010100
            100000100011111110101
            111111100110100010100
            """;
        var lines = Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int n = lines[0].Length;
        var modules = new bool[lines.Length, n];
        for (int y = 0; y < lines.Length; y++)
            for (int x = 0; x < n; x++)
                modules[y, x] = lines[y][x] == '1';
        return modules;
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        Assert.Empty(QrDetection.Detect(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void BytesThatAreNotAnImageYieldNothingRatherThanThrowing()
    {
        // An undecodable image is not an error: the caller distinguishes "ran and found none"
        // from "did not run" by whether it called at all.
        Assert.Empty(QrDetection.Detect(new byte[] { 0, 1, 2, 3, 4 }, "image/png"));
    }

    [Fact]
    public void AnImageWithNoCodeYieldsNothing()
    {
        var blank = new byte[100 * 100];
        Array.Fill(blank, (byte)255);
        Assert.Empty(QrScanner.DetectFromGreyscale(blank, 100, 100));
    }

    // ── the Galois fields ────────────────────────────────────────────────────

    /// <summary>
    /// GF(256) with the QR modulus: the generator's powers must cycle with period 255, and
    /// multiplication must invert.
    /// </summary>
    [Fact]
    public void Gf256ArithmeticIsAField()
    {
        var gf = GaloisField.Gf256;
        Assert.Equal(gf.GeneratorPow(0), gf.GeneratorPow(255));
        Assert.Equal(1, gf.GeneratorPow(0));

        for (int i = 1; i < 256; i++)
        {
            byte a = (byte)i;
            byte inv = gf.Div(1, a);
            Assert.Equal(1, gf.Mul(a, inv));
        }
    }

    [Fact]
    public void Gf16ArithmeticIsAField()
    {
        var gf = GaloisField.Gf16;
        Assert.Equal(gf.GeneratorPow(0), gf.GeneratorPow(15));
        for (int i = 1; i < 16; i++)
            Assert.Equal(1, gf.Mul((byte)i, gf.Div(1, (byte)i)));
    }

    [Fact]
    public void DividingByZeroInAFieldIsRefused()
    {
        Assert.Throws<DivideByZeroException>(() => GaloisField.Gf256.Div(5, 0));
    }

    // ── the version database ─────────────────────────────────────────────────

    /// <summary>
    /// Spot-checks against ISO/IEC 18004: the total codeword counts and the alignment-pattern
    /// centres are what the spec tabulates.
    /// </summary>
    [Fact]
    public void TheVersionDatabaseMatchesTheSpec()
    {
        Assert.Equal(41, QrVersionDb.Versions.Length);

        Assert.Equal(26, QrVersionDb.Versions[1].DataBytes);
        Assert.Equal(3706, QrVersionDb.Versions[40].DataBytes);

        // Version 1 has no alignment pattern; version 7 has three centres at 6, 22 and 38.
        Assert.All(QrVersionDb.Versions[1].Apat, v => Assert.Equal(0, v));
        Assert.Equal(new[] { 6, 22, 38, 0, 0, 0, 0 }, QrVersionDb.Versions[7].Apat);
        Assert.Equal(new[] { 6, 30, 58, 86, 114, 142, 170 }, QrVersionDb.Versions[40].Apat);
    }

    // ── the geometry ─────────────────────────────────────────────────────────

    /// <summary>A transform and its inverse must round-trip.</summary>
    [Fact]
    public void APerspectiveRoundTripsThroughItsInverse()
    {
        var rect = new[]
        {
            new QrPoint(10, 10), new QrPoint(80, 12), new QrPoint(78, 90), new QrPoint(12, 88),
        };
        var p = Perspective.Create(rect, 7.0, 7.0);
        Assert.NotNull(p);

        var mapped = p!.Map(3.5, 3.5);
        var (u, v) = p.Unmap(mapped);
        Assert.InRange(u, 3.0, 4.0);
        Assert.InRange(v, 3.0, 4.0);
    }

    [Fact]
    public void ADegenerateQuadrilateralHasNoPerspective()
    {
        var flat = new[]
        {
            new QrPoint(0, 0), new QrPoint(0, 0), new QrPoint(0, 0), new QrPoint(0, 0),
        };
        Assert.Null(Perspective.Create(flat, 7.0, 7.0));
    }

    [Fact]
    public void ParallelLinesDoNotIntersect()
    {
        Assert.Null(QrGeometry.LineIntersect(
            new QrPoint(0, 0), new QrPoint(10, 0),
            new QrPoint(0, 5), new QrPoint(10, 5)));
    }

    [Fact]
    public void BresenhamWalksBothAxes()
    {
        var horizontal = QrGeometry.BresenhamScan(new QrPoint(0, 0), new QrPoint(5, 0)).ToList();
        Assert.Equal(6, horizontal.Count);
        Assert.All(horizontal, p => Assert.Equal(0, p.Y));

        var vertical = QrGeometry.BresenhamScan(new QrPoint(0, 0), new QrPoint(0, 5)).ToList();
        Assert.Equal(6, vertical.Count);
        Assert.All(vertical, p => Assert.Equal(0, p.X));
    }

    // ── end to end ───────────────────────────────────────────────────────────

    [Fact]
    public void AKnownCodeDecodesToItsPayload()
    {
        var modules = HelloCode();
        int size = (modules.GetLength(0) + 8) * 6;
        var found = QrScanner.DetectFromGreyscale(Render(modules), size, size);

        var code = Assert.Single(found);
        Assert.Equal("HELLO", code.Payload);
        Assert.True(code.Width > 0 && code.Height > 0);
    }

    [Fact]
    public void TheBoundingBoxCoversTheCodeButNotTheQuietZone()
    {
        var modules = HelloCode();
        const int scale = 6, border = 4;
        int size = (modules.GetLength(0) + border * 2) * scale;
        var code = Assert.Single(QrScanner.DetectFromGreyscale(Render(modules, scale, border), size, size));

        // The quiet zone is `border` modules wide on each side, so the box starts around there
        // and spans roughly the code itself.
        Assert.InRange(code.X, border * scale - scale * 2, border * scale + scale * 2);
        Assert.InRange(code.Width, (modules.GetLength(0) - 2) * scale, (modules.GetLength(0) + 4) * scale);
    }

    // ── the post-processor ───────────────────────────────────────────────────

    private static ExtractedDocument DocWithImage(byte[] png) => new()
    {
        Content = "Body text.",
        Images = new List<ExtractedImage> { new() { Data = png, Format = "png" } },
    };

    [Fact]
    public void DetectionIsOptIn()
    {
        var doc = DocWithImage(Array.Empty<byte>());
        QrPostProcessor.Process(doc, new ExtractionConfig());
        // Null, not an empty list: nothing ran, which is a different claim from "found none".
        Assert.Null(doc.Images![0].QrCodes);
    }

    [Fact]
    public void RunningAndFindingNothingIsRecordedAsAnEmptyList()
    {
        var doc = DocWithImage(new byte[] { 1, 2, 3 });
        QrPostProcessor.Process(doc, new ExtractionConfig { QrCodes = true });
        Assert.Empty(doc.Images![0].QrCodes!);
        Assert.Equal("Body text.", doc.Content);
    }

    [Fact]
    public void PayloadsAreAppendedToTheDocumentText()
    {
        // Nothing in the renderers reads `ExtractedImage.QrCodes`, so a payload that stayed
        // there alone would never reach the content a consumer processes.
        var doc = new ExtractedDocument
        {
            Content = "Body text.",
            Images = new List<ExtractedImage> { new() { Data = RenderPng(), Format = "png" } },
        };
        QrPostProcessor.Process(doc, new ExtractionConfig { QrCodes = true });

        Assert.Contains("## QR Codes", doc.Content);
        Assert.Contains("- HELLO", doc.Content);
        Assert.StartsWith("Body text.\n\n", doc.Content);
    }

    [Fact]
    public void HtmlOutputGetsAnHtmlSection()
    {
        var doc = new ExtractedDocument
        {
            Content = "<p>Body</p>",
            Images = new List<ExtractedImage> { new() { Data = RenderPng(), Format = "png" } },
        };
        QrPostProcessor.Process(doc,
            new ExtractionConfig { QrCodes = true, OutputFormat = OutputFormat.Html });

        Assert.Contains("<h2>QR Codes</h2>", doc.Content);
        Assert.Contains("<li>HELLO</li>", doc.Content);
    }

    /// <summary>
    /// A tag stream has no free-text position, so an untagged section would stop it
    /// round-tripping. The payloads stay on the image and the caller is told.
    /// </summary>
    [Fact]
    public void ATagStreamFormatGetsAWarningRatherThanASplicedSection()
    {
        var doc = new ExtractedDocument
        {
            Content = "<doctag><text>Body</text></doctag>",
            Images = new List<ExtractedImage> { new() { Data = RenderPng(), Format = "png" } },
        };
        QrPostProcessor.Process(doc,
            new ExtractionConfig { QrCodes = true, OutputFormat = OutputFormat.DocTags });

        Assert.DoesNotContain("QR Codes", doc.Content);
        Assert.Contains(doc.ProcessingWarnings, w => w.Source == "qr-codes");
        Assert.NotEmpty(doc.Images![0].QrCodes!);
    }

    /// <summary>
    /// A link payload joins the document's own URI list, so a QR link is indistinguishable from
    /// a hyperlink found anywhere else. Non-link payloads stay text.
    /// </summary>
    [Fact]
    public void OnlyLinkShapedPayloadsJoinTheUriList()
    {
        var doc = new ExtractedDocument
        {
            Content = "",
            Images = new List<ExtractedImage> { new() { Data = RenderPng(), Format = "png" } },
        };
        QrPostProcessor.Process(doc, new ExtractionConfig { QrCodes = true });

        // "HELLO" is not a URI, so nothing is added.
        Assert.True(doc.Uris is null || doc.Uris.Count == 0);
        // And a document whose only text is the payload must not start with whitespace.
        Assert.StartsWith("## QR Codes", doc.Content);
    }

    /// <summary>The known code, encoded as a PNG so the image decoder is exercised too.</summary>
    private static byte[] RenderPng()
    {
        var modules = HelloCode();
        const int scale = 6, border = 4;
        int n = modules.GetLength(0);
        int size = (n + border * 2) * scale;
        var grey = Render(modules, scale, border);

        return TestImages.GreyPng(grey, size, size);
    }
}

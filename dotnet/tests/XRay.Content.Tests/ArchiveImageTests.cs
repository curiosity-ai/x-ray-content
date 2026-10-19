using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using XRay.Content.Core;
using XRay.Content.Extractors;
using XRay.Content.Types;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Tests for the archive (zip/tar/gzip/7z), image, HWPX and ODS extractors.
/// Ports the Rust `extractors/archive.rs`, `extractors/image.rs`, `extractors/hwpx.rs`
/// and the ODS branch of `extractors/excel.rs` test intent.
/// </summary>
public class ArchiveImageTests
{
    private const string TestDocs = "/workspace/test_documents";

    private static ExtractedDocument ExtractPlain(byte[] bytes, string mime) =>
        new Extractor().Extract(ExtractInput.FromBytes(bytes, mime), new ExtractionConfig { OutputFormat = OutputFormat.Plain })
            .Results[0];

    // ── ZIP ────────────────────────────────────────────────────────────────

    private static byte[] BuildZip((string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var b = Encoding.UTF8.GetBytes(content);
                s.Write(b, 0, b.Length);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Zip_ExtractsFileListAndTextContent()
    {
        var bytes = BuildZip(new[] { ("test.txt", "Hello, World!") });
        var doc = ExtractPlain(bytes, "application/zip");

        Assert.Equal("application/zip", doc.MimeType);
        Assert.Contains("ZIP Archive", doc.Content);
        Assert.Contains("test.txt", doc.Content);
        Assert.Contains("Hello, World!", doc.Content);

        var archive = Assert.IsType<ArchiveMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("ZIP", archive.Format);
        Assert.Equal(1u, archive.FileCount);
        Assert.Contains("files", doc.Metadata.Additional.Keys);
    }

    [Fact]
    public void Zip_Invalid_Fails()
    {
        var result = new Extractor().Extract(
            ExtractInput.FromBytes(new byte[] { 0, 1, 2, 3, 4, 5 }, "application/zip"),
            new ExtractionConfig());
        Assert.NotEmpty(result.Errors);
    }

    // ── TAR ────────────────────────────────────────────────────────────────

    private static byte[] BuildTar((string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var tw = new TarWriter(ms, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data };
                tw.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void Tar_ExtractsFileListAndTextContent()
    {
        var bytes = BuildTar(new[] { ("test.txt", "Hello, World!") });
        var doc = ExtractPlain(bytes, "application/x-tar");

        Assert.Contains("TAR Archive", doc.Content);
        Assert.Contains("test.txt", doc.Content);
        Assert.Contains("Hello, World!", doc.Content);

        var archive = Assert.IsType<ArchiveMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("TAR", archive.Format);
        Assert.Equal(1u, archive.FileCount);
    }

    // ── GZIP ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gzip_DecompressesAndExtractsText()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var b = Encoding.UTF8.GetBytes("Hello from gzip extraction!");
            gz.Write(b, 0, b.Length);
        }
        var doc = ExtractPlain(ms.ToArray(), "application/gzip");

        Assert.Contains("GZIP Archive", doc.Content);
        Assert.Contains("Hello from gzip extraction!", doc.Content);
        var archive = Assert.IsType<ArchiveMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("GZIP", archive.Format);
        Assert.Equal(1u, archive.FileCount);
    }

    // ── 7z ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SevenZip_TruncatedArchive_Throws()
    {
        // A bare signature with no headers is invalid input, not an empty archive.
        Assert.ThrowsAny<Exception>(() =>
            new SevenZipExtractor().Extract(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
                "application/x-7z-compressed", new ExtractionConfig()));
    }

    [Fact]
    public void SevenZip_LzmaArchive_ExtractsListingAndText()
    {
        // documents.7z from the fixture corpus (216 bytes, embedded verbatim):
        // one LZMA1 folder with two text substreams, plus an LZMA-compressed header.
        byte[] archive = Convert.FromHexString(
            "377ABCAF271C0004468192F89700000000000000210000000000000078864A39" +
            "01001D66616B655F746578742E747874636F6E74726163745F746573742E7478" +
            "74000000813307AE0FD02CF4BC9F3F4741070ABEBB844EBE67D49BFB468D43BE" +
            "E316ECB252530CDD05A3C594E9CD9A1A3ED4970EA1AAC00D3AEE4BD97D10F7A5" +
            "A3A46ED4FEA4EC721F7307FA68C7E71B28FBD46B161AFFE2DCB3FF0D01EBB199" +
            "EEDFBB2B3622484026F221FF16D8BCCF8BD4806A00000017062201097500070B" +
            "01000123030101055D001000000C809E0A01977991E10000");
        var doc = ExtractPlain(archive, "application/x-7z-compressed");

        Assert.Contains("7Z Archive (2 files, 30 bytes)", doc.Content);
        Assert.Contains("text/simple.txt (17 bytes)", doc.Content);
        Assert.Contains("text/multilingual.txt (13 bytes)", doc.Content);
        Assert.Contains("contract_test.txt", doc.Content);
        Assert.Contains("fake_text.txt", doc.Content);
        var archiveMeta = Assert.IsType<ArchiveMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("7Z", archiveMeta.Format);
        Assert.Equal(2u, archiveMeta.FileCount);
    }

    // ── Image ────────────────────────────────────────────────────────────────

    [Fact]
    public void Image_Png_ExtractsDimensionsAndFormat()
    {
        byte[] png = TestImages.Png(7, 3);

        var doc = new ImageExtractor().Extract(png, "image/png", new ExtractionConfig());
        var meta = Assert.IsType<ImageMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(7u, meta.Width);
        Assert.Equal(3u, meta.Height);
        Assert.Equal("PNG", meta.Format);
        Assert.Empty(meta.Exif);
    }

    [Fact]
    public void Image_Jpeg_ReportsJpegFormat()
    {
        byte[] jpeg = TestImages.Jpeg(16, 8);

        var doc = new ImageExtractor().Extract(jpeg, "image/jpeg", new ExtractionConfig());
        var meta = Assert.IsType<ImageMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal("JPEG", meta.Format);
        Assert.Equal(16u, meta.Width);
        Assert.Equal(8u, meta.Height);
    }

    // ── HWPX ───────────────────────────────────────────────────────────────

    [Fact]
    public void Hwpx_ExtractsTextAndMetadata()
    {
        string path = Path.Combine(TestDocs, "hwpx", "simple.hwpx");
        if (!File.Exists(path)) return; // fixture-gated

        var doc = ExtractPlain(File.ReadAllBytes(path), "application/haansofthwpx");
        Assert.Contains("Hello from HWPX document.", doc.Content);
        Assert.Contains("Second paragraph with more content.", doc.Content);
        Assert.Equal("Test HWPX Document", doc.Metadata.Title);
        Assert.Equal("HWPX", doc.Metadata.DocumentVersion);
        Assert.Equal(new List<string> { "Kreuzberg Tests" }, doc.Metadata.Authors);
    }

    // ── ODS ────────────────────────────────────────────────────────────────

    [Fact]
    public void Ods_ExtractsSheetsAsTables()
    {
        string path = Path.Combine(TestDocs, "data_formats", "stanley_cups.ods");
        if (!File.Exists(path)) return; // fixture-gated

        var doc = ExtractPlain(File.ReadAllBytes(path), "application/vnd.oasis.opendocument.spreadsheet");

        var meta = Assert.IsType<ExcelMetadata>(doc.Metadata.Format!.Payload);
        Assert.Equal(2u, meta.SheetCount);
        Assert.Equal(new List<string> { "Stanley Cups", "Stanley Cups Since 67" }, meta.SheetNames);
        Assert.Equal(2, doc.Tables.Count);
        Assert.Contains("Maple Leafs", doc.Content);
        Assert.Equal("2", doc.Metadata.Additional["sheet_count"].GetString());
    }
}

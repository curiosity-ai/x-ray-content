// Tests for the Microsoft Cabinet reader ([MS-CAB]) and its MSZIP ([MS-MCI]) and LZX ([MS-PATCH])
// decoders. The expected hashes were produced by the Rust `cab` crate (lzxd + flate2) over the same
// cabinets, so they are an independent decoder's output, not this reader's.

using System.Security.Cryptography;
using System.Text;
using XRay.Content.Internal.Cab;
using Xunit;

namespace XRay.Content.Tests;

public class CabinetReaderTests
{
    private static readonly byte[] OneNoteSectionGuid = { 0xE4, 0x52, 0x5C, 0x7B, 0x8C, 0xD8, 0xA7, 0x4D };
    private static readonly byte[] OneNoteTocGuid = { 0xA1, 0x2F, 0xFF, 0x43, 0xD9, 0xEF, 0x76, 0x4C };

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void OneNotePackage_LzxWithE8AndAlignedBlocks_MatchesReferenceDecoder()
    {
        var cab = CabinetReader.Read(Convert.FromBase64String(OneNotePackageBase64));

        var folder = Assert.Single(cab.Folders);
        Assert.Equal(CabinetCompression.Lzx, folder.Compression);
        Assert.Equal(18, folder.LzxWindowBits);
        Assert.Equal(16592, folder.UncompressedSize);

        Assert.Equal(2, cab.Entries.Count);
        Assert.Equal("打开笔记本.onetoc2", cab.Entries[0].Path);
        Assert.Equal("新分区 1.one", cab.Entries[1].Path);
        Assert.Equal(5144, cab.Entries[0].Size);
        Assert.Equal(11448, cab.Entries[1].Size);
        Assert.Null(cab.Entries[0].LastModified);

        var toc = cab.Extract(cab.Entries[0]);
        var section = cab.Extract(cab.Entries[1]);
        Assert.Equal(OneNoteTocGuid, toc[..8]);
        Assert.Equal(OneNoteSectionGuid, section[..8]);
        Assert.Equal("ed8e069748a3434e1c45782f1ca2a609efced01faef84bc3773cfd9767ca6540", Sha256(toc));
        Assert.Equal("a5fca79c37ea7fbd26ec8f04dc838c4badd576f85595da57acb61575e10ee94d", Sha256(section));
    }

    [Fact]
    public void MsZipFolder_DictionaryCarriesAcrossDataBlocks()
    {
        var cab = CabinetReader.Read(Convert.FromBase64String(MsZipCabinetBase64));

        var folder = Assert.Single(cab.Folders);
        Assert.Equal(CabinetCompression.MsZip, folder.Compression);
        Assert.Equal(3, folder.BlockCount);

        Assert.Equal(new[] { "readme.txt", "docs/lines.txt", "docs/tail.txt" }, cab.Entries.Select(e => e.Path));
        Assert.Equal(new long[] { 19, 80000, 5000 }, cab.Entries.Select(e => e.Size));

        var readme = cab.Extract(cab.Entries[0]);
        var lines = cab.Extract(cab.Entries[1]);
        var tail = cab.Extract(cab.Entries[2]);
        Assert.Equal("MSZIP test cabinet\n", Encoding.ASCII.GetString(readme));
        Assert.StartsWith("00001 folder window block\n", Encoding.ASCII.GetString(lines));
        Assert.Equal("ce2008bc4c08d4e912ba03735f5a8dbe58fb95cf05cf55a3e2c9e983ae361a09", Sha256(readme));
        Assert.Equal("0e476bac1859eca698ad763a94f22d9840fcd7a2d1db30f3a1debf4d20838fbd", Sha256(lines));
        Assert.Equal("8e9d750dda1d9ce8dbe5342422a66682b014add569f427a7416fd601d3dc7506", Sha256(tail));
    }

    [Fact]
    public void LzxVerbatimAndUncompressedBlocks_SpanningDataBlocks_MatchReferenceDecoder()
    {
        var cab = CabinetReader.Read(Convert.FromBase64String(LzxVerbatimCabinetBase64));

        var folder = Assert.Single(cab.Folders);
        Assert.Equal(CabinetCompression.Lzx, folder.Compression);
        Assert.Equal(16, folder.LzxWindowBits);
        Assert.Equal(2, folder.BlockCount);

        Assert.Equal("text/one.txt", cab.Entries[0].Path);
        Assert.Equal("café.bin", cab.Entries[1].Path);
        Assert.Equal(34000, cab.Entries[0].Size);
        Assert.Equal(1001, cab.Entries[1].Size);

        var text = cab.Extract(cab.Entries[0]);
        var binary = cab.Extract(cab.Entries[1]);
        Assert.StartsWith("00001 folder window", Encoding.ASCII.GetString(text));
        Assert.Equal(new byte[] { 0xC5, 0xE9, 0xB7, 0x3C, 0x7F, 0xAE, 0x33, 0xD2 }, binary[..8]);
        Assert.Equal("08fa7fdc3f729c66c302b47da66511d1feb9b14a4129f3104878482d53e50da0", Sha256(text));
        Assert.Equal("4ac1e4b01485bd10e5f32b9854309bb83835c916f052b8d957b8c32daf8a8272", Sha256(binary));
    }

    [Fact]
    public void Header_ReserveFieldsAndCabinetSetNames_AreParsed()
    {
        var cab = CabinetReader.Read(Convert.FromBase64String(LzxVerbatimCabinetBase64));

        Assert.Equal(0x1234, cab.SetId);
        Assert.Equal(1, cab.SetIndex);
        Assert.Equal("prev.cab", cab.PreviousCabinet);
        Assert.Equal("next.cab", cab.NextCabinet);

        var entry = cab.Entries[0];
        Assert.Equal(new DateTime(2025, 4, 15, 12, 0, 0), entry.LastModified);
        Assert.Equal(CabinetFileAttributes.ReadOnly | CabinetFileAttributes.Archive, entry.Attributes);
    }

    [Fact]
    public void StoredFolder_RoundTripsFilesAcrossFolders()
    {
        var first = Encoding.ASCII.GetBytes("first file");
        var second = Encoding.ASCII.GetBytes("second\\file");
        var data = BuildCabinet(0, ("a\\one.txt", first, 0), ("two.txt", second, 1));

        var cab = CabinetReader.Read(data);

        Assert.Equal(2, cab.Folders.Count);
        Assert.Equal("a/one.txt", cab.Entries[0].Path);
        Assert.Equal(first, cab.Extract(cab.Entries[0]));
        Assert.Equal(second, cab.Extract(cab.Entries[1]));
    }

    [Fact]
    public void Names_FlaggedUtf8_InvalidUtf8FallsBackToWindows1252()
    {
        Assert.Equal("新分区", CabinetReader.DecodeName(Encoding.UTF8.GetBytes("新分区"), utf8: true));
        Assert.Equal("新分区", CabinetReader.DecodeName(Encoding.UTF8.GetBytes("新分区"), utf8: false));
        Assert.Equal("café€", CabinetReader.DecodeName(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x80 }, utf8: false));
    }

    [Fact]
    public void QuantumFolder_ListsButThrowsNotSupportedOnExtract()
    {
        var data = BuildCabinet(0x0102, ("q.txt", new byte[] { 1, 2, 3 }, 0));

        var cab = CabinetReader.Read(data);

        Assert.Equal(CabinetCompression.Quantum, cab.Folders[0].Compression);
        Assert.Throws<NotSupportedException>(() => cab.Extract(cab.Entries[0]));
    }

    [Fact]
    public void MissingSignature_Throws()
    {
        var data = Convert.FromBase64String(OneNotePackageBase64);
        data[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => CabinetReader.Read(data));
    }

    [Fact]
    public void TruncatedCabinet_Throws()
    {
        var data = Convert.FromBase64String(MsZipCabinetBase64);

        Assert.Throws<InvalidDataException>(() => CabinetReader.Read(data.AsSpan(0, 30)));
        Assert.Throws<InvalidDataException>(() => CabinetReader.Read(data.AsSpan(0, data.Length - 100)));
    }

    [Fact]
    public void CorruptLzxPayload_ThrowsInvalidData()
    {
        var data = Convert.FromBase64String(LzxVerbatimCabinetBase64);
        var cab = CabinetReader.Read(data);
        var block = cab.Folders[0].Blocks[0];
        for (int i = 0; i < 64; i++) data[block.DataOffset + 4 + i] = 0xFF;

        var corrupt = CabinetReader.Read(data);

        Assert.Throws<InvalidDataException>(() => corrupt.Extract(corrupt.Entries[0]));
    }

    [Fact]
    public void DeclaredSizeAboveLimit_IsRejectedBeforeDecoding()
    {
        var data = Convert.FromBase64String(OneNotePackageBase64);

        var ex = Assert.Throws<InvalidDataException>(() => CabinetReader.Read(data, maxTotalUncompressed: 16000));
        Assert.Contains("16000", ex.Message);
    }

    [Fact]
    public void FileOutsideFolderData_Throws()
    {
        var data = BuildCabinet(0, ("x.txt", new byte[] { 1, 2, 3 }, 0));
        int fileTable = BitConverter.ToInt32(data, 16);
        BitConverter.GetBytes(4).CopyTo(data, fileTable);

        Assert.Throws<InvalidDataException>(() => CabinetReader.Read(data));
    }

    private static byte[] BuildCabinet(ushort typeCompress, params (string Name, byte[] Data, int Folder)[] files)
    {
        int folderCount = files.Max(f => f.Folder) + 1;
        var fileTable = new MemoryStream();
        var folderOffsets = new long[folderCount];
        foreach (var (name, content, folder) in files)
        {
            var w = new BinaryWriter(fileTable);
            w.Write((uint)content.Length);
            w.Write((uint)files.Where(f => f.Folder == folder).TakeWhile(f => f.Name != name).Sum(f => f.Data.Length));
            w.Write((ushort)folder);
            w.Write((ushort)0x5A8F);
            w.Write((ushort)0x6000);
            w.Write((ushort)0x20);
            w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((byte)0);
        }

        int headerSize = 36 + folderCount * 8;
        int dataStart = headerSize + (int)fileTable.Length;
        var dataArea = new MemoryStream();
        var folderTable = new MemoryStream();
        for (int folder = 0; folder < folderCount; folder++)
        {
            var payload = files.Where(f => f.Folder == folder).SelectMany(f => f.Data).ToArray();
            var fw = new BinaryWriter(folderTable);
            fw.Write((uint)(dataStart + dataArea.Length));
            fw.Write((ushort)1);
            fw.Write(typeCompress);
            var dw = new BinaryWriter(dataArea);
            dw.Write(0u);
            dw.Write((ushort)payload.Length);
            dw.Write((ushort)payload.Length);
            dw.Write(payload);
        }

        var cab = new MemoryStream();
        var hw = new BinaryWriter(cab);
        hw.Write(Encoding.ASCII.GetBytes("MSCF"));
        hw.Write(0u);
        hw.Write((uint)(dataStart + dataArea.Length));
        hw.Write(0u);
        hw.Write((uint)headerSize);
        hw.Write(0u);
        hw.Write((byte)3);
        hw.Write((byte)1);
        hw.Write((ushort)folderCount);
        hw.Write((ushort)files.Length);
        hw.Write((ushort)0);
        hw.Write((ushort)0);
        hw.Write((ushort)0);
        hw.Write(folderTable.ToArray());
        hw.Write(fileTable.ToArray());
        hw.Write(dataArea.ToArray());
        return cab.ToArray();
    }

    // c-note.onepkg: a OneNote notebook package with UTF-8 names stored without the UTF-8 flag.
    internal const string OneNotePackageBase64 = """
        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
        """;

    // Written by the Rust `cab` crate: one MSZIP folder of three data blocks.
    private const string MsZipCabinetBase64 = """
        TVNDRgAAAAAZKgAAAAAAACwAAAAAAAAAAwEBAAMAAAAAAAAAhAAAAAMAAQATAAAAAAAAAAAAT129uSAAcmVhZG1lLnR4dACAOAEAEwAAAAAAT129uSAA
        ZG9jc1xsaW5lcy50eHQAiBMAAJM4AQAAAE9dvbkgAGRvY3NcdGFpbC50eHQA9njKxD8QAIBDS3zdTYpe1xWF4X5GUUM4+3/vIaQRCKSXnn9kMHEssAWe
        foKk+u6pe89abliYsmwWgvvCfhrnH//699//+fbl059f3n764cdff//05W/r/3/J2y+ff/v50x9vf/36+8+f/3r78bfPP/3n60/07edPv/z2w5dPb79/
        /vLpx8+f//P93/n6U3v/Dd9/+7dfvv7I337544f/fvr+9/ff/PVH8e2///bnp5++/Pr592//ytef5PV/ef/ffv/164/r9Vu+/Qf2Cf3+T69frxFzm3ct
        kPXcd40Que3bZ4h+WPgaIXabty0Qfw7cRkjcBr4mSD7mbSPqtm9b0M99H0bM/U/wfYauD/O2ESq3gdsC1efA1wi127xrgvpj3zVC47ZvX6B5+BN8jajb
        vH1Gfxi4j5jbwNcCW8951wiT275rgulj3z7C7P4n+L7A/DlvG2FxG7jNsPww8BpRt3nbgn7u20bMbd8+wdfzT/B9hN+/L9sCP3xhthF+/8S8ZvjHL8w1
        wh/fl9cCP3xgPoy4f2GuCc/vyz7i/oXZFsThE/MaEfcvzDUjbt+X14i4f2D2BXH4wrxGxP37sk2I5xdmH1GoENGsEDGwELlgIVJQIVJpIdJwIdJxITJg
        ITJZIbJIIbLPhcjBhahFC1GCClFKClEGC1HOC1GBClGJClGFC1FNClEDC9GLFKKFFKKVFKINF6IdFaIDFaKTFaILFqKbFqIHFWIWLcQILsTouRBjsBDj
        rBATpBCTuBBTuBDTtBAzoBCy1rkQsgQVQpbSQsgyUAhZTgohK2AhZCUuhKxChZDV50LIGlwIkYULISKwECJKCiFioBAiTgohEqgQIokKIVKgECLNCiEy
        sBCiCxZCVFAhRJUUQtRwIUT9WAjRgIUQTVYI0QKFEG1cCNFBhRBbtBBiAgohpqAQYgYLIea4EGKBCiGWuBBihQsh1rgQYgMLIb5AIcQFFEJcSSHEDRVC
        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
        """;

    // Hand-encoded LZX (window 2^16): verbatim and odd-length uncompressed blocks, one of which
    // crosses the CFDATA boundary; header, folder and data reserve areas; previous/next cabinet
    // names; a Windows-1252 file name.
    private const string LzxVerbatimCabinetBase64 = """
        TVNDRgAAAAC6MgAAAAAAAFkAAAAAAAAAAwEBAAIABwA0EgEABgADAqqqqqqqqnByZXYuY2FiAERpc2sgMQBuZXh0LmNhYgBEaXNrIDMAjwAAAAIAAxB3
        d3fQhAAAAAAAAAAAj1oAYCEAdGV4dFxvbmUudHh0AOkDAADQhAAAAACPWgBgIQBjYWbpLmJpbgAAAAAA9CgAgFVVBxAEU0RERERFRFVVWFWIiIiIiIiI
        iIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI
        iIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiEiERERERFRFVVWFWIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI
        iIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiEiERE
        RERFRFVVWlWqqpmqmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZ
        mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZQBgiRjED2LwyZISccwe4SW/IiDtGDHnD1oykCoCxQMgyZI2ZE4aUoW5A
        nTemDHkj17xqVQyYveczmJdGgkYxA8LIMm1KzcT3tsYIwWYOGVPS0DdvFlUGl3DVbxeZAVOtvRECfAbZMGONmOMm0imHl2EUATfaVIeXVjfY7hpNGFUt
        jLfD5YGqUzFg7apVFzXAjC+drsHNtJS2M+5y6wLpaGl4AGdxWmO7EVN8rdkDMwtwKVZtuKnoCi/l7o1R0RQ16FLe4lYBG/B1mxwywtf8tSMJhCGa8lSW
        4FLoxtzV1wUpVWV4AG/yGj7VxpdwcT0U7W2NLsgsgJuJrvjSe9ZO1wDevrS3Nl2A3TilNb3xbHmqoy/tKg41lbktwaXWzbgrIkwxQ7yF7Am2raaOGgNU
        +N62RpfhVsDNZVbE1FVnLOuLLuBS3tJ1AaPjmDprjRLgK8gpaG14TGS51EureUN35X0BlJA4uoe3y6KYumsOFuA6yFK30LhuYdz0awUv1cNpVoxEX+L0
        SybqKKdjMg752ppLpnCkSt5SPJxbxV3KiNPomKbOcFol49SPp7kxEEdZDVNxOKqSrNTTNZmIf2z9pXE4iJJ3KRhOU8VbKofT7Bil2Tj9kok4xpyexThK
        VdNeGYi1JjRMxuEYW3OpIk6qZEw14TQp5kqbbaNTOHsdeuLjcE0lc2kiTppiMdWH0y8ZheBpGwzENrXtpXE4aJLu0kicU8WY6sNpdYylZjj9JFOlzDit
        inE62htWxOmXTGpcp2MyDsrM01wZiI4u/lI8nEPFuxQRp80xmbrDaZeMBPD0jUzhVFbmUjicdcmXUuK0WibSn5z+yTjtbI4q4jQrJnSap78xDovb0xOZ
        iHou9MTG4a4q9tIpnNuSXJqH02gZJ2IRp1syUwc4DYp5etfTXRiIy+yOqTicc8mVWjoHE3EVFcfUHE62ZJeC4bS3TGk1TmtkHBKydJdG4h4rYeoOp9Ux
        l7ri9EsGqV5Oe2Ic+rKnJzIRR1kdU3E415ZMyowY4TSVTKVIOL2KeZS3p7sxEZ3RtEzG4R9Lc6kgTrJipo5wGiXjqVVOe2Ui/rH0l8bhIErdpWM4TBVv
        qRxOs2OVZjf2GK3A28ZmXzqH06oYS6lx2iQDqdachss4FWNpLkzERpWGqTycM8V3aRROXsee+DicU8lcWojTpphMteF0S8Yh+Dd2FK3ATTZV7aVxOGiS
        7tJInFPFmOrDaXWMpWY4/SRTpcw4rYpxOtrTXpmIuC7H1BxOmWW5lBCnXTKlHDj9inkphmNX23tvlbkBU7c4LZJxAP6NnTvxpaYUlae5Mg5UXe2liTin
        lr/0DqdbMks7cboVEzrN018Yh8Xt6YlMxD2XemLjcFcVW2l7Y+rUW2pLc3CaLeNEDOL0SybqYKdhMQ/vWrpLA3GZHTF1h9MuGVJrnObFRHVF0zEZh6At
        7aVTOM0tWlqH0ywZpYSNXW/lRQ51l9MwGIfd6vpLA3GvJb1UDqdZMRN9iNMsmamjnI7LOOVraS6ZwpEqekvzcG8VdykiTqNjmTrDaZaMUj/Gprr0N7FZ
        AVNHOA2ScdSqp70yEf/Y+kvjcBAl7lIxnKaKt1QOp9kxS7Nx+iUTcYw4PYpxlKqnvTIQak1pmIzDMbamUvfGorIH3kZ1hqk8nDPFd2kUTl7Hnvg4nFPJ
        XFqI06aYTLXhdEvGIfg3dhStwE02Ve2lcThoku7SSJxTxZjqw2l1jKVmjX1z827upQw4rYpxOtrTXpmIuC7H1BxOmWW5lBCnXTKlHDj9inkphk53YyJ1
        m9MyGYfgL9NJMYVRWZpL43DVJVZK3tjgtpbX9KmnvzIOO1u6SxNxzRVfOofT7RiJxcTpl0xiPXB6FeNpV057yRTNbWkujMMxtNMTmYiDLYapPJxrxSa9
        toydN3O0FXB1mdMxGYdrLuZSRJxFxTF1h9MtGaWgOO0tU1rN01oZh4Qs3aWROMeKmLrDaXWMpW44/ZKBqtfTXhiHvqxsiXpvsyiNgHWY0zEZh3xtzaVT
        OFIlbykeTq1iLuXE6XRMU2c4LZJx6sfTXBiIo6yGqTicVcleaojTbJmlfzj9knEpiGNXXHubwDIVvaVxOM2OX5qI0yyZiWPD6VWMpVQ47ZKBVGtOw2Uc
        irE0lybio0rDVB5OmWK7tAqnr2NPfBxOqWQqrW9stUrA22tmx1QcTr9kF4J6Y0TRBdxqU9NeGYeNJukujMQ+VYapOJxWx19qhdNMMlrKh9OtGKWjOO2S
        iY3rdEzG4ZlZm0rOG1OrFXDK0dNfmIdiqHSXJuK3OS1TcTj+kp0A8Y2mO5WluRQOp10ypVQ47ZaJ9KenvzIOO1u6SxNxzRVfOofT7RiJxW9scpvG6a3p
        6S+Mw11V7aVTOLcluTQOp9AyT8QiTrZkpg5wGhXz9K6nuzEQl9kdU3E45pIutcRpVExTVzgdknEK2thWNt/NNJpLtbQOp1kySwlxuhUjdY/TMBiH3er6
        SwNxryW9VA6nWTETfYjTLJmpo5yOyzjla2kumcKRKnpL83BvFVcpeWPA294eih1pmYzDP5bmUkCcZcVMHeE0SsZSq5z2y0T9Y+kvjMNBlLpLxnCZKt5S
        OJxmxy/NxOmWTMQx4fQqxlKqxra68zfvqg6nYTIOxdiaSxNxUSVhqg+nTDFd2oXT1zEnvg6nVDKX1uI0KSZTrTgdknEM/jhdloHUpqe9Mg4aTdhV1t7b
        0fLA1anTMBiHzer6S6ZwmUlaS+NwtBW9dBGnXTKpcZyOyzgpM05zZCA5uvpL83AMFe5SRJw2x2XqDqdfMhmJb+zdiSw1pag4zZJxpeppL0zEP7X9pXE4
        2ZJd2ojTrpjSaZz+xzgsbk5PZCLrudMTGIe7qtpLpnBuS3NpHE6hZZaI88aAt5U90bbD1B5OtWKX3uJ0OwbqMqdjMg7WXM2liTiKimPqDqdbMktBcNpb
        prSap7UyDglZuksjcY8VMHWH0+oYSt0b+9vWuOnXCl6qh9OsGIm+xOmWTNRRTsdlHPK1NJdM4UiVvaV5OLeKu5QRp9ExTJ3htEvGqR9Oc2IgjrIapuNw
        VSVbqXxjctUB3k+2/tI4nETJuxQMp6liLZXD6XaM0myc/slEHGNOT2IcpappLwzEWpMapuNwjC25VBGnVTKmGnAaFPOlzY1dc+9qbr5XdJfG4ZpK5tJE
        nDXFY6oOp18yC8G9saJoAm61qWkvjMNGk3SXRuKfKsNUHE6rYy81wukmmS1lw2lWjNLRxraz85wW2G7H1BxOmWW5lBCnXTKlHDj9inkphk53YyJ1m9My
        GYfgL9gJE99qulFZmkvjcNUlXkqI02qZS39w+iXjtLOxq6y8m6iugNKJnP7HOCxuTk9kIuu50xMYh7uq2kumcG5Lc2kcTqFlnohEnGzJTB3hNCvm6V1O
        d2MgLrM6puNwzSVUat/Y5RaO1+qMp2MyDkFb2kumcJpbtbQOp1kySwlxuhUjdY/TMBiH3er6SwNxryW9VA6nWTETfYjTLJmpo5yOyzjla2wqe2/mWFKl
        bykeTq1iLuXE6XRMU2c4LZJx6sfTXBiIo6yGqTicVcleaojTbJmlfzj9knEpiE53xRgqU9NbGIfZ7H2l6402RhnwjrH0l8bhqir2UkCcNclhqg6n2DJL
        xXGaJROqUadhMQ/aTNNdMYW+16cnMg7WVDSXJuKtKR1TcTj+klEEQqQmjCrgUpuc9sk4aTROd2Ik9ak0TMbhszr+UimcZpLWUjicbcUvHcRpl0xqXKdj
        Mg7KzNNcGYiOLv5SPJxDxasUvbHK7YC329yWqTicf8lOAPjG053KUlyKh9MuGVKqnPbLRPpT018Zh50t3aWJOOaKL53D6XaMxOLi9EsmsZ44PYpxtKux
        req9NXW5Lc2lcTiGlnoiE3GwJTB1h9OtmKV3ON2OgbrM6ZiMwzWXc6kiTqJimLrD6ZaMUtCc9pYpreZpLYzDQpauUvLGh7fLIpi6w2l1jKVuOP2SgarX
        014Yh76s6YlMxFGWx9QcTrVll/LhNJVMpUg4vYp5lLenuzERndG0TMbhH0tTqXpjWF2Am6Oshqk4nFXJXmqI02yZpX84/ZJxKYhOd8UYKlPTWxiH2ez9
        pYk4xpKeODicVcVeSojTJhlMteE0W8apGGNT2XtvUboDVKNOw2IetJmnu2MKfK9OT2QcralpLkzEWlM6puNw/CW7ENYbJooq4FKbnPbJOGk0TndiJPWp
        NEzG4bM6vlL5xve5BjfWUjicbcUvHcRpl0xqXKdjMg7KzNNcGYiOLv5SPJxDxbsUEafNMZm6w2mXjCTwwumsmKWoOM2ScaXqbCtbb2twVMtf+ofTLRml
        nTjdiomd5ukvjMPidvTEJuKeSz2xcTirir20Cqe2ZJfm4TRaxokYxOmWTNTBTsNiHt61dpWdN3O0FXB1mdMxGYdrLuZSRJxFxTF1h9MtGaWgOO0tU1rN
        01oZh4Qs3aWROMeKmLrDaXWMpW44/ZKBqtfTXhiHvqxsiXpvsyiNgHWY0zEZh3xtzaVTOFIlbykeTq1iLuXE6XRMU2c4LZJx6sfTXBiIo6yGqTicVcle
        aojTbJmlfzj9knEpiGs0vbFgropNUpmc3sc4zWbpL0zEMZb0xMbhqir2UkCcNclhqg6n2DJLxXGaJROqUadhMQ/aTNNdMYW+16cnMg7WVDaVpTftWjPg
        qjWnYzIOwV+xC2i9bqKpAi+1w2mTjJdG4nQqRlSfTsNjHDWr6S+ZwmUmaS2Mw9FW9tJEnHXJpsZwOizjpcyNTVXnuKnoCi/lw+lUzEsxcbocE6nbnJbJ
        OAB/xk6d+FLTisrTXBmHqi72UkScU8tf+ofTLRmlnTjdiomd5ukvjMPidrbEzTd0uXTjl9bh9CrG0q6c9pIpmtvTXBmHYminJzIRB1sNU3k414pdeojT
        7Bipy5yOyThaczSXJuIrKo6pOJxtySsFb2wam6VmWs3TWhmHhCzdpZE4x4qYusNpdYylbjj9koGq19NeGIe+rOmJTMRRlsfUHE61ZZfy4TSVTKVIOL2K
        eZS3savtvA/gDm9MxeG0S8apH05zYiCOshqm43BVJXupIk6xZZf+4fRKxqUgON0VY6lMTm9jHGaz9Jcm4hhLeuLjcFUVWyl5Y3fdh5swVYfTbBmlYjjN
        konVqNMwmIdtpukumMLf69MTGYdrKppLE3HWFI6pOJx/yS4GQJxTy15qh9MmGSqNb+xoa+DtVHmY6sNpdYylZjj9JFOlzDitinE62tNemYi4LsfUHE6Z
        ZbmUEKddMqUcOP2KeSmGTndjInWb0zIZh+Av2AkT32q6UVmaS+Nw1SVeSojTaplLf3D6JeO0s6e7MRHTXP2lcTjcjp5YRJxzySfWDqdVMZd24bSWTNLc
        nObLOBFDjS1v53sAbSupo5yGxTy9a+kuDMRldsfUHE65ZEutcZoVE9QVTsdkHIK2tJdM4TW3a2kcTrJklxLidCtG6h6nYTEOutXik3EaJQNUr6e9MQ59
        WdMTmYijLI6pOJxryy7lwmkqmUuRcHoV8ylvTndjIjqjaZmMwz+W5lJAnGXFTB3hNErGUqvGtqr5vOVsA6WfOP2ScSmITnfFGCpT01sYh9ns/aWJOMaS
        njg4nFXFXkqI0yYZTLXhNFvGqRhOc2QiNao0TObhmym7St4b3Oav1C59w+mVjKU1OM2KiVRrTsdkHIK/YxfRetxEUwVeaofTJhkujcTpVYypPpyGxzhq
        VtNfMoXKTNNaGIejrW2l540tZt04qbGcjss4KTNOc2QgObr6S/NwDBXuUkScNsdl6g6nXzITwL6xdCey1Jei4TRLxpSqp70yEf7U9JfG4WdLV2l5Y1FZ
        ATcTXf2lcTjcjp5YRJxzySfWDqdVMZd24bSWTNLcnObLOBFDOD2SiTrYaZjMw7tW7tJAnGbHTF3hdEvG1JqxqS2+r8sZHcfUHE62ZJeC4bS3TGk1Tmtk
        HBKydJdG4h4rYeoOp9Uxl7ri9EsGqV5Oe2Ic+rKnJzIRR1kdU3E415ZUyt7YsfZKzVKknN7FPMpb012YiM7oWqbjcI8ludQQp1kxU0c4DZJx1KqnvTIR
        /9j6S+NwECXuUjGcpoq3VA6n2TFKsxv7jNbgbWMzLx3D6VWMpVQ47ZKBVGtOw2UcirE0lybio0rDVB5OmWK7tAqnr2NPfBxOqWQurcRpU0ymWnA6JeMQ
        /Bu7itbgJpsq9lI4nDTJd2kkTqliTPXhNDrGUrOc/pIpUmac1sU4HW1pL0zEXJdjag6nzDJcyojTLhlSjpz+xTwUQ7Gr7b23ytyAqduclsk4AH/GTp34
        UtOKytNcGYeqLvZSRJxTy1/6h9MtGaWdON2KiZ3m6S+Mw+J29MQm4p5LPbFxOKuKnLTYN/XemjrclubSOJxDyz0RiTjYkpg6w2lWzNK7nO7HQF1mdEzG
        4ZpLudQRp1ExTF3hdEvGKWhOe8sUVnO0lsbhIUtXKXljw1tlkUzd4TQ6xlK3nP7JQNVraS+Mw19W9EQm4ihLY+oOp9oyS/lwmkqmUqSc3sU8ylvTXZiI
        zuhapuNwjyWp1L2xrC7ATVFWw9QcTqpkL7XEabZM0j+c/sk4FESnu2IMlanpLYzDbHb+0kScY8lPHBxOqmIvpcRpkwymWnAaLeNUjLGp7L23qN2BqlGn
        YTEP2kzTXTGFvtenJzIO1lQ0lybirSkdU3E4/pJdCOuNE0UVcKlNTntkHDSap7sxEvpUGqbjcFkdX6l8Y/vcg5trKRxOtmKXjuK0SyY1rtMxGYdlZmku
        DMRHl38pHk6hYl2KiNPmmEzd4bRLxhL44XRWTFJUnObJOFJ1tpWtNzW4quUvfcPplozSzpzuxUROc/SXxuFxO3piE3HPJZ5YOJxVxV7ahdNbMktzcJot
        40QM4vRLJupgp2ExD+9au0rOGznaCri6zOmYjMM1l3OpIk6iYpi6w+mWjFLQnPaWKa3maS2Mw0KW7lJInGPFTN3hNDrGUrec/slA1WtpL4zDX1a2RL03
        WRRGwDrM6ZiMw7625lIpnKmStxQPp1Yxl/LidDomqTOclsk49WNpLgzEUVbD1BxOqmQvtcRptkzSP5z+yTgURLGrrr1NYJmK3lI4nGbHL83E6ZZMxDHh
        9CrGUqqc9slAqjWnYTIOxdiaSxNxUSVhqg+nTDFd2oXT1zEnvg6nVDKV1jc2WqXg7TUzY6oOp18yC8G9saJoAm61qWkvjMNGk3SXRuKfKsNUHE6rYy81
        wukmmS1lw2lWjNLRnPbJRMZ1OqbjcMwswaTGxqrz3NR0BZdy4fQq5qUYON2OidRtTstkHBK/2DgT32q6UVmaS+Nw1SVeSojTaplLf3D6JeO0s6e7MRHT
        XP2lcTjcjpZY+cYut26c0pqc/sU42lXTXjKFc1uaS+NwDC30RCbiYEth6g+nWjFL73G6HQN1mdMxGYdrLuZSRJxFxTF1h9MtGaWgjW1j89RMq7laS+Nw
        kCW7lBKnWDFT9zgNjnHUrae/MhD1WtpL43CXFT3RiTjKkpg6w+m2jFK+nOaSKRSpp7cxD/JWdpWdNwG84e2p2JyWyTj1Y2kuDMRRVsPUHE6qZC+1xGm2
        TNI/nP7JOBREp7tiDJWp6S2Mw2x2/tJEnGPJTxwcTqpiK6VvbK47cPOm6nAaLeNUjKe5MhEaVRqm83DNFN2lUzh7HXri43BNJXNpIk6aYjHVh9MvGcXg
        iNNqGUttcNok46XRjV1t7bwdKg9TnTgNjnHUrKe/ZAqUmae1MQ5HW9pLE3HXJZgaw+mzjJcy4jRLBpSjp78xD8VQ6S5MxG5zWqbjcPwlOwHiG013Kktz
        KRxOumRLqXHaLRPpT05/ZBx2tnSXJuKaK790DqfbMROLiNMumcR64fQqxtKunPaSKZrb01wZh2JosSXtvA/gbWV1tNMwmId3rd2lgTjMjpi6w+mXjKk1
        TnNiIrqi6ZiMw9CW9lIpnOaWLa3DaZaMUkKc7sVI3WM0TMbhtzq+UvXG+rYCbuq1tJfG4S8reqITcZQlMXWH020ZpXw4zSVTKVJOb2Ie5a3pLkzEZ3Qt
        U3E4x5Jc6ojTrBipo5yGyThqVdhWNd+3XG2A9JOnvzIOBVHpLhjDZap6S+Nwmx2/NBGnWDITx4fTqhhLqXHaJAOp1pyGyzgVY2kuTMRGlYapPJwzxTRp
        sXPuvc1t90ru0jicU8lcWojTpphMteF0S8YR+DtH1hsmiirgUpuc9sk4aTROd2Ik9ak0TMbhszr+UimcZpLWUjicbcUrHW9sMTvGaY3tdEzG4ZlZmksD
        cdElX8qH06iYl2LidDkmU7c4LZJxAP6NnTvxpaYUlae5Mg5UXe2liTinlr/0DqdbMko7G7vKyriJ6AovncPpdozE4uL0Syaxnjg9inG0q6e9ZArmtjSX
        xuEYWumJTMTBlsPUHk61Ypfe4nQ7Buoyp2MyDtZcTaXxjV1u6HimznA6JeMUtKe9ZQqruVpL43CQJbuUEqdYMVP3OA2OcdStp78yEPVa2kvjcJcVPdGJ
        OMqSmDrD6baMUr7GprX3ao4iVfSW5uHeKu5SRJxGxzJ1h9MsGaV+OM2KgTrKaZiMw1aV7aWJOMeWX/qH0ygZl4LhdFWMpTI4vY5xms3YV7Teb2ObAekY
        Tn9iHKWqaS8MxFqTGqbjcIwtuVQRp1UyphpwGhTzpc043R1T4ntweiXjaU1Oc2Ii1ZrTMRmH4C/YhbTeN1FUAZfa4bRJxkujcboVI6pPp2ExDprV9JdM
        4TKTtJbG4Wgre+kiTrpkU+M4HZZxUmac5slAcnT0l+bhGCpdpe+NVW4GvN3mtEzG4fhLdgLEN5ruVJbmUjicdcmXUuK0WibSn5z+yTjtbOkuTMQ0V3/p
        HE63YycWEadcMon1w+lVjKVdjW1T76mpzW1pLozDMbTTE5mIgy2GqTyca8UuvcTpdgzUZU7HZBytuZpLE3EVFcfUHE62ZJeC4bS3TGk1TmtkHBKydpWV
        Nzm8XRbD1BxOq2MvdcTplgxSvZz2xTj0ZU5PZCKOsjqm43CvLbmUCqeqZC1Fw+lVzKW8ON2OieqMp2UyDvxY9ApbhnpjWF2Am6Oshqk4nFXJXmqI02yZ
        pX84/ZJxKYhOd8UYKlPTWxiH2ez9pYk4xpKeODicVcVeSojTJhlMteE0W8apGGNT2XtvUboDVKNOw2IetJmnu2MKfK9OT2QcralpLkzEWlM6puNw/CW7
        ENYbJooq4FKbnPbJOGk0TndiJPWpNEzG4bM6vlL5xve5BjfWUjicbcUvHcRpl0xqXKdjMg7KzNNcGYiOLv5SPJxDxbsUEafNMZm6w2mXjCTwwumsmKWo
        OM2ScaXqbCtbb2twVMtf+ofTLRmlnTjdiomd5ukvjMPidvTEJuKeSz2xcTirir20Cqe2ZJfm4TRaxokYxOmWTNTBTsNiHt61dpWdN3O0FXB1mdMxGYdr
        LuZSRJxFxTF1h9MtGaWgOO0tU1rN01oZh4Qs3aWROMeKmLrDaXWMpW44/ZKBqtfTXhiHvqxsiXpvsyiNgHWY0zEZh3xtzaVTOFIlbykeTq1iLuXE6XRM
        U2c4LZJx6sfTXBiIo6yGqTicVcleaojTbJmlfzj9knEpiGNXXHubwDIVvaVxOM2OX5qI0yyZiWPD6VWMpVQ47ZKBVGtOw2UcirE0lybio0rDVB5OmWK7
        tAqnr2NPfBxOqWQqrW9stUrA22tmhVQFgIDcHwAAAAgAAAB3AAAANzEgc2VjdGlvbiBibG9jayBjYWJpbmV0CjAxMDcyIGNhYmluZXQgY2FiaW5ldCBi
        bG9jawowMTA3MyBmb2xkZXIgd2luZG93IHdpbmRvdwowMTA3NCBkZWZsYXRlIG5vdGVib29rIGZvbGRlcgowMTA3NSB3aW5kb3cgZm9sZGVyIG5vdGVi
        b29rCjAxMDc2IGZyYW1lIGZyYW1lIGZyYW1lCjAxMDc3IGJsb2NrIHNlY3Rpb24gZGVmbGF0ZQowMTA3OCBub3RlYm9vayBkZWZsYXRlIGNhYmluZXQK
        MDEwNzkgc2VjdGlvbiBibG9jayBibG9jawowMTA4MCBjYWJpbmV0IGNhYmluZXQgd2luZG93CjAxMDgxIGZvbGRlciB3aW5kb3cgZm9sZGVyCjAxMDgy
        IGRlZmxhdGUgbm90ZWJvb2sgbm90ZWJvb2sKMDEwODMgd2luZG93IGZvbGRlciBmcmFtZQowMTA4NCBmcmFtZSBmcmFtZSBkZWZsYXRlCjAxMDg1IGJs
        b2NrIHNlY3Rpb24gY2FiaW5ldAowMTA4NiBub3RlYm9vayBkZWZsYXRlIGJsb2NrCjAxMDg3IHNlY3Rpb24gYmxvY2sgd2luZG93CjAxMDg4IGNhYmlu
        ZXQgY2FiaW5ldCBmb2xkZXIKMDEwODkgZm9sZGVyIHdpbmRvdyBub3RlYm9vawowMTA5MCBkZWZsYXRlIG5vdGVib29rIGZyYW1lCjAxMDkxIHdpbmRv
        dyBmb2xkZXIgZGVmbGF0ZQowMTA5MiBmcmFtZSBmcmFtZSBjYWJpbmV0CjAxMDkzIGJsb2NrIHNlY3Rpb24gYmxvY2sKMDEwOTQgbm90ZWJvb2sgZGVm
        bGF0ZSB3aW5kb3cKMDEwOTUgc2VjdGlvbiBibG9jayBmb2xkZXIKMDEwOTYgY2FiaW5ldCBjYWJpbmV0IG5vdGVib29rCjAxMDk3IGZvbGRlciB3aW5k
        b3cgZnJhbWUKMDEwOTggZGVmbGF0ZSBub3RlYm9vayBkZWZsYXRlCjAxMDk5IHdpbmRvdyBmb2xkZXIgY2FiaW5ldAowMTEwMCBmcmFtZSBmcmFtZSBi
        bG9jawowMTEwMSBibG9jayBzZWN0aW9uIHdpbmRvdwowMTEwMiBub3RlYm9vayBkZWZsYXRlIGZvbGRlcgowMTEwMyBzZWN0aW9uIGJsb2NrIG5vdGVi
        b29rCjAxMTA0IGNhYmluZXQgY2FiaW5ldCBmcmFtZQowMTEwNSBmb2xkZXIgd2luZG93IGRlZmxhdGUKMDExMDYgZGVmbGF0ZSBub3RlYm9vayBjYWJp
        bmV0CjAxMTA3IHdpbmRvdyBmb2xkZXIgYmxvY2sKMDExMDggZnJhbWUgZnJhbWUgd2luZG93CjAxMTA5IGJsb2NrIHNlY3Rpb24gZm9sZGVyCjAxMTEw
        IG5vdGVib29rIGRlZmxhdGUgbm90ZWJvb2sKMDExMTEgc2VjdGlvbiBibG9jayBmcmFtZQowMTExMiBjYWJpbmV0IGNhYmluZXQgZGVmbGF0ZQowMTEx
        MyBmb2xkZXIgd2luZG93IGNhYmluZXQKMDExMTQgZGVmbGF0ZSBub3RlYm9vayBibG9jawowMTExNSB3aW5kb3cgZm9sZGVyIHdpbmRvdwowMTExNiBm
        cmFtZSBmcmFtZSBmb2xkZXIKMDExMTcgYmxvY2sgc2VjdGlvbiBub3RlYm9vawowMTExOCBub3RlYm9vayBkZWZsYXRlIGZyYW1lCjAxMTE5IHNlY3Rp
        b24gYmxvY2sgZGVmbGF0ZQowMTEyMCBjYWJpbmV0IGNhYmluZXQgY2FiaW5ldAowMTEyMSBmb2xkZXIgd2luZG93IGJsb2NrCjAxMTIyIGRlZmxhdGUg
        bm90ZWJvb2sgd2luZG93CjAxMTIzIHdpbmRvdyBmb2xkZXIgZm9sZGVyCjAxMTI0IGZyYW1lIGZyYW1lIG5vdGVib29rCjAxMTI1IGJsb2NrIHNlY3Rp
        b24gZnJhbWUKMDExMjYgbm90ZWJvb2sgZGVmbGF0ZSBkZWZsYXRlCjAxMTI3IHNlY3Rpb24gYmxvY2sgY2FiaW5ldAowMTEyOCBjYWJpbmV0IGNhYmlu
        ZXQgYmxvY2sKMDExMjkgZm9sZGVyIHdpbmRvdyB3aW5kb3cKMDExMzAgZGVmbGF0ZSBub3RlYm9vayBmb2xkZXIKMDExMzEgd2luZG93IGZvbGRlciBu
        b3RlYm9vawowMTEzMiBmcmFtZSBmcmFtZSBmcmFtZQowMTEzMyBibG9jayBzZWN0aW9uIGRlZmxhdGUKMDExMzQgbm90ZWJvb2sgZGVmbGF0ZSBjYWJp
        bmV0CjAxMTM1IHNlY3Rpb24gYmxvY2sgYmxvY2sKMDExMzYgY2FiaW5ldCBjYWJpbmV0IHdpbmRvdwowMTEzNyBmb2xkZXIgd2luZG93IGZvbGRlcgow
        MTEzOCBkZWZsYXRlIG5vdGVib29rIG5vdGVib29rCjAxMTM5IHdpbmRvdyBmb2xkZXIgZnJhbWUKMDExNDAgZnJhbWUgZnJhbWUgZGVmbGF0ZQowMTE0
        MSBibG9jayBzZWN0aW9uIGNhYmluZXQKMDExNDIgbm90ZWJvb2sgZGVmbGF0ZSBibG9jawowMTE0MyBzZWN0aW9uIGJsb2NrIHdpbmRvdwowMTE0NCBj
        YWJpbmV0IGNhYmluZXQgZm9sZGVyCjAxMTQ1IGZvbGRlciB3aW5kb3cgbm90ZWJvb2sKMDExNDYgZGVmbGF0ZSBub3RlYm9vayBmcmFtZQowMTE0NyB3
        aW5kb3cgZm9sZGVyIGRlZmxhdGUKMDExNDggZnJhbWUgZnJhbWUgY2FiaW5ldAowMTE0OSBibG9jayBzZWN0aW9uIGJsb2NrCjAxMTUwIG5vdGVib29r
        IGRlZmxhdGUgd2luZG93CjAxMTUxIHNlY3Rpb24gYmxvY2sgZm9sZGVyCjAxMTUyIGNhYmluZXQgY2FiaW5ldCBub3RlYm9vawowMTE1MyBmb2xkZXIg
        d2luZG93IGZyYW1lCjAxMTU0IGRlZmxhdGUgbm90ZWJvb2sgZGVmbGF0ZQowMTE1NSB3aW5kb3cgZm9sZGVyIGNhYmluZXQKMDExNTYgZnJhbWUgZnJh
        bWUgYmxvY2sKMDExNTcgYmxvY2sgc2VjdGlvbiB3aW5kb3cKMDExNTggbm90ZWJvb2sgZGVmbGF0ZSBmb2xkZXIKMDExNTkgc2VjdGlvbiBibG9jayBu
        b3RlYm9vawowMTE2MCBjYWJpbmV0IGNhYmluZXQgZnJhbWUKMDExNjEgZm9sZGVyIHdpbmRvdyBkZWZsYXRlCjAxMTYyIGRlZmxhdGUgbm90ZWJvb2sg
        Y2FiaW5ldAowMTE2MyB3aW5kb3cgZm9sZGVyIGJsb2NrCjAxMTY0IGZyYW1lIGZyYW1lIHdpbmRvdwowMTE2NSBibG9jayBzZWN0aW9uIGZvbGRlcgow
        MTE2NiBub3RlYm9vayBkZWZsYXRlIG5vdGVib29rCjAxMTY3IHNlY3Rpb24gYmxvY2sgZnJhbWUKMDExNjggY2FiaW5ldCBjYWJpbmV0IGRlZmxhdGUK
        MDExNjkgZm9sZGVyIHdpbmRvdyBjYWIAAAAAIwm5CFVVaW5ldAowMTE3MCBkZWZsYXRlIG5vdGVib29rIGJsb2NrCjAxMTcxIHdpbmRvdyBmb2xkZXIg
        d2luZG93CjAxMTcyIGZyYW1lIGZyYW1lIGZvbGRlcgowMTE3MyBibG9jayBzZWN0aW9uIG5vdGVib29rCjAxMTc0IG5vdGVib29rIGRlZmxhdGUgZnJh
        bWUKMDExNzUgc2VjdGlvbiBibG9jayBkZWZsYXRlCjAxMTc2IGNhYmluZXQgY2FiaW5ldCBjYWJpbmV0CjAxMTc3IGZvbGRlciB3aW5kb3cgYmxvY2sK
        MDExNzgAACDofIiIiIiKiKqqoKoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAIiIiIiKiKqqoKoAAAAAAAAAAAAAAAAAAAAA
        AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAIiIiIiKiKqqoKoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEARWYOvI3qDVN5OGaKrtL3
        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
        AAAAAGBKAAAAIEIAAABiBAAAGAAAAMQ=
        """;
}

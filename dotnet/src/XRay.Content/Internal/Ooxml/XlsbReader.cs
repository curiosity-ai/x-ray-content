using System.Globalization;
using System.Text;
using XRay.Content.Internal.Biff;
using XRay.Content.Internal.Excel;

namespace XRay.Content.Internal.Ooxml;

/// <summary>
/// XLSB (BIFF12 binary spreadsheet) reader — ports calamine's <c>Xlsb</c> path used by
/// <c>extraction/excel.rs</c>. An .xlsb is an OOXML ZIP whose parts are binary record streams
/// (MS-XLSB): variable-length record ids + 7-bit-varint lengths. Produces the same
/// <see cref="ExcelWorkbook"/> the XLSX reader does (dense used-range grid, calamine cell
/// formatting, markdown), so the extractor path downstream is shared.
/// </summary>
public static class XlsbReader
{
    // BIFF12 record ids (MS-XLSB section 2.3).
    private const int BrtRowHdr = 0x0000;
    private const int BrtCellBlank = 0x0001;
    private const int BrtCellRk = 0x0002;
    private const int BrtCellError = 0x0003;
    private const int BrtCellBool = 0x0004;
    private const int BrtCellReal = 0x0005;
    private const int BrtCellSt = 0x0006;
    private const int BrtCellIsst = 0x0007;
    private const int BrtFmlaString = 0x0008;
    private const int BrtFmlaNum = 0x0009;
    private const int BrtFmlaBool = 0x000A;
    private const int BrtFmlaError = 0x000B;
    private const int BrtSstItem = 0x0013;
    private const int BrtBundleSh = 0x009C;
    private const int BrtFmt = 0x002C;
    private const int BrtXF = 0x002F;
    private const int BrtBeginCellXFs = 0x0269;
    private const int BrtEndCellXFs = 0x026A;

    /// <summary>Reads a workbook's sheets and metadata.</summary>
    /// <param name="content">The OOXML package.</param>
    /// <param name="numberFormats">
    /// Render numeric cells through <c>styles.bin</c>, so a cell reads the way Excel shows it.
    /// False keeps the bare value calamine hands the Rust original — see "Deviation: formatted
    /// Excel cells" in <c>CLAUDE.md</c>.
    /// </param>
    public static ExcelWorkbook Read(ReadOnlySpan<byte> content, bool numberFormats = true)
    {
        using var pkg = new OoxmlPackage(content);
        var wb = new ExcelWorkbook();

        var shared = ReadSharedStrings(pkg);
        var sheetRefs = ReadSheetOrder(pkg);
        var formats = numberFormats ? ReadCellFormats(pkg) : ExcelCellFormats.Empty;

        foreach (var (name, target) in sheetRefs)
            wb.Sheets.Add(ProcessSheet(pkg, name, target, shared, formats));

        // Mirrors read_excel_bytes ".xlsb": no office metadata, just sheet_count/sheet_names.
        var names = sheetRefs.Select(s => s.Name).ToList();
        wb.Metadata["sheet_count"] = names.Count.ToString(CultureInfo.InvariantCulture);
        wb.Metadata["sheet_names"] = names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(5)) + $", ... ({names.Count} total)";
        return wb;
    }

    // ── workbook.bin: ordered (sheet name → worksheet part path) ────────────────
    private static List<(string Name, string Target)> ReadSheetOrder(OoxmlPackage pkg)
    {
        var result = new List<(string, string)>();
        var bin = pkg.ReadBytes("xl/workbook.bin");
        if (bin is null) return result;

        // rId → target (relative to xl/), from the sibling XML rels part.
        var rels = new Dictionary<string, string>(StringComparer.Ordinal);
        var relsXml = pkg.ReadXml("xl/_rels/workbook.bin.rels");
        if (relsXml?.Root is not null)
            foreach (var r in relsXml.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                var id = r.Attribute("Id")?.Value;
                var tgt = r.Attribute("Target")?.Value;
                if (id is not null && tgt is not null) rels[id] = tgt;
            }

        foreach (var (id, data) in Records(bin))
        {
            if (id != BrtBundleSh) continue;
            // u32 hsState, u32 iTabID, XLNullableWideString strRelID, XLWideString strName.
            int pos = 8;
            string? relId = ReadNullableString(data, ref pos);
            string name = ReadString(data, ref pos) ?? "";
            string target = "";
            if (relId is not null && rels.TryGetValue(relId, out var t))
                target = t.StartsWith('/') ? t[1..] : (t.StartsWith("xl/", StringComparison.Ordinal) ? t : "xl/" + t);
            result.Add((name, target));
        }
        return result;
    }

    // ── sharedStrings.bin ────────────────────────────────────────────────────────
    private static List<string> ReadSharedStrings(OoxmlPackage pkg)
    {
        var list = new List<string>();
        var bin = pkg.ReadBytes("xl/sharedStrings.bin");
        if (bin is null) return list;
        foreach (var (id, data) in Records(bin))
        {
            if (id != BrtSstItem) continue;
            // RichStr: flags byte, then XLWideString (formatting runs after are ignored).
            int pos = 1;
            list.Add(ReadString(data, ref pos) ?? "");
        }
        return list;
    }

    // ── worksheet part → ExcelSheet ─────────────────────────────────────────────
    private static ExcelSheet ProcessSheet(
        OoxmlPackage pkg, string name, string target, List<string> shared, ExcelCellFormats formats)
    {
        var cellsByPos = new Dictionary<(int Row, int Col), string>();
        int rowMin = int.MaxValue, rowMax = -1, colMin = int.MaxValue, colMax = -1;

        var bin = target.Length > 0 ? pkg.ReadBytes(target) : null;
        if (bin is not null)
        {
            int row = 0;
            foreach (var (id, data) in Records(bin))
            {
                if (id == BrtRowHdr)
                {
                    if (data.Length >= 4) row = (int)U32(data, 0);
                    continue;
                }
                if (id < BrtCellBlank || id > BrtFmlaError || data.Length < 8) continue;

                int col = (int)U32(data, 0);
                // Cell [MS-XLSB 2.5.9]: the column, then a 24-bit index into cellXfs.
                int style = data[4] | (data[5] << 8) | (data[6] << 16);
                string? value = id switch
                {
                    BrtCellRk when data.Length >= 12 => Render(formats, style, BiffReader.RkToDouble(U32(data, 8))),
                    BrtCellError or BrtFmlaError when data.Length >= 9 => $"#ERR: {data[8]}",
                    BrtCellBool or BrtFmlaBool when data.Length >= 9 => data[8] != 0 ? "true" : "false",
                    BrtCellReal or BrtFmlaNum when data.Length >= 16 =>
                        Render(formats, style, BitConverter.Int64BitsToDouble((long)U64(data, 8))),
                    BrtCellSt or BrtFmlaString => ReadStringAt(data, 8),
                    BrtCellIsst when data.Length >= 12 && U32(data, 8) < (uint)shared.Count => shared[(int)U32(data, 8)],
                    _ => null,
                };
                if (string.IsNullOrEmpty(value)) continue;

                cellsByPos[(row, col)] = value;
                if (row < rowMin) rowMin = row;
                if (row > rowMax) rowMax = row;
                if (col < colMin) colMin = col;
                if (col > colMax) colMax = col;
            }
        }

        if (rowMax < 0)
            return new ExcelSheet { Name = name, Markdown = $"## {name}\n\n*Empty sheet*", TableCells = null };

        int width = colMax - colMin + 1;
        var grid = new List<List<string>>(rowMax - rowMin + 1);
        for (int r = rowMin; r <= rowMax; r++)
        {
            var rowCells = new List<string>(width);
            for (int c = colMin; c <= colMax; c++)
                rowCells.Add(cellsByPos.TryGetValue((r, c), out var v) ? v : "");
            grid.Add(rowCells);
        }
        return new ExcelSheet { Name = name, Markdown = XlsxReader.GenerateMarkdown(name, grid), TableCells = grid };
    }

    /// <summary>
    /// A numeric cell's text: what its format says, and the bare value where the format says
    /// nothing this renders.
    /// </summary>
    private static string Render(ExcelCellFormats formats, int style, double value) =>
        formats.Render(style, value) ?? BiffReader.FormatNumber(value);

    // ── styles.bin: BrtFmt + the cellXfs block ──────────────────────────────────
    /// <summary>
    /// The workbook's number formats and the cell formats that cite them.
    /// </summary>
    /// <remarks>
    /// Only the <c>cellXfs</c> block counts: a cell's style index addresses that list, and the
    /// <c>cellStyleXfs</c> block written before it holds the same record id, so taking every
    /// <c>BrtXF</c> would shift every index. The BIFF8 reader has no such split, which is why
    /// it can take them all.
    /// </remarks>
    private static ExcelCellFormats ReadCellFormats(OoxmlPackage pkg)
    {
        var formats = new ExcelCellFormats();
        var bin = pkg.ReadBytes("xl/styles.bin");
        if (bin is null) return formats;

        bool inCellXfs = false;
        foreach (var (id, data) in Records(bin))
        {
            switch (id)
            {
                case BrtBeginCellXFs: inCellXfs = true; break;
                case BrtEndCellXFs: inCellXfs = false; break;
                case BrtFmt when data.Length >= 6:
                {
                    // BrtFmt [MS-XLSB 2.4.660]: the id, then the code as an XLWideString.
                    int pos = 2;
                    if (ReadString(data, ref pos) is { } code) formats.AddCode(data[0] | (data[1] << 8), code);
                    break;
                }
                case BrtXF when inCellXfs && data.Length >= 4:
                    // BrtXF [MS-XLSB 2.4.836]: the parent style index, then the number format.
                    formats.AddCellFormat(data[2] | (data[3] << 8));
                    break;
            }
        }
        return formats;
    }

    // ── BIFF12 record framing ────────────────────────────────────────────────────
    // Record id: 1 byte, or 2 when the first byte's high bit is set (7 bits each, little-endian).
    // Record length: 1–4 bytes of 7-bit varint.
    private static IEnumerable<(int Id, byte[] Data)> Records(byte[] part)
    {
        int pos = 0;
        while (pos < part.Length)
        {
            int b0 = part[pos++];
            int id;
            if ((b0 & 0x80) != 0)
            {
                if (pos >= part.Length) yield break;
                id = (b0 & 0x7F) | ((part[pos++] & 0x7F) << 7);
            }
            else id = b0;

            int len = 0, shift = 0;
            while (true)
            {
                if (pos >= part.Length || shift > 21) yield break;
                int b = part[pos++];
                len |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            if (len < 0 || pos + len > part.Length) yield break;

            var data = new byte[len];
            Array.Copy(part, pos, data, 0, len);
            pos += len;
            yield return (id, data);
        }
    }

    // XLWideString: u32 char count + UTF-16LE payload.
    private static string? ReadString(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) return null;
        uint cch = U32(data, pos);
        pos += 4;
        if (cch > int.MaxValue / 2 || pos + (int)cch * 2 > data.Length) return null;
        string s = Encoding.Unicode.GetString(data, pos, (int)cch * 2);
        pos += (int)cch * 2;
        return s;
    }

    private static string? ReadStringAt(byte[] data, int pos) => ReadString(data, ref pos);

    // XLNullableWideString: cch of 0xFFFFFFFF means null (no payload bytes follow).
    private static string? ReadNullableString(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) return null;
        if (U32(data, pos) == 0xFFFFFFFF) { pos += 4; return null; }
        return ReadString(data, ref pos);
    }

    private static uint U32(byte[] b, int i) =>
        (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    private static ulong U64(byte[] b, int i) => U32(b, i) | ((ulong)U32(b, i + 4) << 32);
}

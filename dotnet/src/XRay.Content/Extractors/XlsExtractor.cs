using System.Text;
using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Internal.Biff;
using XRay.Content.Internal.Cfb;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>
/// Legacy Excel 97-2003 binary (.xls) extractor. Ports the OLE branch of
/// <c>extraction/excel.rs</c> (calamine's <c>Xls</c> path): reads the BIFF workbook from the CFB
/// container via <see cref="BiffReader"/> and builds the same per-sheet heading + table document
/// the XLSX path produces, with matching <c>excel</c> metadata (sheet_count / sheet_names).
///
/// Advertises <c>application/vnd.ms-excel</c> at a higher priority than the ZIP-only XLSX extractor
/// so .xls files (which are not ZIP containers) route here.
/// </summary>
public sealed class XlsExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.ms-excel" };
    public int Priority => 60;

    private static readonly HashSet<string> StandardKeys = new()
    {
        "title", "subject", "created_by", "creator", "modified_by",
        "created_at", "modified_at", "keywords", "language",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var comp = CompoundFile.Open(content);
        var sheets = BiffReader.ReadSheets(comp, config.Options.ExcelNumberFormats);
        TableBudget.ChargeSheets(SecurityBudget.FromConfig(config), sheets.Select(sh => sh.Cells));

        var doc = BuildInternalDocument(sheets);

        var sheetNames = sheets.Select(s => s.Name).ToList();
        var excelMeta = new ExcelMetadata
        {
            SheetCount = (uint)sheets.Count,
            SheetNames = sheetNames,
        };

        // Rust extract_metadata puts sheet_count + a (possibly truncated) sheet_names string into
        // the workbook metadata map, which the extractor surfaces in `additional`.
        var additional = new Dictionary<string, JsonElement>
        {
            ["sheet_count"] = JsonStr(sheets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["sheet_names"] = JsonStr(FormatSheetNames(sheetNames)),
        };

        foreach (var sheet in sheets)
        {
            if (BiffFormula.CollectSheetFormulas(sheet.Formulas) is { } formulas)
                additional[$"formulas_{sheet.Name}"] = JsonStr(formulas);
        }

        doc.Metadata = new Metadata
        {
            Format = FormatMetadata.Excel(excelMeta),
            Additional = additional,
        };
        doc.MimeType = mimeType;
        return doc;
    }

    private static string FormatSheetNames(List<string> names)
    {
        if (names.Count <= 5) return string.Join(", ", names);
        var sb = new StringBuilder(100);
        for (int i = 0; i < 5; i++) { if (i > 0) sb.Append(", "); sb.Append(names[i]); }
        sb.Append($", ... ({names.Count} total)");
        return sb.ToString();
    }

    private static InternalDocument BuildInternalDocument(List<BiffReader.Sheet> sheets)
    {
        var builder = new InternalDocumentBuilder("excel");
        var pages = new List<PageContent>(sheets.Count);

        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            uint pageNumber = (uint)(i + 1);
            string? nameOpt = sheet.Name.Length == 0 ? null : sheet.Name;

            if (sheet.Cells is { Count: > 0 } cells)
            {
                if (sheet.Name.Length > 0) builder.PushHeading(2, sheet.Name, null, null);
                builder.PushTableFromCells(cells, pageNumber, null);

                string markdown = InternalDocumentBuilder.CellsToMarkdown(cells);
                string pageContent = sheet.Name.Length == 0
                    ? markdown
                    : $"## {EscapeSheetNameForHeading(sheet.Name)}\n\n{markdown}";
                var table = new Table
                {
                    Cells = cells.Select(r => new List<string>(r)).ToList(),
                    Markdown = markdown,
                    PageNumber = pageNumber,
                };
                pages.Add(new PageContent
                {
                    PageNumber = pageNumber,
                    Content = pageContent,
                    Tables = new List<Table> { table },
                    IsBlank = false,
                    SheetName = nameOpt,
                });
            }
            else
            {
                string content = nameOpt is null ? "" : $"## {EscapeSheetNameForHeading(nameOpt)}\n\n";
                pages.Add(new PageContent
                {
                    PageNumber = pageNumber,
                    Content = content,
                    IsBlank = true,
                    SheetName = nameOpt,
                });
            }
        }

        var doc = builder.Build();
        doc.PrebuiltPages = pages;
        return doc;
    }

    private static readonly char[] InlineMetachars = { '\\', '`', '*', '_', '[', ']', '<', '>', '!' };

    private static string EscapeSheetNameForHeading(string name)
    {
        name = name.TrimStart();
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool needsEscape = (i == 0 && ch is '#' or '>' or '-' or '*' or '+' or '~') || Array.IndexOf(InlineMetachars, ch) >= 0;
            if (needsEscape) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static JsonElement JsonStr(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
}

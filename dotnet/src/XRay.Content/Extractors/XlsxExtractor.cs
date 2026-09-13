using System.Text.Json;
using XRay.Content.Core;
using XRay.Content.Internal.Ooxml;
using XRay.Content.Types;

namespace XRay.Content.Extractors;

/// <summary>
/// Excel spreadsheet extractor. Ports `extractors/excel.rs` + `extraction/excel.rs` for the
/// ZIP-based OOXML spreadsheet formats (xlsx/xlsm/xltm/xlam). Each non-empty sheet becomes an
/// H2 heading + one Table; one <see cref="PageContent"/> is emitted per sheet.
/// Binary formats (.xls/.xla/.xlsb) and ODS are not supported.
/// </summary>
public sealed class XlsxExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel.sheet.macroEnabled.12",
        "application/vnd.ms-excel.addin.macroEnabled.12",
        "application/vnd.ms-excel.template.macroEnabled.12",
        "application/vnd.ms-excel",
        "application/vnd.ms-excel.addin.macroEnabled",
        "application/vnd.ms-excel.sheet.binary.macroEnabled.12",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.template",
    };

    // Standard metadata keys mapped to typed Metadata fields (excluded from `additional`).
    private static readonly HashSet<string> StandardKeys = new()
    {
        "title", "subject", "created_by", "creator", "modified_by",
        "created_at", "modified_at", "keywords", "language",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        // Legacy-binary workbooks can arrive under any Excel MIME (e.g. a CFB `.xla` maps to
        // the macro-template MIME). Rust ends up in calamine's BIFF parser for such content;
        // mirror that by sniffing the CFB signature and delegating to the BIFF extractor.
        if (content.Length >= 8 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0)
            return new XlsExtractor().Extract(content, mimeType, config);

        string extension = mimeType switch
        {
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.ms-excel.sheet.macroEnabled.12" => ".xlsm",
            "application/vnd.ms-excel.addin.macroEnabled.12" => ".xlam",
            "application/vnd.ms-excel.template.macroEnabled.12" => ".xltm",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.ms-excel.addin.macroEnabled" => ".xla",
            "application/vnd.ms-excel.sheet.binary.macroEnabled.12" => ".xlsb",
            "application/vnd.oasis.opendocument.spreadsheet" => ".ods",
            _ => ".xlsx",
        };

        bool officeMetadata = extension is ".xlsx" or ".xlsm" or ".xlam" or ".xltm";
        var workbook = extension == ".xlsb"
            ? XlsbReader.Read(content, config.Options.ExcelNumberFormats)
            : XlsxReader.Read(content, officeMetadata, config.Options.ExcelNumberFormats);
        TableBudget.ChargeSheets(SecurityBudget.FromConfig(config), workbook.Sheets.Select(sh => sh.TableCells));
        var doc = WorkbookToInternalDocument(workbook);
        doc.MimeType = mimeType;
        return doc;
    }

    private static InternalDocument WorkbookToInternalDocument(ExcelWorkbook workbook)
    {
        var doc = BuildInternalDocument(workbook);

        var excelMeta = new ExcelMetadata
        {
            SheetCount = (uint)workbook.Sheets.Count,
            SheetNames = workbook.Sheets.Select(s => s.Name).ToList(),
        };

        var wbMeta = workbook.Metadata;
        string? Get(string k) => wbMeta.TryGetValue(k, out var v) ? v : null;

        string? createdBy = Get("created_by") ?? Get("creator");
        string? title = Get("title");
        string? subject = Get("subject");
        string? modifiedBy = Get("modified_by");
        string? createdAt = Get("created_at");
        string? modifiedAt = Get("modified_at");
        string? language = Get("language");
        List<string>? authors = createdBy is null ? null : new List<string> { createdBy };
        List<string>? keywords = Get("keywords") is { } kw
            ? kw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : null;

        var additional = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in wbMeta)
            if (!StandardKeys.Contains(k))
                additional[k] = JsonString(v);

        doc.Metadata = new Metadata
        {
            Title = title,
            Subject = subject,
            Authors = authors,
            Keywords = keywords,
            Language = language,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt,
            CreatedBy = createdBy,
            ModifiedBy = modifiedBy,
            Format = FormatMetadata.Excel(excelMeta),
            Additional = additional,
        };
        return doc;
    }

    /// <summary>
    /// The sheets the workbook hides.
    /// </summary>
    /// <remarks>
    /// A sheet's own content carries no visibility flag; only the workbook's sheet list says
    /// which are hidden, and the reader records that as metadata.
    /// </remarks>
    private static HashSet<string> HiddenSheetNames(ExcelWorkbook workbook) =>
        workbook.Metadata.TryGetValue("hidden_sheets", out string? names)
            ? new HashSet<string>(names.Split(", "), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The suffix a hidden sheet's heading carries, so hidden content stays distinguishable from
    /// visible content in the extracted text.
    /// </summary>
    private static string HiddenSuffix(HashSet<string> hiddenSheets, string sheetName) =>
        hiddenSheets.Contains(sheetName) ? " (hidden)" : "";

    private static InternalDocument BuildInternalDocument(ExcelWorkbook workbook)
    {
        var builder = new InternalDocumentBuilder("excel");
        var pages = new List<PageContent>(workbook.Sheets.Count);
        var hiddenSheets = HiddenSheetNames(workbook);

        for (int i = 0; i < workbook.Sheets.Count; i++)
        {
            var sheet = workbook.Sheets[i];
            uint pageNumber = (uint)(i + 1);
            string? nameOpt = sheet.Name.Length == 0 ? null : sheet.Name;

            if (sheet.TableCells is { Count: > 0 } cells)
            {
                if (sheet.Name.Length > 0)
                    builder.PushHeading(2, sheet.Name + HiddenSuffix(hiddenSheets, sheet.Name), null, null);
                builder.PushTableFromCells(cells, pageNumber, null);

                string pageContent = sheet.Name.Length == 0
                    ? sheet.Markdown
                    : $"## {EscapeSheetNameForHeading(sheet.Name)}\n\n{sheet.Markdown}";

                var table = new Table
                {
                    Cells = cells.Select(r => new List<string>(r)).ToList(),
                    Markdown = sheet.Markdown,
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
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool needsEscape = (i == 0 && ch is '#' or '>' or '-' or '*' or '+' or '~') || Array.IndexOf(InlineMetachars, ch) >= 0;
            if (needsEscape) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static JsonElement JsonString(string s) =>
        JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
}

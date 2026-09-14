namespace XRay.Content.Internal.Excel;

/// <summary>
/// A workbook's number formats and the cell formats that point at them: the two-level lookup
/// every Excel container uses to say how a cell's value is displayed.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the same in each container, only the spelling differs — BIFF writes FORMAT and
/// XF records, OOXML writes <c>numFmts</c> and <c>cellXfs</c> in <c>styles.xml</c> — so the
/// lookup lives here once and each reader fills it.
/// </para>
/// <para>
/// <see cref="Empty"/> is the "no formats known" instance, which renders nothing: it is what a
/// reader passes when the caller has turned formatting off, so the switch costs one comparison
/// rather than a branch at every cell.
/// </para>
/// </remarks>
internal sealed class ExcelCellFormats
{
    /// <summary>A workbook whose cells are all rendered as bare numbers.</summary>
    public static ExcelCellFormats Empty { get; } = new();

    private readonly Dictionary<int, string> _codes = new();
    private readonly List<int> _cellFormats = new();

    /// <summary>Whether any cell format could apply. False for <see cref="Empty"/>.</summary>
    public bool Any => _cellFormats.Count > 0;

    /// <summary>Records a number format code against the id cell formats refer to it by.</summary>
    public void AddCode(int numberFormatId, string code)
    {
        if (code.Length > 0) _codes[numberFormatId] = code;
    }

    /// <summary>
    /// Records the next cell format's number format id. Cell formats are addressed by position,
    /// so they must arrive in the order the container writes them.
    /// </summary>
    public void AddCellFormat(int numberFormatId) => _cellFormats.Add(numberFormatId);

    /// <summary>
    /// The format code a cell format points at: the workbook's own definition where it has one,
    /// and otherwise the code the id implies.
    /// </summary>
    public string? CodeFor(int cellFormatIndex)
    {
        if (cellFormatIndex < 0 || cellFormatIndex >= _cellFormats.Count) return null;
        int id = _cellFormats[cellFormatIndex];
        return _codes.TryGetValue(id, out var code) ? code : ExcelNumberFormat.BuiltIn(id);
    }

    /// <summary>
    /// The cell's text under its format, or <c>null</c> where the format says nothing and the
    /// reader's own rendering stands.
    /// </summary>
    public string? Render(int cellFormatIndex, double value) =>
        _cellFormats.Count == 0 ? null : ExcelNumberFormat.Apply(CodeFor(cellFormatIndex), value);
}

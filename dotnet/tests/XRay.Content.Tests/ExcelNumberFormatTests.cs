using XRay.Content.Internal.Excel;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// The Excel number-format renderer: what a cell's format code makes of its value.
/// </summary>
/// <remarks>
/// Expectations are what Excel itself displays, minus the alignment padding the accounting
/// formats reserve — see <see cref="ExcelNumberFormat"/> for why that is dropped.
/// </remarks>
public sealed class ExcelNumberFormatTests
{
    // ── the two the corpus actually asks for ──────────────────────────────────

    /// <summary>`0.0%` is what `xls/test_excel.xls` gives its ratio cell.</summary>
    [Fact]
    public void APercentageIsScaledAndRounded() =>
        Assert.Equal("3.2%", ExcelNumberFormat.Apply("0.0%", 0.032280358222708555));

    /// <summary>
    /// The accounting format the same workbook gives its currency column. Its `_(` and `* `
    /// runs reserve width rather than carry text, so what is left is the symbol and the number.
    /// </summary>
    [Fact]
    public void AnAccountingFormatKeepsItsSymbolAndDropsItsPadding() =>
        Assert.Equal("$31,200", ExcelNumberFormat.Apply(
            "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"??_);_(@_)", 31200));

    // ── numbers ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", 42.6, "43")]
    [InlineData("0.00", 3.14159, "3.14")]
    [InlineData("#,##0", 31200, "31,200")]
    [InlineData("#,##0", 999, "999")]
    [InlineData("#,##0", 1234567, "1,234,567")]
    [InlineData("#,##0.00", 1234.5, "1,234.50")]
    [InlineData("0%", 0.5, "50%")]
    [InlineData("0.00%", 0.032280358222708555, "3.23%")]
    [InlineData("$#,##0", 31200, "$31,200")]
    [InlineData("#,##0,", 1234567, "1,235")]
    [InlineData("00000", 42, "00042")]
    public void ANumberTakesItsFormat(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    /// <summary>
    /// A `#` past the last significant digit shows nothing, a `0` shows a zero. This is what
    /// keeps `0.##` from writing a bare decimal point.
    /// </summary>
    [Theory]
    [InlineData("0.##", 3.0, "3")]
    [InlineData("0.##", 3.5, "3.5")]
    [InlineData("0.##", 3.456, "3.46")]
    [InlineData("0.00", 3.0, "3.00")]
    public void OptionalDecimalsAreDroppedAndRequiredOnesKept(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    /// <summary>
    /// A zero integer part is a leading zero, shown only where a `0` placeholder insists on it.
    /// This is what makes the accounting zero section write its dash alone rather than beside a
    /// stray digit.
    /// </summary>
    [Theory]
    [InlineData("#.##", 0.5, ".5")]
    [InlineData("0.##", 0.5, "0.5")]
    [InlineData("#,##0", 0.0, "0")]
    [InlineData("_(\"$\"* \"-\"??_)", 0.0, "$-")]
    public void AZeroIntegerPartIsShownOnlyWhereTheFormatInsists(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    /// <summary>
    /// Literals between digit placeholders keep their place, because digits are filled from the
    /// right rather than poured into one run.
    /// </summary>
    [Fact]
    public void LiteralsBetweenDigitsKeepTheirPlaces() =>
        Assert.Equal("123-45-6789", ExcelNumberFormat.Apply("000-00-0000", 123456789));

    // ── negatives and zero ────────────────────────────────────────────────────

    /// <summary>
    /// One section covers every number, so the sign is the renderer's to add; a second section
    /// is the negative one and is handed the magnitude, which is how `(1,234)` gets written.
    /// </summary>
    [Theory]
    [InlineData("#,##0", -1234.0, "-1,234")]
    [InlineData("0.0%", -0.032, "-3.2%")]
    [InlineData("#,##0;(#,##0)", -1234.0, "(1,234)")]
    [InlineData("#,##0;(#,##0)", 1234.0, "1,234")]
    [InlineData("#,##0;(#,##0);\"nil\"", 0.0, "nil")]
    [InlineData("#,##0;(#,##0)", 0.0, "0")]
    public void SectionsSelectOnSign(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    /// <summary>A colour is not text, and neither is the bracket around it.</summary>
    [Fact]
    public void AColourSectionContributesNoText() =>
        Assert.Equal("(1,234)", ExcelNumberFormat.Apply("#,##0;[Red](#,##0)", -1234.0));

    /// <summary>A `[$…]` bracket is the one that does carry text: its currency symbol.</summary>
    [Fact]
    public void ALocaleBracketContributesItsCurrencySymbol() =>
        Assert.Equal("€1,234", ExcelNumberFormat.Apply("[$€-407]#,##0", 1234.0));

    /// <summary>A conditional section picks by value, the unconditional one being the fallback.</summary>
    [Theory]
    [InlineData(1500.0, "1.5k")]
    [InlineData(150.0, "150")]
    public void AConditionSelectsItsSection(double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply("[>=1000]0.0,\"k\";0", value));

    // ── dates ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A date is a serial number until its format says otherwise, which is the whole reason a
    /// spreadsheet's dates come out as five-digit integers without this.
    /// </summary>
    [Theory]
    [InlineData("m/d/yyyy", 39448.0, "1/1/2008")]
    [InlineData("mm/dd/yy", 39448.0, "01/01/08")]
    [InlineData("d-mmm-yy", 39448.0, "1-Jan-08")]
    [InlineData("dddd", 39448.0, "Tuesday")]
    [InlineData("yyyy-mm-dd", 39448.0, "2008-01-01")]
    public void ADateSerialTakesItsFormat(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    /// <summary>
    /// `m` is minutes beside an hour or a second and months everywhere else — the one rule in
    /// the grammar that cannot be read off the character alone.
    /// </summary>
    [Theory]
    [InlineData("h:mm", 0.5208333333333334, "12:30")]
    [InlineData("h:mm AM/PM", 0.5208333333333334, "12:30 PM")]
    [InlineData("h:mm AM/PM", 0.0208333333333333, "12:30 AM")]
    [InlineData("mm:ss", 0.5208449074074074, "30:01")]
    [InlineData("m/d/yyyy h:mm", 39448.5208333333333334, "1/1/2008 12:30")]
    public void MinutesAndMonthsAreToldApartByTheirNeighbours(string code, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(code, value));

    // ── falling back ──────────────────────────────────────────────────────────

    /// <summary>
    /// A format this renderer has no shape for leaves the caller's own rendering in place, so an
    /// unusual workbook degrades to the unformatted number rather than to a wrong string.
    /// </summary>
    [Theory]
    [InlineData("General")]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("# ?/?")]
    [InlineData("0.00E+00")]
    [InlineData("[h]:mm:ss")]
    public void AnUnsupportedFormatDefersToTheCaller(string code) =>
        Assert.Null(ExcelNumberFormat.Apply(code, 1234.5));

    /// <summary>A serial no date can hold is not a date, whatever its format claims.</summary>
    [Fact]
    public void ASerialOutsideTheDateRangeDefersToTheCaller() =>
        Assert.Null(ExcelNumberFormat.Apply("m/d/yyyy", 1e12));

    // ── built-in ids ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ids below 164 have no FORMAT record: the workbook means the implied code, and a reader
    /// that does not know them sees no format at all.
    /// </summary>
    [Theory]
    [InlineData(9, 0.5, "50%")]
    [InlineData(3, 31200.0, "31,200")]
    [InlineData(14, 39448.0, "1/1/2008")]
    [InlineData(2, 3.14159, "3.14")]
    public void ABuiltInIdResolvesToItsCode(int id, double value, string expected) =>
        Assert.Equal(expected, ExcelNumberFormat.Apply(ExcelNumberFormat.BuiltIn(id), value));

    /// <summary>Id 0 is General, which is the "leave it alone" answer rather than a format.</summary>
    [Fact]
    public void TheGeneralBuiltInLeavesTheValueAlone() =>
        Assert.Null(ExcelNumberFormat.Apply(ExcelNumberFormat.BuiltIn(0), 1234.5));

    /// <summary>A locale-defined or reserved id has no implied code to fall back on.</summary>
    [Fact]
    public void AReservedBuiltInIdHasNoCode() => Assert.Null(ExcelNumberFormat.BuiltIn(30));
}

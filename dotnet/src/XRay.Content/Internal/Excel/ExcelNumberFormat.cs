using System.Globalization;
using System.Text;

namespace XRay.Content.Internal.Excel;

/// <summary>
/// Renders a numeric cell through the workbook's number format code, so an extracted cell reads
/// the way the spreadsheet shows it — <c>3.2%</c> rather than <c>0.032280358222708555</c>,
/// <c>$31,200</c> rather than <c>31200</c>, a date rather than its serial number.
/// </summary>
/// <remarks>
/// <para>
/// This is a deviation from upstream, not a port of it: calamine hands the Rust side a bare
/// <c>f64</c> and it prints that. See "Deviation: formatted Excel cells" in <c>CLAUDE.md</c> for
/// why, and for the switch that restores the upstream rendering.
/// </para>
/// <para>
/// The grammar is Excel's, in the shape ECMA-376 §18.8.31 describes it: up to four
/// semicolon-separated sections (positive, negative, zero, text), each a run of digit
/// placeholders (<c>0 # ?</c>) with literals around and between them. What is deliberately not
/// reproduced is everything that is about *display width* rather than about content: <c>_x</c>
/// reserves the width of a character and <c>* x</c> fills the cell with one, and both exist to
/// line a column up on screen. Emitting them would put the alignment padding of a spreadsheet
/// into a text extraction, so the accounting format <c>_("$"* #,##0_)</c> yields
/// <c>$31,200</c> here where Excel draws <c>$␣␣31,200␣</c>.
/// </para>
/// <para>
/// A section this parser does not understand — a fraction (<c># ?/?</c>), or anything left after
/// the known tokens that looks like a digit run it cannot place — falls back to
/// <c>General</c> rather than guessing, so an unusual format degrades to today's output instead
/// of to a wrong string.
/// </para>
/// </remarks>
internal static class ExcelNumberFormat
{
    /// <summary>
    /// The cell's text under <paramref name="formatCode"/>, or <c>null</c> when the format says
    /// nothing this can act on and the caller should keep its own rendering.
    /// </summary>
    /// <param name="formatCode">The number format code, e.g. <c>0.0%</c>.</param>
    /// <param name="value">The cell's numeric value.</param>
    public static string? Apply(string? formatCode, double value)
    {
        if (formatCode is null || formatCode.Length == 0) return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;

        var sections = SplitSections(formatCode);
        if (sections.Count == 0) return null;

        string? section = SelectSection(sections, ref value);
        if (section is null) return null;
        if (IsGeneral(section)) return null;

        var tokens = Tokenize(section);
        if (tokens is null) return null;

        return tokens.Any(t => t.Kind == TokenKind.DatePart)
            ? RenderDateTime(tokens, value)
            : RenderNumber(tokens, value);
    }

    /// <summary>Whether a format code renders its value as a date or a time.</summary>
    /// <remarks>
    /// Callers that keep dates and numbers apart — a reader deciding whether a cell is a date at
    /// all — need this without rendering anything.
    /// </remarks>
    public static bool IsDateTime(string? formatCode)
    {
        if (formatCode is null) return false;
        var sections = SplitSections(formatCode);
        if (sections.Count == 0) return false;
        var tokens = Tokenize(sections[0].Body);
        return tokens is not null && tokens.Any(t => t.Kind == TokenKind.DatePart);
    }

    // ── sections ──────────────────────────────────────────────────────────────

    private readonly record struct Section(string Body, string? Condition);

    /// <summary>
    /// Splits on the semicolons that separate sections, which are the ones outside a quoted
    /// literal, a bracketed part and a backslash escape.
    /// </summary>
    private static List<Section> SplitSections(string code)
    {
        var sections = new List<Section>(4);
        var body = new StringBuilder();
        string? condition = null;
        bool inQuote = false;

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (inQuote)
            {
                body.Append(c);
                if (c == '"') inQuote = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuote = true;
                    body.Append(c);
                    break;
                case '\\':
                    body.Append(c);
                    if (i + 1 < code.Length) body.Append(code[++i]);
                    break;
                case '[':
                {
                    int close = code.IndexOf(']', i);
                    if (close < 0) { body.Append(c); break; }
                    string inner = code.Substring(i + 1, close - i - 1);
                    // A condition steers which section applies and is not part of its body; a
                    // colour, a locale or an elapsed-time bracket is.
                    if (IsCondition(inner)) condition ??= inner;
                    else body.Append('[').Append(inner).Append(']');
                    i = close;
                    break;
                }
                case ';':
                    sections.Add(new Section(body.ToString(), condition));
                    body.Clear();
                    condition = null;
                    break;
                default:
                    body.Append(c);
                    break;
            }
        }

        sections.Add(new Section(body.ToString(), condition));
        return sections;
    }

    private static bool IsCondition(string inner) =>
        inner.Length > 1 && (inner[0] == '<' || inner[0] == '>' || inner[0] == '=');

    /// <summary>
    /// The section that applies to <paramref name="value"/>, which is also where Excel decides
    /// whether the section formats the number or its magnitude.
    /// </summary>
    /// <remarks>
    /// Without conditions the sections mean positive, negative, zero, text in that order, and a
    /// negative value handed to the negative section is handed its magnitude — the section
    /// carries its own sign, which is how <c>(1,234)</c> is written. One section alone covers
    /// every number, so there the sign is prepended instead.
    /// </remarks>
    private static string? SelectSection(List<Section> sections, ref double value)
    {
        // Conditional sections are tried in order, the last one being the unconditional fallback.
        if (sections.Any(s => s.Condition is not null))
        {
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Condition is not { } condition) return sections[i].Body;
                if (ConditionHolds(condition, value)) return sections[i].Body;
            }
            return null;
        }

        var numeric = sections.Count > 3 ? sections.Take(3).ToList() : sections;

        if (numeric.Count == 1)
        {
            // The single section formats magnitude; the sign is the renderer's to add.
            return numeric[0].Body;
        }

        if (value < 0 && numeric.Count >= 2)
        {
            value = -value;
            return numeric[1].Body;
        }

        if (value == 0 && numeric.Count >= 3) return numeric[2].Body;
        return numeric[0].Body;
    }

    private static bool ConditionHolds(string condition, double value)
    {
        int i = 0;
        while (i < condition.Length && (condition[i] == '<' || condition[i] == '>' || condition[i] == '=')) i++;
        string op = condition.Substring(0, i);
        if (!double.TryParse(condition.AsSpan(i), NumberStyles.Float, CultureInfo.InvariantCulture, out double bound))
            return false;

        return op switch
        {
            "<" => value < bound,
            "<=" => value <= bound,
            ">" => value > bound,
            ">=" => value >= bound,
            "=" => value == bound,
            "<>" => value != bound,
            _ => false,
        };
    }

    private static bool IsGeneral(string section) =>
        section.Trim().Equals("General", StringComparison.OrdinalIgnoreCase);

    // ── tokens ────────────────────────────────────────────────────────────────

    private enum TokenKind
    {
        Literal,
        IntegerDigit,
        DecimalDigit,
        DecimalPoint,
        DatePart,
    }

    private readonly record struct Token(TokenKind Kind, char Placeholder, string Text);

    /// <summary>
    /// Splits a section into what it emits and where the value's digits go, or <c>null</c> for a
    /// section this cannot render.
    /// </summary>
    private static List<Token>? Tokenize(string section)
    {
        var tokens = new List<Token>();
        bool afterDecimalPoint = false;
        bool sawDigit = false;
        int trailingCommas = 0;
        bool grouping = false;
        bool percent = false;

        for (int i = 0; i < section.Length; i++)
        {
            char c = section[i];
            switch (c)
            {
                case '"':
                {
                    int close = section.IndexOf('"', i + 1);
                    if (close < 0) { tokens.Add(Literal(section.Substring(i + 1))); i = section.Length; break; }
                    tokens.Add(Literal(section.Substring(i + 1, close - i - 1)));
                    i = close;
                    break;
                }
                case '\\':
                    if (i + 1 < section.Length) tokens.Add(Literal(section[++i].ToString()));
                    break;
                case '_':
                    // Reserve the width of the next character: display alignment, no content.
                    if (i + 1 < section.Length) i++;
                    break;
                case '*':
                    // Repeat the next character to fill the cell: display alignment, no content.
                    if (i + 1 < section.Length) i++;
                    break;
                case '[':
                {
                    int close = section.IndexOf(']', i);
                    if (close < 0) { tokens.Add(Literal("[")); break; }
                    string inner = section.Substring(i + 1, close - i - 1);
                    i = close;
                    if (inner.StartsWith("$", StringComparison.Ordinal))
                    {
                        // [$€-407]: a currency symbol and a locale id. The symbol is content.
                        int dash = inner.IndexOf('-');
                        string symbol = dash >= 0 ? inner.Substring(1, dash - 1) : inner.Substring(1);
                        if (symbol.Length > 0) tokens.Add(Literal(symbol));
                    }
                    else if (inner.Length > 0 && (inner[0] is 'h' or 'H' or 'm' or 'M' or 's' or 'S'))
                    {
                        // [h]/[mm]/[ss]: elapsed time, which is not a clock reading and is not
                        // reproduced here.
                        return null;
                    }
                    // Anything else in brackets is a colour, which has no text.
                    break;
                }
                case '0':
                case '#':
                case '?':
                    sawDigit = true;
                    trailingCommas = 0;
                    tokens.Add(new Token(
                        afterDecimalPoint ? TokenKind.DecimalDigit : TokenKind.IntegerDigit, c, ""));
                    break;
                case '.':
                    if (afterDecimalPoint) { tokens.Add(Literal(".")); break; }
                    afterDecimalPoint = true;
                    tokens.Add(new Token(TokenKind.DecimalPoint, '.', ""));
                    break;
                case ',':
                    // Between integer placeholders it turns grouping on; trailing a placeholder
                    // it scales the value down by a thousand for each comma. Which of the two it
                    // turns out to be is settled by whether another placeholder follows, so both
                    // are recorded and the digit case clears the scale again.
                    if (sawDigit) { grouping |= !afterDecimalPoint; trailingCommas++; }
                    else tokens.Add(Literal(","));
                    break;
                case '%':
                    percent = true;
                    tokens.Add(Literal("%"));
                    break;
                case '/':
                    // A fraction (# ?/?) is a shape this renderer does not produce.
                    if (sawDigit) return null;
                    tokens.Add(Literal("/"));
                    break;
                case 'E':
                case 'e':
                    // Scientific notation is likewise left to General.
                    if (i + 1 < section.Length && (section[i + 1] == '+' || section[i + 1] == '-')) return null;
                    tokens.Add(Literal(c.ToString()));
                    break;
                case 'y': case 'Y': case 'd': case 'D': case 'h': case 'H': case 's': case 'S':
                case 'm': case 'M':
                {
                    int run = 1;
                    while (i + run < section.Length && char.ToLowerInvariant(section[i + run]) == char.ToLowerInvariant(c))
                        run++;
                    tokens.Add(new Token(TokenKind.DatePart, char.ToLowerInvariant(c), new string(c, run)));
                    i += run - 1;
                    break;
                }
                case 'A': case 'a':
                {
                    // AM/PM and its A/P short form; any other 'a' is a literal.
                    if (section.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.DatePart, 'a', "AM/PM"));
                        i += 4;
                    }
                    else if (section.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.DatePart, 'a', "A/P"));
                        i += 2;
                    }
                    else tokens.Add(Literal(c.ToString()));
                    break;
                }
                case '@':
                    // The text placeholder, in a section reached by a number: nothing to put there.
                    break;
                default:
                    tokens.Add(Literal(c.ToString()));
                    break;
            }
        }

        if (tokens.Count == 0) return null;

        bool hasDate = tokens.Any(t => t.Kind == TokenKind.DatePart);
        bool hasDigits = tokens.Any(t => t.Kind is TokenKind.IntegerDigit or TokenKind.DecimalDigit);
        // A section of pure literal text is a real format — `#,##0;(#,##0);"nil"` writes "nil"
        // for zero — but one that holds nothing at all is not something to render.
        if (!hasDate && !hasDigits && !tokens.Any(t => t.Text.Length > 0)) return null;
        // A section mixing the two is not something Excel writes, and not something to guess at.
        if (hasDate && hasDigits) return null;

        if (!hasDate)
        {
            tokens.Add(new Token(TokenKind.Literal, '\0', ScaleMarker(percent, trailingCommas, grouping)));
        }
        return tokens;
    }

    private static Token Literal(string text) => new(TokenKind.Literal, '\0', text);

    /// <summary>
    /// The scaling and grouping a numeric section asks for, carried as a trailing marker token
    /// so <see cref="RenderNumber"/> reads it back without re-walking the format code.
    /// </summary>
    private const string ScaleMarkerPrefix = " scale:";

    private static string ScaleMarker(bool percent, int trailingCommas, bool grouping) =>
        $"{ScaleMarkerPrefix}{(percent ? 1 : 0)},{trailingCommas},{(grouping ? 1 : 0)}";

    private static (bool Percent, int TrailingCommas, bool Grouping) ReadScale(List<Token> tokens)
    {
        var marker = tokens[^1].Text;
        var parts = marker.Substring(ScaleMarkerPrefix.Length).Split(',');
        return (parts[0] == "1", int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2] == "1");
    }

    // ── numbers ───────────────────────────────────────────────────────────────

    private static string RenderNumber(List<Token> tokens, double value)
    {
        var (percent, trailingCommas, grouping) = ReadScale(tokens);
        tokens = tokens.GetRange(0, tokens.Count - 1);

        if (percent) value *= 100;
        for (int i = 0; i < trailingCommas; i++) value /= 1000;

        bool negative = value < 0;
        if (negative) value = -value;

        int decimals = tokens.Count(t => t.Kind == TokenKind.DecimalDigit);
        string rounded = value.ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int dot = rounded.IndexOf('.');
        string integerDigits = dot < 0 ? rounded : rounded.Substring(0, dot);
        string decimalDigits = dot < 0 ? "" : rounded.Substring(dot + 1);

        // A zero integer part is a leading zero, and a leading zero is shown only where a `0`
        // placeholder insists: `#.##` writes `.5`, and the accounting zero section `"-"??`
        // writes its dash with nothing beside it.
        if (integerDigits == "0" && !tokens.Any(t => t.Kind == TokenKind.IntegerDigit && t.Placeholder == '0'))
            integerDigits = "";

        if (grouping) integerDigits = Group(integerDigits);

        var text = new string?[tokens.Count];
        FillIntegerPlaceholders(tokens, integerDigits, text);
        bool anyDecimal = FillDecimalPlaceholders(tokens, decimalDigits, text);

        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.DecimalPoint) { if (anyDecimal) sb.Append('.'); continue; }
            sb.Append(token.Kind == TokenKind.Literal ? token.Text : text[i]);
        }

        // Only the alignment padding is dropped, so trimming here removes nothing a reader wants.
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Gives each integer placeholder its digits, right to left, so the leftmost one absorbs
    /// whatever the format is too narrow to hold. That single rule is what makes
    /// <c>000-00-0000</c> and <c>#,##0</c> the same code path.
    /// </summary>
    private static void FillIntegerPlaceholders(
        IReadOnlyList<Token> tokens, string integerDigits, string?[] text)
    {
        int leftmost = -1;
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Kind == TokenKind.IntegerDigit) { leftmost = i; break; }
        if (leftmost < 0) return;

        int cursor = integerDigits.Length;
        for (int i = tokens.Count - 1; i > leftmost; i--)
        {
            if (tokens[i].Kind != TokenKind.IntegerDigit) continue;

            // A group separator belongs to the digit on its left, so it is taken first and
            // written after it: reading "31,200" from the right gives "0", "0", "2", "1,", "3".
            var suffix = new StringBuilder();
            while (cursor > 0 && integerDigits[cursor - 1] == ',') suffix.Insert(0, integerDigits[--cursor]);

            if (cursor > 0) text[i] = integerDigits[--cursor] + suffix.ToString();
            else text[i] = tokens[i].Placeholder switch { '0' => "0", '?' => " ", _ => "" } + suffix.ToString();
        }

        text[leftmost] = cursor > 0
            ? integerDigits.Substring(0, cursor)
            : tokens[leftmost].Placeholder switch { '0' => "0", '?' => " ", _ => "" };
    }

    /// <summary>
    /// Gives each decimal placeholder its digit, dropping the trailing zeroes the format does
    /// not insist on: <c>0</c> keeps one, <c>?</c> replaces it with a space, <c>#</c> drops it.
    /// </summary>
    /// <returns>Whether anything at all was written after the decimal point, which is what
    /// decides whether the point itself is written.</returns>
    private static bool FillDecimalPlaceholders(
        IReadOnlyList<Token> tokens, string decimalDigits, string?[] text)
    {
        var places = new List<int>();
        for (int i = 0; i < tokens.Count; i++)
            if (tokens[i].Kind == TokenKind.DecimalDigit) places.Add(i);

        bool significant = false;
        bool any = false;
        for (int k = places.Count - 1; k >= 0; k--)
        {
            int i = places[k];
            char digit = k < decimalDigits.Length ? decimalDigits[k] : '0';
            if (digit != '0') significant = true;

            string written;
            if (significant || tokens[i].Placeholder == '0') written = digit.ToString();
            else if (tokens[i].Placeholder == '?') written = " ";
            else written = "";

            text[i] = written;
            if (written.Length > 0) any = true;
        }
        return any;
    }

    private static string Group(string digits)
    {
        if (digits.Length <= 3) return digits;
        var sb = new StringBuilder(digits.Length + digits.Length / 3);
        int lead = digits.Length % 3;
        if (lead > 0) sb.Append(digits, 0, lead);
        for (int i = lead; i < digits.Length; i += 3)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(digits, i, 3);
        }
        return sb.ToString();
    }

    // ── dates ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a date or time. A serial outside the range a date can hold is not a date, so the
    /// caller's own rendering stands.
    /// </summary>
    private static string? RenderDateTime(List<Token> tokens, double serial)
    {
        DateTime when;
        try
        {
            when = DateTime.FromOADate(serial);
        }
        catch (ArgumentException)
        {
            return null;
        }

        bool twelveHour = tokens.Any(t => t.Kind == TokenKind.DatePart && t.Placeholder == 'a');
        var sb = new StringBuilder();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.DatePart) { sb.Append(token.Text); continue; }

            int width = token.Text.Length;
            switch (token.Placeholder)
            {
                case 'y':
                    sb.Append(width <= 2
                        ? (when.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
                        : when.Year.ToString("D4", CultureInfo.InvariantCulture));
                    break;
                case 'd':
                    sb.Append(width switch
                    {
                        1 => when.Day.ToString(CultureInfo.InvariantCulture),
                        2 => when.Day.ToString("D2", CultureInfo.InvariantCulture),
                        3 => when.ToString("ddd", CultureInfo.InvariantCulture),
                        _ => when.ToString("dddd", CultureInfo.InvariantCulture),
                    });
                    break;
                case 'h':
                {
                    int hour = twelveHour ? (when.Hour % 12 == 0 ? 12 : when.Hour % 12) : when.Hour;
                    sb.Append(width <= 1
                        ? hour.ToString(CultureInfo.InvariantCulture)
                        : hour.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                }
                case 's':
                    sb.Append(width <= 1
                        ? when.Second.ToString(CultureInfo.InvariantCulture)
                        : when.Second.ToString("D2", CultureInfo.InvariantCulture));
                    break;
                case 'a':
                    sb.Append(token.Text.Length == 3
                        ? (when.Hour < 12 ? "A" : "P")
                        : (when.Hour < 12 ? "AM" : "PM"));
                    break;
                case 'm':
                {
                    // 'm' is minutes next to an hour or a second, and months everywhere else.
                    bool minutes = IsMinuteToken(tokens, i);
                    if (minutes)
                    {
                        sb.Append(width <= 1
                            ? when.Minute.ToString(CultureInfo.InvariantCulture)
                            : when.Minute.ToString("D2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(width switch
                        {
                            1 => when.Month.ToString(CultureInfo.InvariantCulture),
                            2 => when.Month.ToString("D2", CultureInfo.InvariantCulture),
                            3 => when.ToString("MMM", CultureInfo.InvariantCulture),
                            4 => when.ToString("MMMM", CultureInfo.InvariantCulture),
                            _ => when.ToString("MMMM", CultureInfo.InvariantCulture).Substring(0, 1),
                        });
                    }
                    break;
                }
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Whether the <c>m</c> at <paramref name="index"/> means minutes: the nearest date
    /// part before it is an hour, or the nearest one after it is a second.</summary>
    private static bool IsMinuteToken(List<Token> tokens, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (tokens[i].Kind != TokenKind.DatePart) continue;
            if (tokens[i].Placeholder == 'h') return true;
            break;
        }
        for (int i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.DatePart) continue;
            if (tokens[i].Placeholder == 's') return true;
            break;
        }
        return false;
    }

    // ── built-in formats ──────────────────────────────────────────────────────

    /// <summary>
    /// The format code for a built-in id, or <c>null</c> for one that is locale-defined or
    /// reserved — a workbook always writes its own FORMAT record for those.
    /// </summary>
    /// <remarks>ECMA-376 §18.8.30's implied number formats; ids 23-36 are omitted deliberately,
    /// being locale-specific in the ones Excel actually writes.</remarks>
    public static string? BuiltIn(int id) => id switch
    {
        0 => "General",
        1 => "0",
        2 => "0.00",
        3 => "#,##0",
        4 => "#,##0.00",
        5 => "\"$\"#,##0_);(\"$\"#,##0)",
        6 => "\"$\"#,##0_);[Red](\"$\"#,##0)",
        7 => "\"$\"#,##0.00_);(\"$\"#,##0.00)",
        8 => "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
        9 => "0%",
        10 => "0.00%",
        11 => "0.00E+00",
        12 => "# ?/?",
        13 => "# ??/??",
        14 => "m/d/yyyy",
        15 => "d-mmm-yy",
        16 => "d-mmm",
        17 => "mmm-yy",
        18 => "h:mm AM/PM",
        19 => "h:mm:ss AM/PM",
        20 => "h:mm",
        21 => "h:mm:ss",
        22 => "m/d/yyyy h:mm",
        37 => "#,##0 ;(#,##0)",
        38 => "#,##0 ;[Red](#,##0)",
        39 => "#,##0.00;(#,##0.00)",
        40 => "#,##0.00;[Red](#,##0.00)",
        41 => "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
        42 => "_(\"$\"* #,##0_);_(\"$\"* (#,##0);_(\"$\"* \"-\"_);_(@_)",
        43 => "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
        44 => "_(\"$\"* #,##0.00_);_(\"$\"* (#,##0.00);_(\"$\"* \"-\"??_);_(@_)",
        45 => "mm:ss",
        46 => "[h]:mm:ss",
        47 => "mm:ss.0",
        48 => "##0.0E+0",
        49 => "@",
        _ => null,
    };
}

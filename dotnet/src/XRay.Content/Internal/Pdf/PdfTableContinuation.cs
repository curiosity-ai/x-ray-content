using System;
using System.Collections.Generic;
using System.Linq;
using XRay.Content.Types;

namespace XRay.Content.Internal.Pdf;

/// <summary>
/// Join a ruled table to its continuation on the next page.
/// </summary>
/// <remarks>
/// A table longer than a page is one table, and every tier here reads a page at a time, so a
/// long one arrives as a fragment per page: rows that belong together land in different entries
/// of <c>tables[]</c>, and the header — which only the first fragment has — stops describing
/// the rest of them. Upstream does not join these either. Its
/// <see cref="PdfTableStitch"/> groups by page before it does anything, and is reachable only
/// from the structure pipeline, so this is new rather than a port; it is the reason it lives in
/// its own file.
/// <para>
/// What makes the join safe is the geometry the ruling-line tier already has. Two tables
/// continue each other when the gridlines bounding their columns are the same lines — not
/// merely the same count, and not merely the same outer margins, both of which a document with
/// one page layout hands out to every table on every page. A document whose sections each get
/// their own table, all of them full-width and stacked one after another, is exactly the case
/// that a looser test joins wrongly.
/// </para>
/// </remarks>
internal static class PdfTableContinuation
{
    /// <summary>How far apart two column gridlines may be and still be the same line.</summary>
    /// <remarks>
    /// The same rule drawn on two pages lands on the same coordinate to well under a point;
    /// this is the ruling tier's own snap tolerance, so two edges it would have merged inside
    /// one page are not separated across a page break.
    /// </remarks>
    private const double ColumnMatchTolerancePts = 3.0;

    /// <summary>Bound on the pages one joined table may span.</summary>
    /// <remarks>
    /// A run this long is a document that is one table, which is real — the bound is here so a
    /// mis-join cannot swallow a whole corpus fixture, not because tables this long are
    /// suspect.
    /// </remarks>
    private const int MaxJoinedPages = 64;

    /// <summary>
    /// Join each table to the one continuing it on the following page, and drop every
    /// continuation candidate nothing claimed.
    /// </summary>
    /// <remarks>
    /// Order is preserved: a joined table keeps the position, page and bounding box of its
    /// head, which is where a reader looking for it will look.
    /// </remarks>
    /// <returns>
    /// The tables to emit, and — separately — the fragments folded into them. A joined table is
    /// filed under the page it starts on, so the pages its rest came from would otherwise look
    /// untouched to the structure pipeline and hand that text back a second time as prose.
    /// </returns>
    public static (List<Table> Tables, List<Table> Continuations) Join(IReadOnlyList<RuledTable> tables)
    {
        var byPage = new Dictionary<uint, List<int>>();
        for (int i = 0; i < tables.Count; i++)
        {
            if (!byPage.TryGetValue(tables[i].Table.PageNumber, out var list))
                byPage[tables[i].Table.PageNumber] = list = new List<int>();
            list.Add(i);
        }

        // The continuation of a table is the topmost table of the next page, and only a table
        // at the very bottom of its own page can have one. Both are resolved once per page.
        var lastOfPage = new Dictionary<uint, int>();
        var firstOfPage = new Dictionary<uint, int>();
        foreach (var (page, indices) in byPage)
        {
            lastOfPage[page] = indices.MinBy(i => Bottom(tables[i]));
            firstOfPage[page] = indices.MaxBy(i => Top(tables[i]));
        }

        // `continues[i]` is the table that carries on from table i, resolved before anything is
        // merged so a chain of pages is followed in one pass.
        var continues = new int[tables.Count];
        Array.Fill(continues, -1);
        var claimed = new bool[tables.Count];
        foreach (var (page, head) in lastOfPage)
        {
            if (!firstOfPage.TryGetValue(page + 1, out int tail)) continue;
            if (claimed[tail] || !Continues(tables[head], tables[tail])) continue;
            continues[head] = tail;
            claimed[tail] = true;
        }

        var result = new List<Table>(tables.Count);
        var continuations = new List<Table>();
        for (int i = 0; i < tables.Count; i++)
        {
            if (claimed[i]) continue;

            var head = tables[i].Table;
            bool wasCandidate = tables[i].IsContinuationCandidate;
            var folded = new List<Table>();
            for (int next = continues[i]; next >= 0 && folded.Count + 1 < MaxJoinedPages; next = continues[next])
            {
                folded.Add(tables[next].Table);
                head = Append(head, tables[next].Table);
            }

            // A candidate that joined nothing was never a table; one that did is the head of
            // the table it joined, which is what it was a fragment of all along.
            if (wasCandidate && folded.Count == 0) continue;
            result.Add(head);
            continuations.AddRange(folded);
        }
        return (result, continuations);
    }

    private static double Bottom(RuledTable t) => t.Table.BoundingBox is { } b ? b.Y0 : double.MaxValue;

    private static double Top(RuledTable t) => t.Table.BoundingBox is { } b ? b.Y1 : double.MinValue;

    /// <summary>
    /// Whether <paramref name="tail"/> is the rest of <paramref name="head"/>: the same grid,
    /// read on the next page.
    /// </summary>
    private static bool Continues(RuledTable head, RuledTable tail)
    {
        // At least one side has to be a table. Two bands both too small to be one, joined, are
        // still not evidence of a table — and a band repeated at the same place on consecutive
        // pages is the shape of page furniture, a repeated column header or a navigation strip,
        // far more often than it is a table two rows long split across a page break.
        if (head.IsContinuationCandidate && tail.IsContinuationCandidate) return false;
        if (head.ColumnBoundaries is not { } a || tail.ColumnBoundaries is not { } b) return false;
        if (a.Length < 2 || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > ColumnMatchTolerancePts) return false;

        // Cell width is what the reader sees; two grids can share their gridlines and still be
        // read into different numbers of columns when a tier merges some of them.
        if (ColumnCount(head.Table) == 0 || ColumnCount(head.Table) != ColumnCount(tail.Table))
            return false;

        // Sharing a grid is not being the same table. A page laid out in one grid puts every
        // table on it, and two of them can sit in different columns of it — a page of Foo in
        // the left three columns followed by a page of Bar in the right three is one grid and
        // two tables. A continuation writes in the columns its head wrote in, so one side's
        // filled columns must be the other's, or a subset of them.
        var headColumns = FilledColumns(head.Table);
        var tailColumns = FilledColumns(tail.Table);
        return headColumns.Count > 0 && tailColumns.Count > 0
            && (headColumns.IsSubsetOf(tailColumns) || tailColumns.IsSubsetOf(headColumns));
    }

    private static int ColumnCount(Table t) => t.Cells.Count > 0 ? t.Cells[0].Count : 0;

    /// <summary>The indices of the columns this table wrote anything in.</summary>
    private static HashSet<int> FilledColumns(Table t)
    {
        var filled = new HashSet<int>();
        foreach (var row in t.Cells)
            for (int i = 0; i < row.Count; i++)
                if (row[i].Trim().Length > 0) filled.Add(i);
        return filled;
    }

    /// <summary>
    /// The head with the tail's rows after its own. The head keeps its page and bounding box —
    /// a table spanning pages has no one box, and where it starts is what locates it.
    /// </summary>
    private static Table Append(Table head, Table tail)
    {
        var cells = new List<List<string>>(head.Cells.Count + tail.Cells.Count);
        cells.AddRange(head.Cells);
        cells.AddRange(tail.Cells);

        return new Table
        {
            Cells = cells,
            Markdown = head.Markdown + WithoutHeaderRule(tail.Markdown),
            PageNumber = head.PageNumber,
            BoundingBox = head.BoundingBox,
        };
    }

    /// <summary>
    /// The tail's rows without the `| --- |` its own first row was given. Appending its markdown
    /// verbatim would put a second header rule in the middle of the joined table; rebuilding
    /// both halves through a renderer instead would re-escape cell text the ruling tier writes
    /// through unaltered.
    /// </summary>
    private static string WithoutHeaderRule(string markdown)
    {
        var kept = markdown.Split('\n').Where(line => !IsHeaderRule(line));
        return string.Join('\n', kept);
    }

    private static bool IsHeaderRule(string line)
    {
        bool sawDashes = false;
        foreach (string cell in line.Split('|'))
        {
            string trimmed = cell.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Any(c => c != '-')) return false;
            sawDashes = true;
        }
        return sawDashes;
    }
}

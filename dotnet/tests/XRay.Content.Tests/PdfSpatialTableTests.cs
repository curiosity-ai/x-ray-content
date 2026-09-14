using System.Collections.Generic;
using XRay.Content.Internal.Pdf;
using XRay.Content.Internal.PdfOxide.Layout;
using XRay.Content.Internal.PdfOxide.Text;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>Covers the ruling-line table tier (`PdfSpatialTables`).</summary>
public class PdfSpatialTableTests
{
    private static PdfPath Line(double x1, double y1, double x2, double y2, double width = 0.75)
    {
        var ops = new List<PathOp> { PathOp.MoveTo(x1, y1), PathOp.LineTo(x2, y2) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = width };
    }

    private static PdfPath Rect(double x, double y, double w, double h, double width = 0.75)
    {
        var ops = new List<PathOp> { PathOp.Rect(x, y, w, h) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Stroked = true, StrokeWidth = width };
    }

    private static TableSpan Word(string text, double x, double y, double w = 20, double h = 10) =>
        new() { Text = text, Bbox = new PathRect(x, y, w, h), FontSize = 10 };

    private static XRay.Content.Internal.PdfOxide.OxTextSpan OxSpan(
        string text, float x, float y, float width, float rotation) =>
        new()
        {
            Text = text,
            Bbox = new XRay.Content.Internal.PdfOxide.OxRect(x, y, width, 10.0f),
            FontSize = 10.0f,
            RotationDegrees = rotation,
            CharWidths = new List<float>(new float[text.Length]),
        };

    /// <summary>
    /// A page whose runs are mostly sideways is ordered in the rotated frame, and the map
    /// back out of that frame is not the exact inverse of the map into it: the span origins
    /// the word grid is built from carry a single-precision round-trip of the page width.
    /// </summary>
    [Fact]
    public void SidewaysPageSpanOriginsCarryTheRotatedFrameRoundTrip()
    {
        var spans = new List<XRay.Content.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 90.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 90.0f),
        };

        var turned = OxPageOrder.PageReadingOrder(Clone(spans), 0, (0f, 0f, 612f, 792f));

        // 612 - (612 - 81.502) lands one ULP of the page width above 81.502.
        Assert.Equal(81.50201416015625f, turned[0].Bbox.X);
        // Only the origin turns; the run's own extents describe it in its upright frame.
        Assert.Equal(30.0f, turned[0].Bbox.Width);
        Assert.Equal(10.0f, turned[0].Bbox.Height);
    }

    /// <summary>
    /// The page's own <c>/Rotate</c> takes the rotated-frame branch out of play:
    /// `postprocess_spans` has already mapped such a page into its displayed frame.
    /// </summary>
    [Fact]
    public void ARotatedPageSkipsTheReadingFrameRoundTrip()
    {
        var spans = new List<XRay.Content.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 90.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 90.0f),
        };

        var ordered = OxPageOrder.PageReadingOrder(Clone(spans), 90, (0f, 0f, 612f, 792f));

        Assert.Equal(81.502f, ordered[0].Bbox.X);
    }

    /// <summary>An upright page has no rotated reading frame to round-trip through.</summary>
    [Fact]
    public void AnUprightPageIsLeftAlone()
    {
        var spans = new List<XRay.Content.Internal.PdfOxide.OxTextSpan>
        {
            OxSpan("alpha", 81.502f, 72.0f, 30.0f, 0.0f),
            OxSpan("beta", 81.502f, 120.0f, 24.0f, 0.0f),
        };

        var ordered = OxPageOrder.PageReadingOrder(Clone(spans), 0, (0f, 0f, 612f, 792f));

        Assert.Equal(81.502f, ordered[0].Bbox.X);
    }

    /// <summary>
    /// A <c>/Rotate 90</c> page turns its rotated-content runs into the displayed frame,
    /// where the run's advance becomes its height (`map_span_into_rotated_frame`).
    /// </summary>
    [Fact]
    public void RotatedContentOnARotatedPageIsMappedIntoTheDisplayedFrame()
    {
        var span = OxSpan("CENTRAL", 59.57f, 166.21f, 53.34f, 90.0f);
        span.Bbox = new XRay.Content.Internal.PdfOxide.OxRect(59.57f, 166.21f, 53.34f, 7.5f);

        OxSpanPostprocess.MapSpanIntoRotatedFrame(span, 90, 0f, 0f, 612f, 792f);

        Assert.Equal(166.21f, span.Bbox.X, 3);
        Assert.Equal(612f - 59.57f - 53.34f, span.Bbox.Y, 3);
        Assert.Equal(7.5f, span.Bbox.Width, 3);
        Assert.Equal(53.34f, span.Bbox.Height, 3);
    }

    private static List<XRay.Content.Internal.PdfOxide.OxTextSpan> Clone(
        List<XRay.Content.Internal.PdfOxide.OxTextSpan> spans)
    {
        var copy = new List<XRay.Content.Internal.PdfOxide.OxTextSpan>(spans.Count);
        foreach (var s in spans) copy.Add(s.Clone());
        return copy;
    }

    [Fact]
    public void ANegativeExtentRectangleIsNormalizedLikeRustsRectNew()
    {
        // `20 204 480 -160 re` — Skia writes the box with the height running downward.
        var r = new PathRect(20, 204, 480, -160);
        Assert.Equal(44, r.Y);
        Assert.Equal(160, r.Height);
        Assert.Equal(44, r.Top);
        Assert.Equal(204, r.Bottom);
    }

    [Fact]
    public void AThinLongStrokeIsATablePrimitiveAndAZeroLengthDotIsNot()
    {
        Assert.True(Line(20, 76, 500, 76).IsTablePrimitive());
        Assert.True(Line(180, 44, 180, 204).IsTablePrimitive());
        // A zero-length round-capped segment renders as a blob, not a ruling.
        var dot = Line(180, 44, 180, 44, width: 6);
        dot.LineCap = 1;
        Assert.False(dot.IsTablePrimitive());
    }

    [Fact]
    public void AStrokeWidthEncodedRuleUsesItsRenderedExtent()
    {
        // A 1 pt segment stroked 40 pt wide is a bar 40 pt tall, not a speck.
        var bar = Line(100, 500, 101, 500, width: 40);
        var rendered = bar.RenderedBbox();
        Assert.Equal(40, rendered.Height, 3);
    }

    [Fact]
    public void AZeroLengthButtCappedSegmentKeepsItsDegenerateExtent()
    {
        // §8.4.3.3: a butt cap paints nothing at zero length. The bound upstream compares
        // the length against is the f32 machine epsilon, not the smallest denormal — with
        // the latter the guard never fires and the segment gains a stroke-wide box.
        var speck = Line(180, 44, 180, 44, width: 6);
        var rendered = speck.RenderedBbox();
        Assert.Equal(0, rendered.Width, 6);
        Assert.Equal(0, rendered.Height, 6);
    }

    [Fact]
    public void AnAxisAlignedRuleKeepsItsEdgesInSinglePrecision()
    {
        // The rendered edges are what the table bounding box is built from, and upstream
        // computes them in f32 throughout. A horizontal rule extends by exactly half the
        // stroke on the cross axis and not at all along its own, whatever its length.
        var rule = Line(73, 569.8, 543.2, 569.8, width: 1);
        var rendered = rule.RenderedBbox();
        Assert.Equal(73.0f, (float)rendered.X);
        Assert.Equal(543.2f - 73.0f, (float)rendered.Width);
        Assert.Equal(569.8f - 0.5f, (float)rendered.Y);
        Assert.Equal(1.0f, (float)rendered.Height);
        // `right()` is the single-precision sum of two single-precision edges.
        Assert.Equal((float)rendered.X + (float)rendered.Width, (float)rendered.Right);
    }

    [Fact]
    public void ARuledGridBecomesATableWithItsCellsInPlace()
    {
        var paths = new List<PdfPath>
        {
            Rect(20, 44, 480, 160),
            Line(20, 76, 500, 76), Line(20, 108, 500, 108),
            Line(20, 140, 500, 140), Line(20, 172, 500, 172),
            Line(180, 44, 180, 204), Line(286, 44, 286, 204), Line(393, 44, 393, 204),
        };
        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 296, 180), Word("Q3", 400, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 296, 148), Word("1,502", 400, 148),
            Word("South", 30, 116), Word("942", 190, 116), Word("1,011", 296, 116), Word("1,140", 400, 116),
            Word("East", 30, 84), Word("1,655", 190, 84), Word("1,702", 296, 84), Word("1,690", 400, 84),
            Word("West", 30, 52), Word("803", 190, 52), Word("877", 296, 52), Word("934", 400, 52),
        };

        var tables = PdfSpatialTables.DetectPageTables(spans, paths, 1, TableDetectionConfig.Strict());

        var table = Assert.Single(tables);
        Assert.Equal(new List<string> { "Region", "Q1", "Q2", "Q3" }, table.Cells[0]);
        Assert.Equal(new List<string> { "West", "803", "877", "934" }, table.Cells[4]);
        Assert.StartsWith("| Region | Q1 | Q2 | Q3 |\n| --- | --- | --- | --- |\n", table.Markdown);
        Assert.Equal(1u, table.PageNumber);
    }

    /// <summary>
    /// A filled rectangle, as a shading or highlight is drawn — no stroke, so
    /// <see cref="PdfPath.RenderedBbox"/> is its geometry.
    /// </summary>
    private static PdfPath Fill(double x, double y, double w, double h)
    {
        var ops = new List<PathOp> { PathOp.Rect(x, y, w, h) };
        return new PdfPath { Operations = ops, Bbox = PdfPath.ComputeBbox(ops), Filled = true };
    }

    /// <summary>The 4x5 grid the tests below vary, drawn with every rule present.</summary>
    private static List<PdfPath> GridPaths() => new()
    {
        Line(20, 44, 500, 44), Line(20, 76, 500, 76), Line(20, 108, 500, 108),
        Line(20, 140, 500, 140), Line(20, 172, 500, 172), Line(20, 204, 500, 204),
        Line(20, 44, 20, 204), Line(180, 44, 180, 204),
        Line(340, 44, 340, 204), Line(500, 44, 500, 204),
    };

    /// <summary>
    /// A tracked-changes document strikes a deleted cell through, and that stroke reaches the
    /// detector as a line like any other. Where it runs over a column rule it raises an
    /// intersection, inventing a row boundary mid-row; the near-empty rows that boundary
    /// produces are what the emptiness filters then reject the whole table for.
    /// </summary>
    [Fact]
    public void AStrikethroughAcrossACellIsNotReadAsARowRule()
    {
        var paths = GridPaths();
        // One strikethrough per struck row, each drawn over its cell's text and so reaching
        // from just inside one column rule to just past the next — which is what lets it raise
        // an intersection on both.
        paths.Add(Line(182, 57, 343, 57));
        paths.Add(Line(182, 89, 343, 89));
        paths.Add(Line(182, 121, 343, 121));

        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 350, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 350, 148),
            Word("South", 30, 116, 145), Word("Struck out row", 190, 116, 145),
            Word("1,011", 350, 116),
            Word("East", 30, 84, 145), Word("Struck out row", 190, 84, 145),
            Word("1,702", 350, 84),
            Word("West", 30, 52, 145), Word("Struck out row", 190, 52, 145),
            Word("877", 350, 52),
        };

        var table = Assert.Single(PdfSpatialTables.DetectPageTables(spans, paths, 1, TableDetectionConfig.Strict()));
        Assert.Equal(5, table.Cells.Count);
        Assert.Equal(new List<string> { "East", "Struck out row", "1,702" }, table.Cells[3]);
    }

    /// <summary>
    /// A cell whose text wraps puts that text on several baselines inside one rule-bounded
    /// band. Splitting the band there strands each wrapped line in a row of its own with every
    /// other column empty — which is both wrong and, downstream, fatal to the table.
    /// </summary>
    [Fact]
    public void AWrappedCellStaysOneRow()
    {
        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 350, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 350, 148),
            // One cell wrapped onto three lines; its neighbours sit centred against it.
            Word("wrapped", 190, 128), Word("over", 190, 116), Word("three", 190, 104),
            Word("South", 30, 116), Word("1,011", 350, 116),
            Word("East", 30, 84), Word("1,655", 190, 84), Word("1,702", 350, 84),
            Word("West", 30, 52), Word("803", 190, 52), Word("877", 350, 52),
        };

        var table = Assert.Single(PdfSpatialTables.DetectPageTables(spans, GridPaths(), 1, TableDetectionConfig.Strict()));
        Assert.Equal(5, table.Cells.Count);
        Assert.Equal(new List<string> { "South", "wrapped over three", "1,011" }, table.Cells[2]);
    }

    /// <summary>
    /// Two rows sharing one tall cell: the row rule between them stops at the column it
    /// splits, so the tall cell's nearest bottom-right corner is missing and it has to be
    /// closed against the next gridline along. Its text belongs to the first of the two rows,
    /// the way <see cref="XRay.Content.Internal.Tables.GridFlatten"/> places every other
    /// format's merged cells.
    /// </summary>
    [Fact]
    public void AVerticallyMergedCellKeepsItsTextOnTheFirstRowItSpans()
    {
        var paths = GridPaths();
        // Splits only the leftmost column of the 76..108 band.
        paths.Add(Line(20, 92, 180, 92));

        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 350, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 350, 148),
            Word("South", 30, 116), Word("942", 190, 116), Word("1,011", 350, 116),
            Word("East", 30, 96), Word("West", 30, 80),
            // Centred across both, so it lands in the lower interval on its own centre.
            Word("1,655", 190, 84), Word("1,702", 350, 84),
            Word("Total", 30, 52), Word("803", 190, 52), Word("877", 350, 52),
        };

        var table = Assert.Single(PdfSpatialTables.DetectPageTables(spans, paths, 1, TableDetectionConfig.Strict()));
        Assert.Equal(new List<string> { "East", "1,655", "1,702" }, table.Cells[3]);
        Assert.Equal(new List<string> { "West", "", "" }, table.Cells[4]);
    }

    /// <summary>
    /// A shading rectangle is drawn against the rule it sits behind, a fraction of a point
    /// away. Snapping each coordinate to the first of its group rather than chaining from the
    /// one before splits that pair whenever a third edge sorts ahead of them within tolerance
    /// — and the half-point offcut becomes a column of its own down the whole table.
    /// </summary>
    [Fact]
    public void AShadingDrawnAgainstARuleDoesNotBecomeItsOwnColumn()
    {
        var paths = GridPaths();
        paths.Add(Fill(20.8, 172, 479.4, 31.8));
        // An unrelated rule above the table, close enough to the table's left edge to anchor
        // the snapped group there.
        paths.Add(Line(17.5, 220, 17.5, 260));

        var spans = new List<TableSpan>
        {
            Word("Region", 30, 180), Word("Q1", 190, 180), Word("Q2", 350, 180),
            Word("North", 30, 148), Word("1,204", 190, 148), Word("1,388", 350, 148),
            Word("South", 30, 116), Word("942", 190, 116), Word("1,011", 350, 116),
            Word("East", 30, 84), Word("1,655", 190, 84), Word("1,702", 350, 84),
            Word("West", 30, 52), Word("803", 190, 52), Word("877", 350, 52),
        };

        var table = Assert.Single(PdfSpatialTables.DetectPageTables(spans, paths, 1, TableDetectionConfig.Strict()));
        Assert.All(table.Cells, row => Assert.Equal(3, row.Count));
        Assert.Equal(new List<string> { "Region", "Q1", "Q2" }, table.Cells[0]);
    }

    [Fact]
    public void APageWithNoRulingLinesYieldsNoRuledTable()
    {
        var spans = new List<TableSpan> { Word("Region", 30, 180), Word("Q1", 190, 180) };
        Assert.Empty(PdfSpatialTables.DetectPageTables(spans, new List<PdfPath>(), 1, TableDetectionConfig.Strict()));
    }

    [Fact]
    public void WordPiecesEmittedAsSeparateOperatorsAreGluedBackTogether()
    {
        // pdf_oxide assembles words from glyphs, so `1,011` never reaches the detector
        // as two pieces; our spans arrive cut at every Tj.
        var spans = new List<TextSpan>
        {
            new() { Text = "1,01", X = 251.25, Y = 667.5, Width = 18.98, Height = 9.75, FontSize = 9.75 },
            new() { Text = "1", X = 269.51, Y = 667.5, Width = 5.42, Height = 9.75, FontSize = 9.75 },
            new() { Text = "877", X = 331.5, Y = 667.5, Width = 16.27, Height = 9.75, FontSize = 9.75 },
        };
        var words = PdfSpatialTables.SpansToWords(spans);
        Assert.Equal(new[] { "1,011", "877" }, words.ConvertAll(w => w.Text).ToArray());
    }

    [Fact]
    public void ASpanHoldingSeveralWordsIsSplitProportionally()
    {
        var spans = new List<TextSpan>
        {
            new() { Text = "Quarterly revenue", X = 40, Y = 700, Width = 170, Height = 11, FontSize = 11 },
        };
        var words = PdfSpatialTables.SpansToWords(spans);
        Assert.Equal(2, words.Count);
        Assert.Equal("Quarterly", words[0].Text);
        Assert.Equal("revenue", words[1].Text);
        Assert.True(words[1].Bbox.X > words[0].Bbox.X + words[0].Bbox.Width);
    }
}

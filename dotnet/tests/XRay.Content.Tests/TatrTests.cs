using XRay.Content.Internal.Imaging;
using XRay.Content.Internal.Layout;
using Xunit;

namespace XRay.Content.Tests;

/// <summary>
/// Port of the Rust <c>layout::models::tatr</c> test module.
/// </summary>
public class TatrTests
{
    private static TatrDetection Detection(float[] box, float confidence, TatrClass className) =>
        new(box[0], box[1], box[2], box[3], confidence, className);

    private static TatrDetection Row(float[] box, float confidence) =>
        Detection(box, confidence, TatrClass.Row);

    private static TatrDetection Column(float[] box, float confidence) =>
        Detection(box, confidence, TatrClass.Column);

    private static TatrDetection HeaderDetection(float[] box) =>
        Detection(box, 0.9f, TatrClass.ColumnHeader);

    private static TatrDetection SpanningDetection(float[] box) =>
        Detection(box, 0.9f, TatrClass.SpanningCell);

    // ------------------------------------------------------------------ DETR resize

    [Fact]
    public void ComputeDetrResizeLandscape() => Assert.Equal((1000, 750), TatrModel.ComputeDetrResize(1600, 1200));

    [Fact]
    public void ComputeDetrResizePortrait() => Assert.Equal((600, 1000), TatrModel.ComputeDetrResize(600, 1000));

    [Fact]
    public void ComputeDetrResizeVeryElongated() => Assert.Equal((33, 1000), TatrModel.ComputeDetrResize(100, 3000));

    [Fact]
    public void ComputeDetrResizeSquare() => Assert.Equal((800, 800), TatrModel.ComputeDetrResize(800, 800));

    /// <summary>
    /// The tentative long edge is truncated before the cap is applied, matching Hugging Face's
    /// <c>get_resize_output_image_size</c>. Collapsing it into one ratio drifts by a pixel.
    /// </summary>
    [Fact]
    public void ComputeDetrResizeTruncatesLikeHuggingFace()
    {
        Assert.Equal((807, 800), TatrModel.ComputeDetrResize(102, 101));
        Assert.Equal((353, 1000), TatrModel.ComputeDetrResize(6, 17));
    }

    [Fact]
    public void ComputeDetrResizeSmall() => Assert.Equal((666, 1000), TatrModel.ComputeDetrResize(200, 300));

    // ------------------------------------------------------------------ Box conversion

    [Fact]
    public void CxCyWhToXyXyCenter()
    {
        var box = TatrModel.CxCyWhToXyXy(0.5f, 0.5f, 0.5f, 0.5f, 100.0f, 100.0f);
        Assert.Equal(25.0f, box[0], 5);
        Assert.Equal(25.0f, box[1], 5);
        Assert.Equal(75.0f, box[2], 5);
        Assert.Equal(75.0f, box[3], 5);
    }

    [Fact]
    public void CxCyWhToXyXyTopLeft()
    {
        var box = TatrModel.CxCyWhToXyXy(0.5f, 0.5f, 1.0f, 1.0f, 200.0f, 100.0f);
        Assert.Equal(0.0f, box[0], 5);
        Assert.Equal(0.0f, box[1], 5);
        Assert.Equal(200.0f, box[2], 5);
        Assert.Equal(100.0f, box[3], 5);
    }

    [Fact]
    public void CxCyWhToXyXyClampsNegative()
    {
        var box = TatrModel.CxCyWhToXyXy(0.0f, 0.0f, 0.5f, 0.5f, 100.0f, 100.0f);
        Assert.Equal(0.0f, box[0]);
        Assert.Equal(0.0f, box[1]);
    }

    // ------------------------------------------------------------------ Softmax

    [Fact]
    public void SoftmaxArgmaxClearWinner()
    {
        var (index, probability) = TatrModel.SoftmaxArgmax(
            new[] { 0.0f, 0.0f, 10.0f, 0.0f, 0.0f, 0.0f, 0.0f }, 0, 7);
        Assert.Equal(2, index);
        Assert.True(probability > 0.99f);
    }

    [Fact]
    public void SoftmaxArgmaxUniform()
    {
        var (_, probability) = TatrModel.SoftmaxArgmax(Enumerable.Repeat(1.0f, 7).ToArray(), 0, 7);
        Assert.Equal(1.0f / 7.0f, probability, 5);
    }

    [Fact]
    public void SoftmaxArgmaxNegative()
    {
        var (index, _) = TatrModel.SoftmaxArgmax(
            new[] { -10.0f, -5.0f, -1.0f, -20.0f, -30.0f, -2.0f, -100.0f }, 0, 7);
        Assert.Equal(2, index);
    }

    // ------------------------------------------------------------------ IoB

    [Fact]
    public void IobFullContainment() =>
        Assert.Equal(1.0f, TatrModel.Iob([10, 10, 20, 20], [0, 0, 100, 100]), 5);

    [Fact]
    public void IobNoOverlap() => Assert.Equal(0.0f, TatrModel.Iob([0, 0, 10, 10], [20, 20, 30, 30]));

    [Fact]
    public void IobPartialOverlap() =>
        Assert.Equal(0.5f, TatrModel.Iob([0, 0, 10, 10], [5, 0, 15, 10]), 5);

    [Fact]
    public void IobZeroArea() => Assert.Equal(0.0f, TatrModel.Iob([5, 5, 5, 5], [0, 0, 10, 10]));

    // ------------------------------------------------------------------ NMS

    private static List<float[]> Nms(List<TatrDetection> detections, float threshold) =>
        TatrModel.NmsByIob(detections, detections.Select(d => d.Box).ToList(), threshold);

    [Fact]
    public void NmsSuppressesOverlapping()
    {
        var detections = new List<TatrDetection>
        {
            Row([0, 0, 100, 20], 0.9f),
            Row([0, 2, 100, 22], 0.7f),
        };
        var kept = Nms(detections, TatrModel.NmsIobThresholdRows);
        Assert.Single(kept);
        Assert.Equal(new[] { 0f, 0f, 100f, 20f }, kept[0]);
    }

    [Fact]
    public void NmsKeepsNonOverlapping()
    {
        var detections = new List<TatrDetection>
        {
            Row([0, 0, 100, 20], 0.9f),
            Row([0, 50, 100, 70], 0.8f),
        };
        Assert.Equal(2, Nms(detections, TatrModel.NmsIobThresholdRows).Count);
    }

    [Fact]
    public void NmsKeepsAdjacentRowsWithMinorOverlap()
    {
        var detections = new List<TatrDetection>
        {
            Row([0, 0, 100, 20], 0.9f),
            Row([0, 18, 100, 38], 0.8f),
        };
        Assert.Equal(2, Nms(detections, TatrModel.NmsIobThresholdRows).Count);
    }

    /// <summary>
    /// The column threshold is lower than the row one, which is exactly what lets two narrow
    /// adjacent columns be told apart from one duplicated column.
    /// </summary>
    [Fact]
    public void NmsColThresholdPreservesNarrowAdjacentColumns()
    {
        const float columnWidth = 20.0f;
        const float overlap = 7.0f;
        var detections = new List<TatrDetection>
        {
            Column([0, 0, columnWidth, 100], 0.9f),
            Column([columnWidth - overlap, 0, 2 * columnWidth - overlap, 100], 0.85f),
        };

        Assert.Equal(2, Nms(detections, TatrModel.NmsIobThresholdRows).Count);
        Assert.Single(Nms(detections, TatrModel.NmsIobThresholdCols));
    }

    [Fact]
    public void NmsColThresholdKeepsWellSeparatedColumns()
    {
        var detections = new List<TatrDetection>
        {
            Column([0, 0, 20, 100], 0.9f),
            Column([17, 0, 37, 100], 0.85f),
        };
        Assert.Equal(2, Nms(detections, TatrModel.NmsIobThresholdCols).Count);
    }

    // ------------------------------------------------------------------ Cell grid

    [Fact]
    public void BuildCellGrid2x2()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 0, 100, 20], 0.9f), Row([0, 20, 100, 40], 0.85f) },
            Columns = { Column([0, 0, 50, 40], 0.9f), Column([50, 0, 100, 40], 0.85f) },
        };

        var grid = TatrModel.BuildCellGrid(result, null);
        Assert.Equal(2, grid.Count);
        Assert.Equal(2, grid[0].Count);

        Assert.Equal(0.0f, grid[0][0].X1, 5);
        Assert.Equal(0.0f, grid[0][0].Y1, 5);
        Assert.Equal(50.0f, grid[0][0].X2, 5);
        Assert.Equal(20.0f, grid[0][0].Y2, 5);

        Assert.Equal(50.0f, grid[1][1].X1, 5);
        Assert.Equal(20.0f, grid[1][1].Y1, 5);
        Assert.Equal(100.0f, grid[1][1].X2, 5);
        Assert.Equal(40.0f, grid[1][1].Y2, 5);
    }

    [Fact]
    public void BuildCellGridEmpty() => Assert.Empty(TatrModel.BuildCellGrid(new TatrResult(), null));

    /// <summary>
    /// The table box, when present, is what rows are widened to — a more precise bound than the
    /// crop, and than the rows' own extent.
    /// </summary>
    [Fact]
    public void BuildCellGridWithTableBox()
    {
        var result = new TatrResult
        {
            Rows = { Row([10, 5, 90, 25], 0.9f) },
            Columns = { Column([0, 0, 50, 30], 0.9f) },
        };

        var grid = TatrModel.BuildCellGrid(result, [0, 0, 100, 30]);
        Assert.Single(grid);
        Assert.Single(grid[0]);
        Assert.Equal(0.0f, grid[0][0].X1, 5);
        Assert.Equal(50.0f, grid[0][0].X2, 5);
    }

    [Fact]
    public void TatrClassFromIndex()
    {
        Assert.Equal(TatrClass.Table, TatrModel.ClassFromIndex(0));
        Assert.Equal(TatrClass.Column, TatrModel.ClassFromIndex(1));
        Assert.Equal(TatrClass.Row, TatrModel.ClassFromIndex(2));
        Assert.Equal(TatrClass.ColumnHeader, TatrModel.ClassFromIndex(3));
        Assert.Equal(TatrClass.ProjectedRowHeader, TatrModel.ClassFromIndex(4));
        Assert.Equal(TatrClass.SpanningCell, TatrModel.ClassFromIndex(5));
        Assert.Null(TatrModel.ClassFromIndex(6));
        Assert.Null(TatrModel.ClassFromIndex(7));
    }

    /// <summary>
    /// The grid is ordered spatially, not by confidence: suppression ranks by confidence but the
    /// surviving bands are re-sorted by position.
    /// </summary>
    [Fact]
    public void BuildCellGridRowsSortedSpatially()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 30, 100, 50], 0.95f), Row([0, 0, 100, 20], 0.80f) },
            Columns = { Column([0, 0, 100, 50], 0.9f) },
        };

        var grid = TatrModel.BuildCellGrid(result, null);
        Assert.Equal(2, grid.Count);
        Assert.True(grid[0][0].Y1 < grid[1][0].Y1);
    }

    [Fact]
    public void BuildCellGridColumnsSortedSpatially()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 0, 100, 20], 0.9f) },
            Columns = { Column([60, 0, 100, 20], 0.95f), Column([0, 0, 50, 20], 0.80f) },
        };

        var grid = TatrModel.BuildCellGrid(result, null);
        Assert.Equal(2, grid[0].Count);
        Assert.True(grid[0][0].X1 < grid[0][1].X1);
    }

    [Fact]
    public void PreprocessDetrOutputShape()
    {
        var image = new RgbImage(640, 480);
        var (width, height) = TatrModel.ComputeDetrResize(image.Width, image.Height);
        Assert.Equal(1000, width);
        Assert.Equal(750, height);

        var destination = new float[3 * width * height];
        TatrModel.PreprocessDetr(image, destination, width, height);
        Assert.Equal(3 * 1000 * 750, destination.Length);
    }

    [Fact]
    public void MinColWidthFilterRemovesNoiseColumns()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 0, 100, 20], 0.9f) },
            Columns =
            {
                Column([0, 0, 50, 20], 0.9f),
                Column([60, 0, 60.5f, 20], 0.5f),
                Column([70, 0, 100, 20], 0.85f),
            },
        };

        var grid = TatrModel.BuildCellGrid(result, [0, 0, 100, 20]);
        Assert.Equal(2, grid[0].Count);
    }

    /// <summary>
    /// Two rows overlapping by 0.4 IoB both survive, because the row threshold is 0.5.
    /// </summary>
    [Fact]
    public void BuildCellGridUsesPerClassNms()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 0, 100, 25], 0.9f), Row([0, 15, 100, 40], 0.85f) },
            Columns = { Column([0, 0, 50, 40], 0.9f) },
        };

        Assert.Equal(2, TatrModel.BuildCellGrid(result, null).Count);
    }

    // ------------------------------------------------------------------ Structure

    [Fact]
    public void ComputeHeaderRowCountSingleHeaderRow()
    {
        var rows = new List<float[]> { new float[] {0, 0, 100, 20}, new float[] {0, 20, 100, 40} };
        var headers = new List<TatrDetection> { HeaderDetection([0, 0, 100, 20]) };
        Assert.Equal(1, TatrModel.ComputeHeaderRowCount(headers, rows));
    }

    [Fact]
    public void ComputeHeaderRowCountMultiRowHeader()
    {
        var rows = new List<float[]> { new float[] {0, 0, 100, 20}, new float[] {0, 20, 100, 40}, new float[] {0, 40, 100, 60} };
        var headers = new List<TatrDetection> { HeaderDetection([0, 0, 100, 40]) };
        Assert.Equal(2, TatrModel.ComputeHeaderRowCount(headers, rows));
    }

    /// <summary>
    /// No header detections means zero, which callers must not conflate with an explicit single
    /// header row.
    /// </summary>
    [Fact]
    public void ComputeHeaderRowCountNoHeadersReturnsZero() =>
        Assert.Equal(0, TatrModel.ComputeHeaderRowCount(
            new List<TatrDetection>(), new List<float[]> { new float[] {0, 0, 100, 20} }));

    [Fact]
    public void ComputeSpansMergesTwoColumns()
    {
        var rows = new List<float[]> { new float[] {0, 0, 100, 20} };
        var cols = new List<float[]> { new float[] {0, 0, 50, 20}, new float[] {50, 0, 100, 20} };
        var spanning = new List<TatrDetection> { SpanningDetection([0, 0, 100, 20]) };
        Assert.Equal([(0, 1, 0, 2)], TatrModel.ComputeSpans(spanning, rows, cols));
    }

    [Fact]
    public void ComputeSpansSkipsSingleCellOverlap()
    {
        var rows = new List<float[]> { new float[] {0, 0, 100, 20} };
        var cols = new List<float[]> { new float[] {0, 0, 50, 20}, new float[] {50, 0, 100, 20} };
        // Covers only the first column, so it describes no actual merge.
        var spanning = new List<TatrDetection> { SpanningDetection([0, 0, 50, 20]) };
        Assert.Empty(TatrModel.ComputeSpans(spanning, rows, cols));
    }

    [Fact]
    public void BuildCellGridWithStructureReportsHeaderAndSpan()
    {
        var result = new TatrResult
        {
            Rows = { Row([0, 0, 100, 20], 0.9f), Row([0, 20, 100, 40], 0.9f) },
            Columns = { Column([0, 0, 50, 40], 0.9f), Column([50, 0, 100, 40], 0.9f) },
            Headers = { HeaderDetection([0, 0, 100, 20]) },
            Spanning = { SpanningDetection([0, 0, 100, 20]) },
            TableBox = [0, 0, 100, 40],
        };

        var (grid, structure) = TatrModel.BuildCellGridWithStructure(result, result.TableBox);
        Assert.Equal(2, grid.Count);
        Assert.Equal(2, grid[0].Count);
        Assert.Equal(1, structure.HeaderRowCount);
        Assert.Equal([(0, 1, 0, 2)], structure.Spans);
    }
}

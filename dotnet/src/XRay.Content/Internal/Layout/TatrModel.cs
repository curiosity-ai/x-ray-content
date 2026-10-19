using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>The seven object classes TATR predicts.</summary>
/// <remarks>
/// Class 6, <c>NoObject</c>, is the background class DETR uses to mean "this query found
/// nothing"; it has no member here because it is filtered out rather than carried.
/// </remarks>
internal enum TatrClass
{
    Table,
    Column,
    Row,
    ColumnHeader,
    ProjectedRowHeader,
    SpanningCell,
}

/// <summary>One TATR detection, in the crop's own pixel coordinates.</summary>
internal sealed record TatrDetection(float X1, float Y1, float X2, float Y2, float Confidence, TatrClass ClassName)
{
    public float[] Box => [X1, Y1, X2, Y2];
}

/// <summary>A cell rectangle within the reconstructed grid, in crop-pixel coordinates.</summary>
internal readonly record struct CellBBox(float X1, float Y1, float X2, float Y2);

/// <summary>TATR detections separated by class.</summary>
internal sealed class TatrResult
{
    /// <summary>Detected rows, sorted top to bottom by their lower edge.</summary>
    public List<TatrDetection> Rows { get; init; } = new();

    /// <summary>Detected columns, sorted left to right by their right edge.</summary>
    public List<TatrDetection> Columns { get; init; } = new();

    /// <summary>Column-header and projected-row-header detections.</summary>
    public List<TatrDetection> Headers { get; init; } = new();

    /// <summary>Spanning-cell detections.</summary>
    public List<TatrDetection> Spanning { get; init; } = new();

    /// <summary>
    /// The model's own highest-confidence <c>Table</c> box, or <c>null</c> if it found none.
    /// </summary>
    /// <remarks>
    /// A more precise localisation of the table than the crop's full extent, which is what lets a
    /// caller widen rows to the table rather than to the crop.
    /// </remarks>
    public float[]? TableBox { get; set; }
}

/// <summary>Header rows and merged spans derived from a cell grid.</summary>
internal sealed record TableStructure(int HeaderRowCount, List<(int RowStart, int RowEnd, int ColStart, int ColEnd)> Spans)
{
    public static TableStructure Empty => new(0, new List<(int, int, int, int)>());
}

/// <summary>
/// TATR (Table Transformer) table structure recognition, ported from Rust
/// <c>layout::models::tatr</c>.
/// </summary>
/// <remarks>
/// <para>
/// A DETR-based detector that predicts every structural element of a cropped table in one forward
/// pass — rows, columns, headers and spanning cells — which the grid builder then intersects into
/// cells.
/// </para>
/// <para>
/// Graph contract: input <c>pixel_values</c> f32 <c>[batch,3,H,W]</c> at a variable size set by
/// DETR preprocessing; outputs <c>logits</c> <c>[batch,125,7]</c> and <c>pred_boxes</c>
/// <c>[batch,125,4]</c> holding normalised <c>(cx, cy, w, h)</c>.
/// </para>
/// </remarks>
internal sealed class TatrModel
{
    /// <summary>DETR's standard shortest-edge target.</summary>
    private const int DetrShortEdge = 800;

    /// <summary>DETR's standard longest-edge cap.</summary>
    private const int DetrLongEdge = 1000;

    /// <summary>ImageNet normalisation mean, in RGB channel order.</summary>
    private static readonly float[] ImagenetMeanRgb = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet normalisation standard deviation, in RGB channel order.</summary>
    private static readonly float[] ImagenetStdRgb = { 0.229f, 0.224f, 0.225f };

    /// <summary>Output classes, including the background class.</summary>
    internal const int NumClasses = 7;

    /// <summary>Confidence threshold for row and column detections.</summary>
    private const float ConfThresholdRowCol = 0.3f;

    /// <summary>Confidence threshold for spanning-cell detections.</summary>
    private const float ConfThresholdSpanning = 0.5f;

    /// <summary>
    /// IoB threshold for suppressing duplicate <em>row</em> detections.
    /// </summary>
    /// <remarks>
    /// TATR's row predictions routinely overlap by a few pixels, so a tight threshold suppresses
    /// valid rows and merges their content. Half means "suppress only when the majority of the
    /// candidate is already covered", which removes true duplicates while keeping close but
    /// distinct rows.
    /// </remarks>
    internal const float NmsIobThresholdRows = 0.5f;

    /// <summary>
    /// IoB threshold for suppressing duplicate <em>column</em> detections.
    /// </summary>
    /// <remarks>
    /// Lower than for rows: narrow adjacent columns — quarter headers, say — overlap
    /// substantially relative to their own small width, and would otherwise be merged.
    /// </remarks>
    internal const float NmsIobThresholdCols = 0.3f;

    /// <summary>Minimum column width as a fraction of the table width.</summary>
    /// <remarks>Below this a column is noise that would split the grid wrongly.</remarks>
    internal const float MinColWidthFrac = 0.01f;

    /// <summary>
    /// Overlap fraction at which a grid row or column counts as belonging to a header or
    /// spanning detection: more than half of it must be covered.
    /// </summary>
    internal const float StructureIobThreshold = 0.5f;

    private readonly OnnxSession _session;
    private readonly string _inputName;

    private TatrModel(OnnxSession session, string inputName)
    {
        _session = session;
        _inputName = inputName;
    }

    public static TatrModel FromFile(string path) => Create(OnnxModel.Load(path));

    public static TatrModel FromBytes(ReadOnlySpan<byte> modelBytes) => Create(OnnxModel.Parse(modelBytes));

    private static TatrModel Create(OnnxModel model)
    {
        var inputs = model.FeedInputs.Select(input => input.Name).ToArray();
        if (inputs.Length < 1) throw new InvalidDataException("TATR model has no inputs");
        return new TatrModel(new OnnxSession(model), inputs[0]);
    }

    internal static TatrClass? ClassFromIndex(int index) => index switch
    {
        0 => TatrClass.Table,
        1 => TatrClass.Column,
        2 => TatrClass.Row,
        3 => TatrClass.ColumnHeader,
        4 => TatrClass.ProjectedRowHeader,
        5 => TatrClass.SpanningCell,
        _ => null,
    };

    /// <summary>Recognise table structure from a cropped table image.</summary>
    public TatrResult Recognize(RgbImage tableImage)
    {
        float imageWidth = tableImage.Width;
        float imageHeight = tableImage.Height;

        var (resizedWidth, resizedHeight) = ComputeDetrResize(tableImage.Width, tableImage.Height);
        var input = Tensor.AllocateFloat(1, 3, resizedHeight, resizedWidth);
        PreprocessDetr(tableImage, input.Floats, resizedWidth, resizedHeight);

        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [_inputName] = input,
        });

        var floatOutputs = outputs.Values.Where(t => t.IsFloat).ToList();
        if (floatOutputs.Count < 2)
            throw new InvalidDataException($"TATR expected 2 float outputs, got {floatOutputs.Count}");

        // The two outputs are told apart by shape rather than by order: `logits` is the one whose
        // last dimension is the class count.
        Tensor logits, boxes;
        if (floatOutputs[0].Shape[^1] == NumClasses) { logits = floatOutputs[0]; boxes = floatOutputs[1]; }
        else { boxes = floatOutputs[0]; logits = floatOutputs[1]; }

        int queries = logits.Shape.Length > 1 ? logits.Shape[1] : 0;
        int classes = logits.Shape[^1];
        int boxDim = boxes.Shape[^1];

        var result = new TatrResult();
        if (queries == 0 || classes < NumClasses || boxDim < 4) return result;

        float tableConfidence = 0.0f;
        for (int q = 0; q < queries; q++)
        {
            var (classIndex, confidence) = SoftmaxArgmax(logits.Floats, q * classes, classes);
            if (ClassFromIndex(classIndex) is not { } layoutClass) continue;

            float threshold = layoutClass == TatrClass.SpanningCell
                ? ConfThresholdSpanning
                : ConfThresholdRowCol;
            if (confidence < threshold) continue;

            int boxOffset = q * boxDim;
            var box = CxCyWhToXyXy(
                boxes.Floats[boxOffset], boxes.Floats[boxOffset + 1],
                boxes.Floats[boxOffset + 2], boxes.Floats[boxOffset + 3],
                resizedWidth, resizedHeight);

            // Boxes come back in the resized frame; scaling maps them onto the crop.
            float scaleX = imageWidth / resizedWidth;
            float scaleY = imageHeight / resizedHeight;
            var detection = new TatrDetection(
                Math.Clamp(box[0] * scaleX, 0.0f, imageWidth),
                Math.Clamp(box[1] * scaleY, 0.0f, imageHeight),
                Math.Clamp(box[2] * scaleX, 0.0f, imageWidth),
                Math.Clamp(box[3] * scaleY, 0.0f, imageHeight),
                confidence, layoutClass);

            switch (layoutClass)
            {
                case TatrClass.Row: result.Rows.Add(detection); break;
                case TatrClass.Column: result.Columns.Add(detection); break;
                case TatrClass.ColumnHeader:
                case TatrClass.ProjectedRowHeader: result.Headers.Add(detection); break;
                case TatrClass.SpanningCell: result.Spanning.Add(detection); break;
                case TatrClass.Table:
                    if (detection.Confidence > tableConfidence)
                    {
                        tableConfidence = detection.Confidence;
                        result.TableBox = detection.Box;
                    }
                    break;
            }
        }

        ReadingOrder.StableSort(result.Rows, (a, b) => ReadingOrder.TotalCmp(a.Y2, b.Y2));
        ReadingOrder.StableSort(result.Columns, (a, b) => ReadingOrder.TotalCmp(a.X2, b.X2));
        return result;
    }

    /// <summary>
    /// DETR-standard preprocessing: aspect-preserving resize, then ImageNet normalisation in RGB
    /// channel order.
    /// </summary>
    /// <remarks>
    /// RGB here, unlike the PaddleOCR models' BGR — the two families normalise through different
    /// toolchains and the constants are not interchangeable.
    /// </remarks>
    internal static void PreprocessDetr(
        RgbImage image, float[] destination, int newWidth, int newHeight, int offset = 0)
    {
        var resized = image.ResizeTriangle(newWidth, newHeight);

        int plane = newWidth * newHeight;
        const float inv255 = 1.0f / 255.0f;
        float invStdR = 1.0f / ImagenetStdRgb[0];
        float invStdG = 1.0f / ImagenetStdRgb[1];
        float invStdB = 1.0f / ImagenetStdRgb[2];

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = offset + y * newWidth;
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    destination[rowOffset + x] = (pixel.R * inv255 - ImagenetMeanRgb[0]) * invStdR;
                    destination[plane + rowOffset + x] = (pixel.G * inv255 - ImagenetMeanRgb[1]) * invStdG;
                    destination[2 * plane + rowOffset + x] = (pixel.B * inv255 - ImagenetMeanRgb[2]) * invStdB;
                }
            }
        });
    }

    /// <summary>
    /// DETR resize dimensions: scale the shortest edge to 800, then cap the longest at 1000.
    /// </summary>
    /// <remarks>
    /// This matches Hugging Face's <c>get_resize_output_image_size</c> exactly, including the
    /// order of operations: the tentative long edge is computed and truncated <em>first</em>, and
    /// that truncated value is what the cap is applied to. Collapsing it into a single ratio
    /// drifts by a pixel for some dimensions, which moves every predicted box.
    /// </remarks>
    internal static (int Width, int Height) ComputeDetrResize(int originalWidth, int originalHeight)
    {
        long shortEdge = Math.Min(originalWidth, originalHeight);
        long longEdge = Math.Max(originalWidth, originalHeight);
        if (shortEdge == 0) return (Math.Max(originalWidth, 1), Math.Max(originalHeight, 1));

        long requestedShort = DetrShortEdge;
        long requestedLong = requestedShort * longEdge / shortEdge;

        long newShort, newLong;
        if (requestedLong > DetrLongEdge)
        {
            newShort = (long)DetrLongEdge * requestedShort / requestedLong;
            newLong = DetrLongEdge;
        }
        else
        {
            newShort = requestedShort;
            newLong = requestedLong;
        }

        return originalWidth <= originalHeight
            ? ((int)Math.Max(newShort, 1), (int)Math.Max(newLong, 1))
            : ((int)Math.Max(newLong, 1), (int)Math.Max(newShort, 1));
    }

    /// <summary>Softmax over a span of logits, returning the argmax index and its probability.</summary>
    internal static (int Index, float Probability) SoftmaxArgmax(float[] logits, int offset, int count)
    {
        float max = float.NegativeInfinity;
        for (int i = 0; i < count; i++) max = ReadingOrderText.RustMax(max, logits[offset + i]);

        var probabilities = new float[count];
        float sum = 0.0f;
        for (int i = 0; i < count; i++)
        {
            float e = MathF.Exp(logits[offset + i] - max);
            probabilities[i] = e;
            sum += e;
        }

        float invSum = 1.0f / sum;
        int bestIndex = 0;
        float bestProbability = 0.0f;
        for (int i = 0; i < count; i++)
        {
            float probability = probabilities[i] * invSum;
            if (probability > bestProbability) { bestProbability = probability; bestIndex = i; }
        }
        return (bestIndex, bestProbability);
    }

    /// <summary>
    /// Convert a normalised centre-format box to pixel <c>[x1, y1, x2, y2]</c>.
    /// </summary>
    internal static float[] CxCyWhToXyXy(float cx, float cy, float w, float h, float imageWidth, float imageHeight)
    {
        float pixelCx = cx * imageWidth, pixelCy = cy * imageHeight;
        float pixelW = w * imageWidth, pixelH = h * imageHeight;
        return
        [
            MathF.Max(pixelCx - pixelW / 2.0f, 0.0f),
            MathF.Max(pixelCy - pixelH / 2.0f, 0.0f),
            MathF.Max(pixelCx + pixelW / 2.0f, 0.0f),
            MathF.Max(pixelCy + pixelH / 2.0f, 0.0f),
        ];
    }

    /// <summary>
    /// Intersection-over-box: the fraction of <paramref name="a"/> that <paramref name="b"/>
    /// covers.
    /// </summary>
    /// <remarks>
    /// Not IoU. A row that a much larger row fully contains has IoB 1 but a small IoU, and it is
    /// containment, not mutual similarity, that makes a duplicate here.
    /// </remarks>
    internal static float Iob(float[] a, float[] b)
    {
        float areaA = MathF.Max(a[2] - a[0], 0.0f) * MathF.Max(a[3] - a[1], 0.0f);
        if (areaA <= 0.0f) return 0.0f;

        float intersection =
            MathF.Max(MathF.Min(a[2], b[2]) - MathF.Max(a[0], b[0]), 0.0f) *
            MathF.Max(MathF.Min(a[3], b[3]) - MathF.Max(a[1], b[1]), 0.0f);
        return intersection / areaA;
    }

    /// <summary>Build a cell grid from TATR detections.</summary>
    public static List<List<CellBBox>> BuildCellGrid(TatrResult result, float[]? tableBox) =>
        BuildCellGridWithStructure(result, tableBox).Grid;

    /// <summary>
    /// Build the cell grid and the header/span structure that goes with it.
    /// </summary>
    /// <remarks>
    /// Rows are widened to the full table width before suppression, so two rows that differ only
    /// in how far their text happens to extend are recognised as the same row. Columns are not
    /// widened, because their horizontal extent is exactly what distinguishes them.
    /// </remarks>
    public static (List<List<CellBBox>> Grid, TableStructure Structure) BuildCellGridWithStructure(
        TatrResult result, float[]? tableBox)
    {
        if (result.Rows.Count == 0 || result.Columns.Count == 0)
            return (new List<List<CellBBox>>(), TableStructure.Empty);

        float tableX1, tableX2;
        if (tableBox is { } box) { tableX1 = box[0]; tableX2 = box[2]; }
        else
        {
            tableX1 = float.PositiveInfinity;
            tableX2 = float.NegativeInfinity;
            foreach (var row in result.Rows)
            {
                tableX1 = ReadingOrderText.RustMin(tableX1, row.X1);
                tableX2 = ReadingOrderText.RustMax(tableX2, row.X2);
            }
        }

        var widenedRows = result.Rows.Select(r => new[] { tableX1, r.Y1, tableX2, r.Y2 }).ToList();
        var nmsRows = NmsByIob(result.Rows, widenedRows, NmsIobThresholdRows);
        ReadingOrder.StableSort(nmsRows, (a, b) => ReadingOrder.TotalCmp(a[1], b[1]));

        var columnBoxes = result.Columns.Select(c => c.Box).ToList();
        var nmsCols = NmsByIob(result.Columns, columnBoxes, NmsIobThresholdCols);

        float tableWidth = tableX2 - tableX1;
        if (tableWidth > 0.0f)
        {
            float minColumnWidth = tableWidth * MinColWidthFrac;
            nmsCols.RemoveAll(col => col[2] - col[0] < minColumnWidth);
        }

        ReadingOrder.StableSort(nmsCols, (a, b) => ReadingOrder.TotalCmp(a[0], b[0]));

        var grid = new List<List<CellBBox>>(nmsRows.Count);
        foreach (var rowBox in nmsRows)
        {
            var cells = new List<CellBBox>(nmsCols.Count);
            foreach (var columnBox in nmsCols) cells.Add(IntersectBoxes(rowBox, columnBox));
            grid.Add(cells);
        }

        var structure = new TableStructure(
            ComputeHeaderRowCount(result.Headers, nmsRows),
            ComputeSpans(result.Spanning, nmsRows, nmsCols));
        return (grid, structure);
    }

    /// <summary>
    /// The fraction of the extent <c>[lo, hi]</c> that <c>[otherLo, otherHi]</c> covers.
    /// </summary>
    /// <remarks>
    /// Row bands are widened to the full table width and column bands span the full height, but
    /// the perpendicular axis is not — a header or spanning detection typically covers only part
    /// of a column's height. Projecting onto each band's single perpendicular axis keeps that
    /// mismatch from swamping the overlap fraction, which a 2-D IoB would not.
    /// </remarks>
    internal static float AxisOverlapFraction(float lo, float hi, float otherLo, float otherHi)
    {
        float extent = hi - lo;
        if (extent <= 0.0f) return 0.0f;
        float overlap = MathF.Max(MathF.Min(hi, otherHi) - MathF.Max(lo, otherLo), 0.0f);
        return overlap / extent;
    }

    /// <summary>
    /// Count the leading grid rows a header detection covers, stopping at the first that it does
    /// not.
    /// </summary>
    /// <remarks>
    /// A single leading run is the right reading even for a multi-row header, because TATR's
    /// header rows are always the topmost rows of the table.
    /// </remarks>
    internal static int ComputeHeaderRowCount(List<TatrDetection> headers, List<float[]> rows)
    {
        int count = 0;
        foreach (var row in rows)
        {
            bool isHeader = headers.Any(h =>
                AxisOverlapFraction(row[1], row[3], h.Y1, h.Y2) > StructureIobThreshold);
            if (!isHeader) break;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Map each spanning detection onto the grid rectangle it covers.
    /// </summary>
    /// <remarks>
    /// A detection resolving to a single cell is dropped: it describes no actual merge.
    /// </remarks>
    internal static List<(int RowStart, int RowEnd, int ColStart, int ColEnd)> ComputeSpans(
        List<TatrDetection> spanning, List<float[]> rows, List<float[]> cols)
    {
        var spans = new List<(int, int, int, int)>();
        foreach (var span in spanning)
        {
            var rowIndices = new List<int>();
            for (int i = 0; i < rows.Count; i++)
                if (AxisOverlapFraction(rows[i][1], rows[i][3], span.Y1, span.Y2) > StructureIobThreshold)
                    rowIndices.Add(i);

            var colIndices = new List<int>();
            for (int i = 0; i < cols.Count; i++)
                if (AxisOverlapFraction(cols[i][0], cols[i][2], span.X1, span.X2) > StructureIobThreshold)
                    colIndices.Add(i);

            if (rowIndices.Count * colIndices.Count <= 1) continue;
            spans.Add((rowIndices[0], rowIndices[^1] + 1, colIndices[0], colIndices[^1] + 1));
        }
        return spans;
    }

    /// <summary>
    /// Greedy non-maximum suppression on the IoB metric: keep detections in descending confidence
    /// order, dropping any whose IoB with an already-kept box exceeds the threshold.
    /// </summary>
    internal static List<float[]> NmsByIob(
        List<TatrDetection> detections, List<float[]> boxes, float threshold)
    {
        var indices = Enumerable.Range(0, detections.Count).ToList();
        ReadingOrder.StableSort(indices,
            (a, b) => ReadingOrder.TotalCmp(detections[b].Confidence, detections[a].Confidence));

        var kept = new List<float[]>();
        foreach (int index in indices)
        {
            var candidate = boxes[index];
            if (kept.Any(keptBox => Iob(candidate, keptBox) > threshold)) continue;
            kept.Add(candidate);
        }
        return kept;
    }

    /// <summary>
    /// The intersection rectangle of two boxes. Non-overlapping boxes give a zero-area cell
    /// rather than an error.
    /// </summary>
    internal static CellBBox IntersectBoxes(float[] a, float[] b) => new(
        MathF.Max(a[0], b[0]), MathF.Max(a[1], b[1]),
        MathF.Min(a[2], b[2]), MathF.Min(a[3], b[3]));
}

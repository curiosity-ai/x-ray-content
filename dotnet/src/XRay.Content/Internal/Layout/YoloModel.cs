using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>Which YOLO family a model file belongs to.</summary>
/// <remarks>
/// The three differ in what comes out of the graph, not in what goes in: one emits decoded
/// boxes, one emits centre-format boxes with per-class scores, and one emits raw anchor
/// predictions that mean nothing until they are placed on their grid.
/// </remarks>
internal enum YoloVariant
{
    /// <summary>
    /// YOLOv10/v8 trained on DocLayNet, eleven classes.
    /// Output <c>[batch, detections, 6]</c> = x1, y1, x2, y2, score, class.
    /// </summary>
    DocLayNet,

    /// <summary>
    /// DocLayout-YOLO trained on DocStructBench, ten classes.
    /// Output <c>[batch, detections, 4 + classes]</c> in centre format, or <c>[.., 6]</c> decoded.
    /// </summary>
    DocStructBench,

    /// <summary>
    /// YOLOX, letterboxed input and grid-decoded output.
    /// Output <c>[batch, anchors, 5 + classes]</c>, which needs decoding and then suppression.
    /// </summary>
    Yolox,
}

/// <summary>
/// YOLO-family layout detection, running on the in-process ONNX runtime.
/// </summary>
/// <remarks>
/// <para>
/// Ports Rust <c>layout::models::yolo</c>. Unlike the transformer detectors, these models are
/// anchor-based: nothing in the graph removes a duplicate, so a box that two anchors both found
/// comes out twice and non-maximum suppression is part of reading the output rather than an
/// optional tidy-up.
/// </para>
/// <para>
/// The exception is YOLOv10, whose export folds suppression into the graph — which is why the
/// six-column output path below runs no suppression of its own and the wider one does.
/// </para>
/// </remarks>
internal sealed class YoloModel
{
    /// <summary>Confidence below which a detection is discarded.</summary>
    public const float DefaultThreshold = 0.35f;

    /// <summary>Overlap above which the weaker of two detections is suppressed.</summary>
    private const float NmsIouThreshold = 0.45f;

    /// <summary>The feature-map strides YOLOX decodes its anchors against.</summary>
    private static readonly int[] YoloxStrides = { 8, 16, 32 };

    private readonly OnnxSession _session;
    private readonly string _input;
    private readonly YoloVariant _variant;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly string _name;

    private YoloModel(
        OnnxSession session, string input, YoloVariant variant,
        int inputWidth, int inputHeight, string name)
    {
        _session = session;
        _input = input;
        _variant = variant;
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _name = name;
    }

    /// <summary>
    /// Load a YOLO model.
    /// </summary>
    /// <remarks>
    /// The square-input models pass the same value twice; YOLOX is trained on a portrait input
    /// and needs both, because its grid decoding depends on the two separately.
    /// </remarks>
    public static YoloModel FromFile(
        string path, YoloVariant variant, int inputWidth, int inputHeight, string name) =>
        Create(OnnxModel.Load(path), variant, inputWidth, inputHeight, name);

    public static YoloModel FromBytes(
        ReadOnlySpan<byte> modelBytes, YoloVariant variant, int inputWidth, int inputHeight, string name) =>
        Create(OnnxModel.Parse(modelBytes), variant, inputWidth, inputHeight, name);

    private static YoloModel Create(
        OnnxModel model, YoloVariant variant, int inputWidth, int inputHeight, string name)
    {
        var inputs = model.FeedInputs.Select(i => i.Name).ToArray();
        if (inputs.Length < 1)
            throw new InvalidDataException("YOLO model declares no inputs");
        return new YoloModel(
            new OnnxSession(model), inputs[0], variant, inputWidth, inputHeight, name);
    }

    public string Name => _name;

    /// <summary>Detect layout regions in a page image.</summary>
    public IReadOnlyList<LayoutDetection> Detect(RgbImage page, float threshold = DefaultThreshold) =>
        _variant == YoloVariant.Yolox
            ? DetectYolox(page, threshold)
            : DetectDecoded(page, threshold);

    // ------------------------------------------------------------------ YOLOv10 / DocLayout-YOLO

    /// <summary>
    /// Read an output that is already in image coordinates.
    /// </summary>
    /// <remarks>
    /// Two shapes reach here. Six columns is a decoded detection, one per row, and the export
    /// has already suppressed its own duplicates. Anything wider is centre-format geometry
    /// followed by one score per class, which still has both a winner to pick and duplicates to
    /// remove.
    /// </remarks>
    private IReadOnlyList<LayoutDetection> DetectDecoded(RgbImage page, float threshold)
    {
        var input = Tensor.AllocateFloat(1, 3, _inputHeight, _inputWidth);
        LayoutPreprocessing.PreprocessRescale(page, input.Floats, _inputWidth);

        var output = RunSingleOutput(input);
        var (rows, columns) = OutputLayout(output);

        // The preprocessing squashes the page into the model's input without preserving its
        // aspect ratio, so the two axes scale back independently.
        float scaleX = (float)page.Width / _inputWidth;
        float scaleY = (float)page.Height / _inputHeight;

        var detections = new List<LayoutDetection>();

        if (columns == 6)
        {
            for (int i = 0; i < rows; i++)
            {
                int offset = i * 6;
                float score = output.GetFloat(offset + 4);
                if (score < threshold) continue;

                if (MapClass((long)output.GetFloat(offset + 5)) is not { } layoutClass) continue;

                detections.Add(new LayoutDetection(layoutClass, score, new BBox(
                    output.GetFloat(offset) * scaleX,
                    output.GetFloat(offset + 1) * scaleY,
                    output.GetFloat(offset + 2) * scaleX,
                    output.GetFloat(offset + 3) * scaleY)));
            }
        }
        else if (columns > 4)
        {
            int classes = columns - 4;
            for (int i = 0; i < rows; i++)
            {
                int offset = i * columns;
                float centreX = output.GetFloat(offset);
                float centreY = output.GetFloat(offset + 1);
                float width = output.GetFloat(offset + 2);
                float height = output.GetFloat(offset + 3);

                var (score, classId) = BestClass(output, offset + 4, classes);
                if (score < threshold) continue;
                if (MapClass(classId) is not { } layoutClass) continue;

                detections.Add(new LayoutDetection(layoutClass, score, new BBox(
                    (centreX - width / 2f) * scaleX,
                    (centreY - height / 2f) * scaleY,
                    (centreX + width / 2f) * scaleX,
                    (centreY + height / 2f) * scaleY)));
            }

            detections = LayoutPostprocessing.GreedyNms(detections, NmsIouThreshold);
        }

        return LayoutPostprocessing.SortByConfidenceDesc(detections);
    }

    // ------------------------------------------------------------------ YOLOX

    /// <summary>
    /// Read a YOLOX output, which is raw anchor predictions rather than boxes.
    /// </summary>
    /// <remarks>
    /// Each row is an offset from the anchor's own grid cell: the centre is where the cell is
    /// plus what the model predicted, times the stride, and the size is exponential so it can
    /// never go negative. Reading the row as a box gives a picture of tiny boxes clustered at
    /// the top left.
    /// </remarks>
    private IReadOnlyList<LayoutDetection> DetectYolox(RgbImage page, float threshold)
    {
        var input = Tensor.AllocateFloat(1, 3, _inputHeight, _inputWidth);
        float scale = LayoutPreprocessing.PreprocessLetterbox(page, input.Floats, _inputWidth, _inputHeight);

        var output = RunSingleOutput(input);
        var (rows, columns) = OutputLayout(output);
        if (columns <= 5)
            throw new InvalidDataException(
                $"YOLOX output must carry geometry, objectness and classes, found {columns} columns");
        int classes = columns - 5;

        var grid = BuildYoloxGrid(_inputWidth, _inputHeight);
        if (grid.Count != rows)
            throw new InvalidDataException(
                $"YOLOX grid anchor count mismatch: {grid.Count} from the strides, {rows} from the model");

        var detections = new List<LayoutDetection>();
        for (int i = 0; i < rows; i++)
        {
            int offset = i * columns;
            var (gridX, gridY, stride) = grid[i];

            float centreX = (output.GetFloat(offset) + gridX) * stride;
            float centreY = (output.GetFloat(offset + 1) + gridY) * stride;
            float width = MathF.Exp(output.GetFloat(offset + 2)) * stride;
            float height = MathF.Exp(output.GetFloat(offset + 3)) * stride;

            float objectness = output.GetFloat(offset + 4);
            var (classScore, classId) = BestClass(output, offset + 5, classes);

            // An anchor is only as good as its worse half: a confident class on a cell holding
            // no object, or an object whose class is a guess, are both weak detections.
            float confidence = objectness * classScore;
            if (confidence < threshold) continue;
            if (MapClass(classId) is not { } layoutClass) continue;

            // The letterbox scaled the page down by one factor on both axes, so one divide
            // puts the box back on the page.
            detections.Add(new LayoutDetection(layoutClass, confidence, new BBox(
                (centreX - width / 2f) / scale,
                (centreY - height / 2f) / scale,
                (centreX + width / 2f) / scale,
                (centreY + height / 2f) / scale)));
        }

        detections = LayoutPostprocessing.GreedyNms(detections, NmsIouThreshold);
        return LayoutPostprocessing.SortByConfidenceDesc(detections);
    }

    /// <summary>
    /// Every anchor's grid position and stride, in the order the model emits them.
    /// </summary>
    /// <remarks>
    /// The order is what ties a row of the output to a place on the page, so it has to be the
    /// same walk the export used: coarsest stride last, and row-major within each level.
    /// </remarks>
    internal static List<(float GridX, float GridY, float Stride)> BuildYoloxGrid(
        int inputWidth, int inputHeight)
    {
        var grid = new List<(float, float, float)>();
        foreach (int stride in YoloxStrides)
        {
            int rows = inputHeight / stride;
            int columns = inputWidth / stride;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                    grid.Add((x, y, stride));
        }
        return grid;
    }

    // ------------------------------------------------------------------ Shared

    /// <summary>The highest-scoring class among <paramref name="count"/> scores, and its index.</summary>
    internal static (float Score, long ClassId) BestClass(Tensor output, int offset, int count)
    {
        float best = 0f;
        long bestId = 0;
        for (int c = 0; c < count; c++)
        {
            float score = output.GetFloat(offset + c);
            if (score > best)
            {
                best = score;
                bestId = c;
            }
        }
        return (best, bestId);
    }

    /// <summary>
    /// How many detections the output holds and how wide each one is.
    /// </summary>
    /// <remarks>
    /// Exports disagree on whether the batch dimension survives tracing, so a rank-2 output is
    /// the same thing with the leading 1 dropped rather than a different contract.
    /// </remarks>
    internal static (int Rows, int Columns) OutputLayout(Tensor output) => output.Rank switch
    {
        >= 3 => (output.Shape[1], output.Shape[2]),
        2 => (output.Shape[0], output.Shape[1]),
        _ => throw new InvalidDataException(
            $"Unexpected YOLO output rank {output.Rank}; expected a 2- or 3-dimensional tensor"),
    };

    /// <summary>Map a model's own class number onto the shared taxonomy.</summary>
    /// <remarks>
    /// A number this build does not model is dropped rather than folded into a neighbour: a
    /// wrong class reads as a confident detection of the wrong thing, and a missing one reads
    /// as what it is.
    /// </remarks>
    private LayoutClass? MapClass(long id) => _variant switch
    {
        YoloVariant.DocStructBench => LayoutClassExtensions.FromDocStructBenchId(id),
        _ => LayoutClassExtensions.FromDocLayNetId(id),
    };

    private Tensor RunSingleOutput(Tensor input)
    {
        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [_input] = input,
        });

        foreach (string name in _session.OutputNames)
            if (outputs.TryGetValue(name, out var tensor))
                return tensor;

        throw new InvalidDataException("YOLO model produced no output tensors");
    }
}

using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>
/// PP-DocLayout-V3 layout detection, ported from Rust <c>layout::models::pp_doclayout_v3</c>.
/// </summary>
/// <remarks>
/// <para>
/// A PaddleDetection DETR export over a 25-class document taxonomy, run at 800x800. The 25
/// classes fold onto this port's shared <see cref="LayoutClass"/> set, several of them many-to-one
/// — display, inline and numbered formulas all become <see cref="LayoutClass.Formula"/>.
/// </para>
/// <para>
/// Graph contract, which the preprocessing must match exactly: inputs <c>image</c> f32
/// <c>[batch,3,800,800]</c>, <c>im_shape</c> f32 <c>[batch,2]</c> and <c>scale_factor</c> f32
/// <c>[batch,2]</c>; outputs <c>fetch_name_0</c> f32 <c>[N,7]</c> holding
/// <c>[class_id, score, x1, y1, x2, y2, unused]</c> and <c>fetch_name_1</c> i32 <c>[batch]</c>
/// holding each image's detection count.
/// </para>
/// <para>
/// The two scalar inputs follow PaddleDetection's convention, which is the easy thing to get
/// wrong: <c>im_shape</c> is the <em>resized</em> tensor size — always 800x800 — and not the
/// original page size, and <c>scale_factor</c> is <c>[800/origH, 800/origW]</c>. The model divides
/// its output coordinates by the scale factor itself, so boxes arrive already in original-image
/// pixel space. Passing the original size in <c>im_shape</c> makes the coordinates overflow the
/// page by roughly the scale factor.
/// </para>
/// </remarks>
internal sealed class PpDocLayoutV3Model
{
    /// <summary>Confidence below which a detection is discarded.</summary>
    public const float DefaultThreshold = 0.5f;

    /// <summary>The square resolution the export was traced at.</summary>
    public const int InputSize = 800;

    /// <summary>Columns in each <c>fetch_name_0</c> row.</summary>
    private const int DetRowCols = 7;

    private const int ColClass = 0;
    private const int ColScore = 1;
    private const int ColX1 = 2;
    private const int ColY1 = 3;
    private const int ColX2 = 4;
    private const int ColY2 = 5;

    private readonly OnnxSession _session;
    private readonly string[] _inputNames;

    private PpDocLayoutV3Model(OnnxSession session, string[] inputNames)
    {
        _session = session;
        _inputNames = inputNames;
    }

    public static PpDocLayoutV3Model FromFile(string path) => Create(OnnxModel.Load(path));

    public static PpDocLayoutV3Model FromBytes(ReadOnlySpan<byte> modelBytes) => Create(OnnxModel.Parse(modelBytes));

    private static PpDocLayoutV3Model Create(OnnxModel model) =>
        new(new OnnxSession(model), model.FeedInputs.Select(input => input.Name).ToArray());

    public string Name => "PP-DocLayout-V3";

    /// <summary>
    /// Map a PP-DocLayout-V3 class ID onto the shared taxonomy, or <c>null</c> for one this port
    /// does not model.
    /// </summary>
    /// <remarks>
    /// Class 1 is <c>algorithm</c>, which maps to <see cref="LayoutClass.Code"/>: an algorithm
    /// block is preformatted text and reads correctly only if its line breaks survive.
    /// </remarks>
    internal static LayoutClass? ClassFromId(long id) => id switch
    {
        1 => LayoutClass.Code,
        3 => LayoutClass.Chart,
        4 => LayoutClass.DocumentIndex,
        5 or 11 or 15 => LayoutClass.Formula,
        6 or 17 => LayoutClass.Title,
        7 => LayoutClass.Caption,
        8 or 9 => LayoutClass.PageFooter,
        10 or 24 => LayoutClass.Footnote,
        12 or 13 => LayoutClass.PageHeader,
        14 or 20 => LayoutClass.Picture,
        21 => LayoutClass.Table,
        0 or 2 or 16 or 18 or 19 or 22 or 23 => LayoutClass.Text,
        _ => null,
    };

    /// <summary>
    /// Find a named input, falling back to a positional one so the model still runs if the export
    /// renames its inputs.
    /// </summary>
    private string ResolveInputName(string canonical, int fallbackPosition)
    {
        foreach (string name in _inputNames) if (name == canonical) return name;
        return fallbackPosition < _inputNames.Length ? _inputNames[fallbackPosition] : canonical;
    }

    /// <summary>Detect layout regions in a page image.</summary>
    public IReadOnlyList<LayoutDetection> Detect(RgbImage page, float threshold = DefaultThreshold) =>
        DetectBatch([page], threshold)[0];

    /// <summary>
    /// The empty-batch short-circuit, as a pure function.
    /// </summary>
    /// <remarks>
    /// Extracted so the contract is testable without loading a model, and because a zero-length
    /// batch dimension is not a shape the export accepts — the graph must not be reached at all.
    /// </remarks>
    internal static IReadOnlyList<IReadOnlyList<LayoutDetection>>? EmptyBatchShortCircuit(int pageCount) =>
        pageCount == 0 ? [] : null;

    /// <summary>
    /// The two scalar inputs for one page: <c>im_shape</c> and <c>scale_factor</c>.
    /// </summary>
    /// <remarks>
    /// PaddleDetection's convention, and the easy thing to get wrong: <c>im_shape</c> is the
    /// resized tensor size, and <c>scale_factor</c> is resized-over-original, in (height, width)
    /// order both times.
    /// </remarks>
    internal static ((float Height, float Width) ImShape, (float Height, float Width) ScaleFactor)
        ScalarInputs(int originalWidth, int originalHeight) =>
        ((InputSize, InputSize), (InputSize / (float)originalHeight, InputSize / (float)originalWidth));

    /// <summary>Detect over several pages in one graph execution.</summary>
    public IReadOnlyList<IReadOnlyList<LayoutDetection>> DetectBatch(
        IReadOnlyList<RgbImage> pages, float threshold = DefaultThreshold)
    {
        if (EmptyBatchShortCircuit(pages.Count) is { } empty) return empty;

        int batch = pages.Count;
        var images = Tensor.AllocateFloat(batch, 3, InputSize, InputSize);
        var imShape = Tensor.AllocateFloat(batch, 2);
        var scaleFactor = Tensor.AllocateFloat(batch, 2);
        int stride = 3 * InputSize * InputSize;

        for (int n = 0; n < batch; n++)
        {
            LayoutPreprocessing.PreprocessRescale(pages[n], images.Floats, InputSize, n * stride);
            var (shape, scale) = ScalarInputs(pages[n].Width, pages[n].Height);
            imShape.Floats[n * 2] = shape.Height;
            imShape.Floats[n * 2 + 1] = shape.Width;
            scaleFactor.Floats[n * 2] = scale.Height;
            scaleFactor.Floats[n * 2 + 1] = scale.Width;
        }

        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [ResolveInputName("im_shape", 0)] = imShape,
            [ResolveInputName("image", 1)] = images,
            [ResolveInputName("scale_factor", 2)] = scaleFactor,
        });

        float[]? detections = null;
        long[]? bboxCounts = null;
        foreach (var (name, value) in outputs)
        {
            if (name == "fetch_name_0" && value.IsFloat) detections = value.Floats;
            else if (name == "fetch_name_1" && !value.IsFloat) bboxCounts = value.Longs;
        }

        if (detections is null || detections.Length == 0)
            throw new InvalidDataException("fetch_name_0 missing or empty from PP-DocLayout-V3");

        var results = new List<IReadOnlyList<LayoutDetection>>(batch);
        int rowOffset = 0;
        for (int b = 0; b < batch; b++)
        {
            int valid = bboxCounts is not null && b < bboxCounts.Length
                ? (int)Math.Max(bboxCounts[b], 0)
                : 0;
            int rowEnd = rowOffset + valid;
            int sliceEnd = Math.Min(rowEnd, detections.Length / DetRowCols) * DetRowCols;
            int sliceStart = rowOffset * DetRowCols;

            results.Add(sliceStart < detections.Length
                ? ParseDetections(detections, sliceStart, sliceEnd, valid, threshold,
                                  pages[b].Width, pages[b].Height)
                : new List<LayoutDetection>());
            rowOffset = rowEnd;
        }
        return results;
    }

    /// <summary>
    /// Read the valid detection rows for one image, clamped to the page.
    /// </summary>
    /// <remarks>
    /// Rows past the count <c>fetch_name_1</c> reports are padding and must not be read: the
    /// output tensor is sized for the whole batch and the tail holds whatever the graph left there.
    /// </remarks>
    internal static List<LayoutDetection> ParseDetections(
        float[] rows, int start, int end, int valid, float threshold, int originalWidth, int originalHeight)
    {
        float maxWidth = originalWidth;
        float maxHeight = originalHeight;
        var detections = new List<LayoutDetection>();

        for (int i = 0; i < valid; i++)
        {
            int b = start + i * DetRowCols;
            if (b + DetRowCols > end || b + DetRowCols > rows.Length) break;

            float score = rows[b + ColScore];
            if (score < threshold) continue;

            if (ClassFromId((long)rows[b + ColClass]) is not { } layoutClass) continue;

            detections.Add(new LayoutDetection(layoutClass, score, new BBox(
                Math.Clamp(rows[b + ColX1], 0.0f, maxWidth),
                Math.Clamp(rows[b + ColY1], 0.0f, maxHeight),
                Math.Clamp(rows[b + ColX2], 0.0f, maxWidth),
                Math.Clamp(rows[b + ColY2], 0.0f, maxHeight))));
        }

        return LayoutPostprocessing.SortByConfidenceDesc(detections);
    }
}

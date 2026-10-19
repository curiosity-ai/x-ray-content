using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>
/// Docling RT-DETR v2 document layout detection, running on the in-process ONNX runtime.
/// <para>
/// The model is NMS-free — it is an end-to-end transformer detector, so its 300 queries come
/// out already de-duplicated and postprocessing is only a confidence filter and a clamp.
/// Ports Rust <c>layout::models::rtdetr</c>.
/// </para>
/// <para>
/// Graph contract, which the preprocessing must match exactly:
/// inputs <c>images</c> f32 <c>[N,3,640,640]</c> and <c>orig_target_sizes</c> i64
/// <c>[N,2]</c> holding <c>[height, width]</c> of the <em>source</em> page; outputs
/// <c>labels</c> i64 <c>[N,300]</c>, <c>boxes</c> f32 <c>[N,300,4]</c> already in source-image
/// coordinates, and <c>scores</c> f32 <c>[N,300]</c>.
/// </para>
/// </summary>
internal sealed class RtDetrModel
{
    /// <summary>Confidence below which a detection is discarded.</summary>
    public const float DefaultThreshold = 0.3f;

    /// <summary>The square resolution the export was traced at.</summary>
    public const int InputSize = 640;

    private readonly OnnxSession _session;
    private readonly string _imagesInput;
    private readonly string _sizesInput;

    private RtDetrModel(OnnxSession session, string imagesInput, string sizesInput)
    {
        _session = session;
        _imagesInput = imagesInput;
        _sizesInput = sizesInput;
    }

    public static RtDetrModel FromFile(string path) => Create(OnnxModel.Load(path));

    public static RtDetrModel FromBytes(ReadOnlySpan<byte> modelBytes) => Create(OnnxModel.Parse(modelBytes));

    private static RtDetrModel Create(OnnxModel model)
    {
        var inputs = model.FeedInputs.Select(i => i.Name).ToArray();
        if (inputs.Length < 2)
            throw new InvalidDataException(
                $"RT-DETR model must declare 2 inputs (images, orig_target_sizes), found {inputs.Length}");
        return new RtDetrModel(new OnnxSession(model), inputs[0], inputs[1]);
    }

    public string Name => "Docling RT-DETR v2";

    /// <summary>Detect layout regions in a page image.</summary>
    public IReadOnlyList<LayoutDetection> Detect(RgbImage page, float threshold = DefaultThreshold) =>
        DetectBatch([page], threshold)[0];

    /// <summary>
    /// Detect over several pages in one graph execution.
    /// <para>
    /// Batching matters more here than it looks: the backbone convolutions dominate runtime
    /// and their weights are re-read per call, so stacking pages amortises that traffic
    /// instead of paying it per page.
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<LayoutDetection>> DetectBatch(
        IReadOnlyList<RgbImage> pages, float threshold = DefaultThreshold)
    {
        if (pages.Count == 0) return [];

        int batch = pages.Count;
        var images = Tensor.AllocateFloat(batch, 3, InputSize, InputSize);
        var sizes = Tensor.AllocateLong(ElementType.Int64, batch, 2);
        int stride = 3 * InputSize * InputSize;

        for (int n = 0; n < batch; n++)
        {
            PreprocessRescale(pages[n], images.Floats, n * stride);
            sizes.Longs[n * 2] = pages[n].Height;
            sizes.Longs[n * 2 + 1] = pages[n].Width;
        }

        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [_imagesInput] = images,
            [_sizesInput] = sizes,
        });

        var (labels, boxes, scores) = ResolveOutputs(outputs);
        int queries = scores.Count / batch;

        var results = new List<IReadOnlyList<LayoutDetection>>(batch);
        for (int n = 0; n < batch; n++)
        {
            var detections = new List<LayoutDetection>();
            for (int q = 0; q < queries; q++)
            {
                int flat = n * queries + q;
                float confidence = scores.GetFloat(flat);
                if (confidence < threshold) continue;

                // An unmapped label id means a class this build does not model; skipping is
                // what the Rust does, and is safer than folding it into a neighbouring class.
                if (LayoutClassExtensions.FromDoclingId(labels.GetLong(flat)) is not { } layoutClass) continue;

                int b = flat * 4;
                detections.Add(new LayoutDetection(layoutClass, confidence, ClampBox(
                    boxes.GetFloat(b), boxes.GetFloat(b + 1), boxes.GetFloat(b + 2), boxes.GetFloat(b + 3),
                    pages[n].Width, pages[n].Height)));
            }

            // Descending confidence, matching `LayoutDetection::sort_by_confidence_desc`.
            detections.Sort((a, c) => c.Confidence.CompareTo(a.Confidence));
            results.Add(detections);
        }
        return results;
    }

    /// <summary>
    /// Pick the labels, boxes and scores tensors out of the graph outputs.
    /// <para>
    /// Matching by name would be brittle — the export names outputs after the nodes that
    /// produced them — so they are identified by type and rank, exactly as the Rust does:
    /// the integral tensor is the labels, the rank-3 float is the boxes, the other float is
    /// the scores.
    /// </para>
    /// </summary>
    private (Tensor Labels, Tensor Boxes, Tensor Scores) ResolveOutputs(Dictionary<string, Tensor> outputs)
    {
        Tensor? labels = null, boxes = null, scores = null;
        foreach (string name in _session.OutputNames)
        {
            if (!outputs.TryGetValue(name, out var tensor)) continue;
            if (!tensor.IsFloat) labels ??= tensor;
            else if (tensor.Rank >= 3 || tensor.Shape[^1] == 4) boxes ??= tensor;
            else scores ??= tensor;
        }

        if (boxes is null || scores is null)
            throw new InvalidDataException("RT-DETR output shape mismatch: expected float boxes and scores tensors");

        // Some exports emit labels as float; recover them rather than failing.
        labels ??= scores;
        return (labels, boxes, scores);
    }

    private static BBox ClampBox(float x1, float y1, float x2, float y2, int width, int height) => new(
        Math.Clamp(x1, 0f, width),
        Math.Clamp(y1, 0f, height),
        Math.Clamp(x2, 0f, width),
        Math.Clamp(y2, 0f, height));

    /// <summary>
    /// Rescale-only preprocessing at this model's input resolution.
    /// </summary>
    /// <remarks>Delegates to the shared implementation; see <see cref="LayoutPreprocessing"/>.</remarks>
    internal static void PreprocessRescale(RgbImage page, float[] destination, int offset = 0) =>
        LayoutPreprocessing.PreprocessRescale(page, destination, InputSize, offset);
}

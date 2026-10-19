using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>Whether a table draws its gridlines.</summary>
internal enum TableType
{
    /// <summary>Bordered table with visible gridlines.</summary>
    Wired,

    /// <summary>Borderless table without visible gridlines.</summary>
    Wireless,
}

internal static class TableTypeExtensions
{
    public static string Name(this TableType value) => value == TableType.Wired ? "wired" : "wireless";
}

/// <summary>
/// PP-LCNet table classifier — wired versus wireless — ported from Rust
/// <c>layout::models::table_classifier</c>.
/// </summary>
/// <remarks>
/// <para>
/// Its whole job is routing: a cropped table image is classified so the matching SLANeXt variant
/// runs on it, and the two variants disagree sharply on tables of the wrong kind.
/// </para>
/// <para>
/// Graph contract: input <c>x</c> f32 <c>[batch, 3, 224, 224]</c> at a fixed size with ImageNet
/// normalisation; output <c>[batch, 2]</c> holding <c>[wired_score, wireless_score]</c> as logits.
/// </para>
/// </remarks>
internal sealed class TableClassifier
{
    /// <summary>The fixed square resolution the export was traced at.</summary>
    private const int InputSize = 224;

    /// <summary>Minimum edge length the image is resized to before the centre crop.</summary>
    private const int MinEdge = 256;

    /// <summary>
    /// ImageNet normalisation mean, in BGR channel order.
    /// </summary>
    /// <remarks>
    /// PaddleOCR preprocesses through OpenCV, which is BGR, so the per-channel constants are
    /// applied to blue, green and red in that order. Feeding them in RGB order silently shifts
    /// every channel and the classifier's answer with it.
    /// </remarks>
    private static readonly float[] ImagenetMeanBgr = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet normalisation standard deviation, in BGR channel order.</summary>
    private static readonly float[] ImagenetStdBgr = { 0.229f, 0.224f, 0.225f };

    private readonly OnnxSession _session;
    private readonly string _inputName;

    private TableClassifier(OnnxSession session, string inputName)
    {
        _session = session;
        _inputName = inputName;
    }

    public static TableClassifier FromFile(string path) => Create(OnnxModel.Load(path));

    public static TableClassifier FromBytes(ReadOnlySpan<byte> modelBytes) => Create(OnnxModel.Parse(modelBytes));

    private static TableClassifier Create(OnnxModel model)
    {
        var inputs = model.FeedInputs.Select(input => input.Name).ToArray();
        if (inputs.Length < 1)
            throw new InvalidDataException("table classifier model has no inputs");
        return new TableClassifier(new OnnxSession(model), inputs[0]);
    }

    /// <summary>Classify a cropped table image as wired or wireless.</summary>
    /// <remarks>
    /// A model output that cannot be read at all falls back to wireless, matching upstream: the
    /// wireless variant is the safer default because it does not depend on gridlines being found.
    /// </remarks>
    public TableType Classify(RgbImage tableImage) => ClassifyWithLogits(tableImage).Type;

    /// <summary>
    /// Classify, also returning the two raw logits.
    /// </summary>
    /// <remarks>
    /// The logits are what make a preprocessing divergence visible: the wired/wireless decision
    /// alone is a single bit and survives a fairly large numeric drift, so a probe that compares
    /// only the decision would pass on preprocessing that is merely close.
    /// </remarks>
    internal (TableType Type, float RawWired, float RawWireless) ClassifyWithLogits(RgbImage tableImage)
    {
        var input = Tensor.AllocateFloat(1, 3, InputSize, InputSize);
        PreprocessLcnet(tableImage, input.Floats);

        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [_inputName] = input,
        });

        foreach (var output in outputs.Values)
        {
            if (!output.IsFloat) continue;
            var data = output.Floats;
            if (data.Length < 2) continue;

            return (Classify(data[0], data[1]), data[0], data[1]);
        }

        return (TableType.Wireless, float.NaN, float.NaN);
    }

    /// <summary>
    /// Turn the two raw logits into a decision, through a max-subtracted softmax.
    /// </summary>
    /// <remarks>
    /// Subtracting the maximum before exponentiating is what keeps a large logit from
    /// overflowing to infinity and making both probabilities NaN. A tie goes to wired.
    /// </remarks>
    internal static TableType Classify(float rawWired, float rawWireless)
    {
        float max = MathF.Max(rawWired, rawWireless);
        float expWired = MathF.Exp(rawWired - max);
        float expWireless = MathF.Exp(rawWireless - max);
        float sum = expWired + expWireless;
        return expWired / sum >= expWireless / sum ? TableType.Wired : TableType.Wireless;
    }

    /// <summary>
    /// Preprocess for PP-LCNet, matching MinerU's <c>paddle_table_cls.py</c>.
    /// </summary>
    /// <remarks>
    /// Resize so the shortest edge is 256 with the aspect ratio preserved, centre-crop to
    /// 224x224, then normalise in BGR order. The aspect-preserving resize followed by a crop is
    /// not interchangeable with a direct stretch to 224: it is what keeps a wide table's
    /// gridline spacing intact, which is the entire signal this model reads.
    /// </remarks>
    internal static void PreprocessLcnet(RgbImage image, float[] destination, int offset = 0)
    {
        int originalWidth = image.Width;
        int originalHeight = image.Height;

        float scale = MinEdge / (float)Math.Min(originalWidth, originalHeight);
        int newWidth = (int)MathF.Max(MathF.Round(originalWidth * scale, MidpointRounding.AwayFromZero), 1.0f);
        int newHeight = (int)MathF.Max(MathF.Round(originalHeight * scale, MidpointRounding.AwayFromZero), 1.0f);

        var resized = image.ResizeTriangle(newWidth, newHeight);

        int cropX = Math.Max(newWidth - InputSize, 0) / 2;
        int cropY = Math.Max(newHeight - InputSize, 0) / 2;
        int cropWidth = Math.Min(InputSize, newWidth);
        int cropHeight = Math.Min(InputSize, newHeight);

        const float inv255 = 1.0f / 255.0f;
        float alphaB = inv255 / ImagenetStdBgr[0];
        float alphaG = inv255 / ImagenetStdBgr[1];
        float alphaR = inv255 / ImagenetStdBgr[2];
        float betaB = -ImagenetMeanBgr[0] / ImagenetStdBgr[0];
        float betaG = -ImagenetMeanBgr[1] / ImagenetStdBgr[1];
        float betaR = -ImagenetMeanBgr[2] / ImagenetStdBgr[2];

        int plane = InputSize * InputSize;

        // The crop can be smaller than 224 when the source is smaller than the minimum edge; the
        // tensor keeps its declared shape and the shortfall stays zeroed, as it does upstream.
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < cropHeight; y++)
            {
                var row = accessor.GetRowSpan(cropY + y);
                int rowOffset = offset + y * cropWidth;
                for (int x = 0; x < cropWidth; x++)
                {
                    var pixel = row[cropX + x];
                    destination[rowOffset + x] = pixel.B * alphaB + betaB;
                    destination[plane + rowOffset + x] = pixel.G * alphaG + betaG;
                    destination[2 * plane + rowOffset + x] = pixel.R * alphaR + betaR;
                }
            }
        });
    }
}

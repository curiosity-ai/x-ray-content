using XRay.Content.Internal.Onnx;
using XRay.Content.Internal.Imaging;

namespace XRay.Content.Internal.Layout;

/// <summary>One cell SLANeXt detected, with its grid position.</summary>
/// <param name="Polygon">
/// Four corners in image pixel coordinates, clockwise from top-left:
/// <c>[x1,y1,x2,y2,x3,y3,x4,y4]</c>.
/// </param>
/// <param name="Box">Axis-aligned bounds derived from the polygon: <c>[left, top, right, bottom]</c>.</param>
internal sealed record SlanetCell(float[] Polygon, float[] Box, int Row, int Col);

/// <summary>SLANeXt's recognition of one table image.</summary>
internal sealed class SlanetResult
{
    public List<SlanetCell> Cells { get; init; } = new();
    public int NumRows { get; set; }
    public int NumCols { get; set; }

    /// <summary>Mean structure-token confidence over the decoded sequence.</summary>
    public float Confidence { get; set; }

    /// <summary>The raw HTML structure tokens, in order.</summary>
    public List<string> StructureTokens { get; init; } = new();
}

/// <summary>
/// SLANeXt table structure recognition, ported from Rust <c>layout::models::slanet</c>.
/// </summary>
/// <remarks>
/// <para>
/// PaddleOCR's sequence-to-sequence table recogniser. It emits a stream of HTML structure tokens —
/// <c>&lt;tr&gt;</c>, <c>&lt;td&gt;</c>, colspan and rowspan attributes — alongside a cell polygon
/// per position, and the grid is reconstructed by walking that token stream. Two variants exist,
/// wired and wireless, chosen by the PP-LCNet table classifier.
/// </para>
/// <para>
/// Graph contract: input <c>x</c> f32 <c>[batch,3,512,512]</c>; outputs <c>[batch,seq,8]</c> cell
/// polygons and <c>[batch,seq,50]</c> token logits.
/// </para>
/// </remarks>
internal sealed class SlanetModel
{
    /// <summary>The fixed square resolution the export was traced at.</summary>
    internal const int InputSize = 512;

    /// <summary>Structure-token vocabulary size.</summary>
    internal const int VocabSize = 50;

    private const int SosTokenIndex = 0;
    private const int EosTokenIndex = 49;

    /// <summary>
    /// ImageNet normalisation mean, in BGR channel order.
    /// </summary>
    /// <remarks>PaddleOCR preprocesses through OpenCV, which splits channels as BGR.</remarks>
    private static readonly float[] ImagenetMeanBgr = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet normalisation standard deviation, in BGR channel order.</summary>
    private static readonly float[] ImagenetStdBgr = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// The HTML structure token dictionary: PaddleOCR's <c>table_structure_dict_ch.txt</c> with
    /// <c>sos</c> prepended and <c>eos</c> appended.
    /// </summary>
    internal static readonly string[] TokenDict =
    {
        "sos",
        "<thead>", "</thead>", "<tbody>", "</tbody>",
        "<tr>", "</tr>",
        "<td>", "<td", ">", "</td>",
        " colspan=\"2\"", " colspan=\"3\"", " colspan=\"4\"", " colspan=\"5\"", " colspan=\"6\"",
        " colspan=\"7\"", " colspan=\"8\"", " colspan=\"9\"", " colspan=\"10\"", " colspan=\"11\"",
        " colspan=\"12\"", " colspan=\"13\"", " colspan=\"14\"", " colspan=\"15\"", " colspan=\"16\"",
        " colspan=\"17\"", " colspan=\"18\"", " colspan=\"19\"", " colspan=\"20\"",
        " rowspan=\"2\"", " rowspan=\"3\"", " rowspan=\"4\"", " rowspan=\"5\"", " rowspan=\"6\"",
        " rowspan=\"7\"", " rowspan=\"8\"", " rowspan=\"9\"", " rowspan=\"10\"", " rowspan=\"11\"",
        " rowspan=\"12\"", " rowspan=\"13\"", " rowspan=\"14\"", " rowspan=\"15\"", " rowspan=\"16\"",
        " rowspan=\"17\"", " rowspan=\"18\"", " rowspan=\"19\"", " rowspan=\"20\"",
        "eos",
    };

    private readonly OnnxSession _session;
    private readonly string _inputName;

    private SlanetModel(OnnxSession session, string inputName)
    {
        _session = session;
        _inputName = inputName;
    }

    public static SlanetModel FromFile(string path) => Create(OnnxModel.Load(path));

    public static SlanetModel FromBytes(ReadOnlySpan<byte> modelBytes) => Create(OnnxModel.Parse(modelBytes));

    private static SlanetModel Create(OnnxModel model)
    {
        var inputs = model.FeedInputs.Select(input => input.Name).ToArray();
        if (inputs.Length < 1) throw new InvalidDataException("SLANeXt model has no inputs");
        return new SlanetModel(new OnnxSession(model), inputs[0]);
    }

    /// <summary>Recognise table structure from a cropped table image.</summary>
    public SlanetResult Recognize(RgbImage tableImage)
    {
        var input = Tensor.AllocateFloat(1, 3, InputSize, InputSize);
        PreprocessSlanet(tableImage, input.Floats);

        var outputs = _session.Run(new Dictionary<string, Tensor>(StringComparer.Ordinal)
        {
            [_inputName] = input,
        });

        var floatOutputs = outputs.Values.Where(t => t.IsFloat).ToList();
        if (floatOutputs.Count < 2)
            throw new InvalidDataException($"SLANeXt expected 2 float outputs, got {floatOutputs.Count}");

        // Told apart by shape, not order: the polygon output has a trailing dimension of 8.
        Tensor boxes, logits;
        if (floatOutputs[0].Shape[^1] == 8) { boxes = floatOutputs[0]; logits = floatOutputs[1]; }
        else { logits = floatOutputs[0]; boxes = floatOutputs[1]; }

        return Decode(logits, boxes, tableImage.Width, tableImage.Height);
    }

    /// <summary>
    /// Decode the token stream and cell polygons into a grid.
    /// </summary>
    /// <remarks>
    /// Separate from inference so the reconstruction can be tested on hand-built tensors, which
    /// is where the token-walking rules actually live.
    /// </remarks>
    internal static SlanetResult Decode(Tensor logits, Tensor boxes, float originalWidth, float originalHeight)
    {
        int sequence = logits.Shape.Length > 1 ? logits.Shape[1] : 0;
        int vocab = logits.Shape[^1];

        var result = new SlanetResult();
        if (sequence == 0 || vocab < VocabSize) return result;

        var tokenEntries = new List<(int Index, int Position)>();
        var scores = new List<float>();

        for (int t = 0; t < sequence; t++)
        {
            var (index, score) = ArgmaxWithScore(logits.Floats, t * vocab, vocab);

            // End of sequence stops the walk, but only after the first position: the model emits
            // its start token there and an eos at t=0 would truncate everything.
            if (t > 0 && index == EosTokenIndex) break;
            if (index == SosTokenIndex) continue;
            if (index >= VocabSize) continue;

            result.StructureTokens.Add(TokenDict[index]);
            tokenEntries.Add((index, t));
            scores.Add(score);
        }

        result.Confidence = scores.Count == 0 ? 0.0f : scores.Sum() / scores.Count;

        int currentRow = 0, currentColumn = 0, maxColumns = 0;
        bool inTd = false;

        foreach (var (index, position) in tokenEntries)
        {
            string token = TokenDict[index];
            switch (token)
            {
                case "<tr>":
                    // The first row opens without advancing; every later one closes the previous.
                    if (currentRow > 0 || currentColumn > 0)
                    {
                        maxColumns = Math.Max(maxColumns, currentColumn);
                        currentRow++;
                    }
                    currentColumn = 0;
                    break;

                case "</tr>":
                    maxColumns = Math.Max(maxColumns, currentColumn);
                    break;

                case "<td>":
                case "<td":
                {
                    int offset = position * 8;
                    if (offset + 8 <= boxes.Count)
                        result.Cells.Add(BuildCell(
                            boxes.Floats, offset, originalWidth, originalHeight, currentRow, currentColumn));

                    // `<td>` is a complete cell; `<td` opens one whose attributes follow, and the
                    // column only advances when the matching `>` arrives.
                    if (token == "<td>") currentColumn++;
                    else inTd = true;
                    break;
                }

                case ">" when inTd:
                    currentColumn++;
                    inTd = false;
                    break;
            }
        }

        maxColumns = Math.Max(maxColumns, currentColumn);
        result.NumCols = maxColumns;
        result.NumRows = maxColumns > 0 ? currentRow + 1 : 0;
        return result;
    }

    /// <summary>
    /// Turn a normalised polygon into a pixel-space cell, clamped to the image.
    /// </summary>
    /// <remarks>
    /// The polygon's x coordinates scale by the original width and the y coordinates by the
    /// original height — the model predicts in normalised space against the source image, not
    /// against the letterboxed 512x512 input, so the padding does not enter the decode.
    /// </remarks>
    private static SlanetCell BuildCell(
        float[] data, int offset, float originalWidth, float originalHeight, int row, int column)
    {
        var polygon = new float[8];
        for (int i = 0; i < 8; i += 2)
        {
            polygon[i] = Math.Clamp(data[offset + i] * originalWidth, 0.0f, originalWidth);
            polygon[i + 1] = Math.Clamp(data[offset + i + 1] * originalHeight, 0.0f, originalHeight);
        }

        float left = polygon[0], top = polygon[1], right = polygon[0], bottom = polygon[1];
        for (int i = 2; i < 8; i += 2)
        {
            left = MathF.Min(left, polygon[i]);
            right = MathF.Max(right, polygon[i]);
            top = MathF.Min(top, polygon[i + 1]);
            bottom = MathF.Max(bottom, polygon[i + 1]);
        }

        return new SlanetCell(polygon, [left, top, right, bottom], row, column);
    }

    /// <summary>
    /// Preprocess for SLANeXt: aspect-preserving resize into 512x512, then ImageNet normalisation
    /// in BGR channel order.
    /// </summary>
    /// <remarks>
    /// The padding is not zero pixels but the normalised value of a zero pixel — the whole tensor
    /// is filled with the per-channel bias first and the resized image written over it. Padding
    /// with raw zeros would present mid-grey to the model instead of black.
    /// </remarks>
    internal static void PreprocessSlanet(RgbImage image, float[] destination, int offset = 0)
    {
        float scale = MathF.Min(InputSize / (float)image.Width, InputSize / (float)image.Height);
        int newWidth = (int)MathF.Max(MathF.Round(image.Width * scale, MidpointRounding.AwayFromZero), 1.0f);
        int newHeight = (int)MathF.Max(MathF.Round(image.Height * scale, MidpointRounding.AwayFromZero), 1.0f);

        var resized = image.ResizeTriangle(newWidth, newHeight);

        const float inv255 = 1.0f / 255.0f;
        float alphaB = inv255 / ImagenetStdBgr[0];
        float alphaG = inv255 / ImagenetStdBgr[1];
        float alphaR = inv255 / ImagenetStdBgr[2];
        float betaB = -ImagenetMeanBgr[0] / ImagenetStdBgr[0];
        float betaG = -ImagenetMeanBgr[1] / ImagenetStdBgr[1];
        float betaR = -ImagenetMeanBgr[2] / ImagenetStdBgr[2];

        int plane = InputSize * InputSize;
        Array.Fill(destination, betaB, offset, plane);
        Array.Fill(destination, betaG, offset + plane, plane);
        Array.Fill(destination, betaR, offset + 2 * plane, plane);

        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = offset + y * InputSize;
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    destination[rowOffset + x] = pixel.B * alphaB + betaB;
                    destination[plane + rowOffset + x] = pixel.G * alphaG + betaG;
                    destination[2 * plane + rowOffset + x] = pixel.R * alphaR + betaR;
                }
            }
        });
    }

    /// <summary>
    /// The argmax index and the softmax probability of that maximum.
    /// </summary>
    /// <remarks>
    /// The probability is <c>1 / sum(exp(logit - max))</c>, which is the softmax of the maximum
    /// without materialising the whole distribution.
    /// </remarks>
    internal static (int Index, float Probability) ArgmaxWithScore(float[] logits, int offset, int count)
    {
        int maxIndex = 0;
        float maxValue = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
            if (logits[offset + i] > maxValue) { maxValue = logits[offset + i]; maxIndex = i; }

        float sumExp = 0.0f;
        for (int i = 0; i < count; i++) sumExp += MathF.Exp(logits[offset + i] - maxValue);
        return (maxIndex, 1.0f / sumExp);
    }
}

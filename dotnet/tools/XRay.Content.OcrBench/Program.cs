// OCR measurement harness: runs the optional OCR pass over the scanned-PDF fixtures, times it,
// and scores the recognised text against test_documents' hand-checked ground truth.
//
// Two things here are deliberately not in the library. It stages the checkpoints (the library
// never downloads), and it scores (the library has no opinion about quality). Both belong to a
// harness precisely because an extraction call should not do either as a side effect.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PaddleOcrSharp.Download;
using XRay.Content.Core;
using XRay.Content.Types;

string corpus = "/home/user/test_documents";
string outDir = "/tmp/claude-0/-home-user/12a178fb-ea2c-5d68-9dbc-406d556abf79/scratchpad/ocr";
bool download = false, run = false;
string? only = null;
int maxFiles = int.MaxValue;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--download": download = true; break;
        case "--run": run = true; break;
        case "--corpus": corpus = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--only": only = args[++i]; break;
        case "--max": maxFiles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}
Directory.CreateDirectory(outDir);

if (download)
{
    using var downloader = new ModelDownloader();
    var sw0 = Stopwatch.StartNew();
    string vl = await downloader.EnsureAsync(ModelCatalog.PaddleOcrVL16);
    Console.WriteLine($"[ocr] VL checkpoint  : {vl} ({sw0.Elapsed.TotalSeconds:F0}s)");
    sw0.Restart();
    string layout = await downloader.EnsureAsync(ModelCatalog.PpDocLayoutV3);
    Console.WriteLine($"[ocr] layout detector: {layout} ({sw0.Elapsed.TotalSeconds:F0}s)");
    long bytes = 0;
    foreach (var d in new[] { vl, layout })
        foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            bytes += new FileInfo(f).Length;
    Console.WriteLine($"[ocr] staged {bytes / 1024.0 / 1024:F0} MiB");
}

if (!run) return 0;

// --- ground truth ------------------------------------------------------------------------
// The scanned fixtures are named `<stem>_scanned.pdf` and the ground truth is filed under the
// bare `<stem>`, so the mapping is looked up on the stem with the suffix removed.
var mapPath = Path.Combine(corpus, "ground_truth", "ground_truth_mapping.json");
var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath))!;
string corpusParent = Path.GetDirectoryName(corpus.TrimEnd('/'))!;

string? GroundTruthFor(string pdf)
{
    string stem = Path.GetFileNameWithoutExtension(pdf);
    foreach (var key in new[] { stem, stem.Replace("_scanned", "") })
        if (map.TryGetValue(key, out var rel))
        {
            string p = Path.Combine(corpusParent, rel);
            if (File.Exists(p)) return p;
        }
    return null;
}

// --- the metric --------------------------------------------------------------------------
// Token F1 against the reference: the multiset overlap of whitespace-split, case-folded,
// punctuation-stripped tokens. Recall is what "did OCR read the page" means; precision catches
// a recognizer that invents text. Word order is deliberately not scored — a two-column scan and
// its reference disagree about order for reasons that are not recognition quality.
static List<string> Tokens(string s)
{
    var result = new List<string>();
    var sb = new StringBuilder();
    foreach (var ch in s)
    {
        if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        else if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
    }
    if (sb.Length > 0) result.Add(sb.ToString());
    return result;
}

static (double P, double R, double F1) Score(string hyp, string reference)
{
    var h = Tokens(hyp); var r = Tokens(reference);
    if (h.Count == 0 || r.Count == 0) return (0, 0, 0);
    var counts = new Dictionary<string, int>();
    foreach (var t in r) counts[t] = counts.GetValueOrDefault(t) + 1;
    int overlap = 0;
    foreach (var t in h)
        if (counts.TryGetValue(t, out var c) && c > 0) { counts[t] = c - 1; overlap++; }
    double p = (double)overlap / h.Count, rec = (double)overlap / r.Count;
    return (p, rec, p + rec == 0 ? 0 : 2 * p * rec / (p + rec));
}

var files = Directory.EnumerateFiles(Path.Combine(corpus, "pdf_scanned"), "*.pdf")
    .Where(f => only is null || Path.GetFileName(f).Contains(only, StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => new FileInfo(f).Length)
    .Take(maxFiles)
    .ToList();

Console.WriteLine($"[ocr] {files.Count} scanned fixtures");

var extractor = new Extractor();
var nativeCfg = new ExtractionConfig { OutputFormat = OutputFormat.Plain };
var ocrCfg = new ExtractionConfig
{
    OutputFormat = OutputFormat.Plain,
    Ocr = new OcrOptions { Mode = OcrMode.ScanOnly, PerImageTimeout = TimeSpan.FromMinutes(10) },
};

using var tsv = new StreamWriter(Path.Combine(outDir, "ocr.tsv"));
tsv.WriteLine("file\tbytes\tnative_ms\tnative_chars\tnative_f1\tocr_ms\tocr_chars\tocr_p\tocr_r\tocr_f1\tgt_chars\twarnings");

foreach (var f in files)
{
    string name = Path.GetFileName(f);
    string? gt = GroundTruthFor(f);
    string reference = gt is null ? "" : File.ReadAllText(gt);

    var sw = Stopwatch.StartNew();
    string nativeText = "";
    try { nativeText = extractor.Extract(ExtractInput.FromUri(f), nativeCfg).Results.FirstOrDefault()?.Content ?? ""; }
    catch (Exception e) { Console.Error.WriteLine($"  native failed: {e.Message}"); }
    double nativeMs = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    string ocrText = ""; string warn = "";
    try
    {
        var res = extractor.Extract(ExtractInput.FromUri(f), ocrCfg).Results.FirstOrDefault();
        ocrText = res?.Content ?? "";
        warn = string.Join("; ", res?.ProcessingWarnings?.Select(w => w.Message) ?? []);
    }
    catch (Exception e) { warn = e.Message; Console.Error.WriteLine($"  ocr failed: {e.Message}"); }
    double ocrMs = sw.Elapsed.TotalMilliseconds;

    var ns = Score(nativeText, reference);
    var os_ = Score(ocrText, reference);

    File.WriteAllText(Path.Combine(outDir, name + ".ocr.txt"), ocrText);
    tsv.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{name}\t{new FileInfo(f).Length}\t{nativeMs:F0}\t{nativeText.Length}\t{ns.F1:F4}\t{ocrMs:F0}\t{ocrText.Length}\t{os_.P:F4}\t{os_.R:F4}\t{os_.F1:F4}\t{reference.Length}\t{warn.Replace('\t', ' ')}"));
    tsv.Flush();
    Console.WriteLine($"{name,-34} native {nativeText.Length,7} ch f1={ns.F1:F3}  |  ocr {ocrText.Length,7} ch f1={os_.F1:F3} in {ocrMs / 1000:F0}s");
}

Console.WriteLine($"[ocr] wrote {outDir}/ocr.tsv");
return 0;

using System;

namespace XRay.Content.Core;

/// <summary>
/// Behavioural knobs for this port that have no counterpart in the Rust `ExtractionConfig`.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately kept out of <see cref="ExtractionConfig"/>, which mirrors upstream's
/// config type field for field and is part of the JSON wire format. What lives here is
/// port-local: guards against pathological input, and switches between two implementations of
/// the same stage that exist only because the port carries both.
/// </para>
/// <para>
/// <b>Nothing in the library reads the environment.</b> A knob that answers to an environment
/// variable read from inside library code is invisible to the consumer who linked it: it cannot
/// be discovered from the API, cannot be set per call, and silently changes behaviour based on
/// ambient process state. Test harnesses that need to drive the port and the Rust original from
/// one variable call <see cref="FromEnvironment"/> themselves and pass the result in — the
/// mapping is theirs to opt into, not the library's to assume.
/// </para>
/// </remarks>
public sealed class XRayOptions
{
    /// <summary>
    /// Options used when a call supplies none. Assign once at startup to configure the library
    /// process-wide; prefer setting <see cref="ExtractionConfig.Options"/> per call where the
    /// choice is not really global.
    /// </summary>
    public static XRayOptions Default { get; set; } = new();

    /// <summary>
    /// Take page spans from the ported pdf_oxide pipeline (default) rather than the older
    /// content-stream interpreter.
    /// </summary>
    /// <remarks>
    /// On by default: the ported producer wins on every dimension the corpus measures. The older
    /// interpreter stays reachable because it is still the source of the drawn paths the table
    /// tiers read, and because a per-fixture A/B is the fastest way to attribute a regression to
    /// the span layer.
    /// </remarks>
    public bool UsePortedPdfSpans { get; init; } = true;

    /// <summary>
    /// Resolve a file whose extension or shebang names a programming language to
    /// <c>text/x-source-code</c>, so the source-code extractor handles it instead of the
    /// plain-text one.
    /// </summary>
    /// <remarks>
    /// On by default, matching what upstream's published packages ship: the <c>tree-sitter</c>
    /// feature is in its <c>full</c> set. It is a switch rather than a constant because upstream
    /// gates it at compile time, so a build without that feature reads the same `.py` file as
    /// plain text — and the golden sets generated from each build disagree accordingly.
    /// </remarks>
    public bool SourceCodeDetection { get; init; } = true;

    /// <summary>
    /// Render a spreadsheet's numeric cells through their number format, so a cell comes out
    /// the way the spreadsheet shows it — <c>3.2%</c>, <c>$31,200</c>, <c>1/1/2008</c> — rather
    /// than as the bare value behind it.
    /// </summary>
    /// <remarks>
    /// On by default, and a deviation from upstream rather than a port of one: calamine hands
    /// the Rust side an <c>f64</c> and it prints that, so a golden generated from upstream shows
    /// the unformatted number. Turn this off — or set <c>XRAY_EXCEL_NUMBER_FORMATS=0</c> for a
    /// harness — to compare against those goldens. See "Deviation: formatted Excel cells" in
    /// <c>CLAUDE.md</c>.
    /// </remarks>
    public bool ExcelNumberFormats { get; init; } = true;

    /// <summary>
    /// Fixed part of the per-document wall-clock guard for PDF extraction, in seconds. Also the
    /// floor: a one-page document still gets this long.
    /// </summary>
    public int PdfBaseSeconds { get; init; } = 25;

    /// <summary>
    /// Per-page allowance added to <see cref="PdfBaseSeconds"/>, in milliseconds, so the guard
    /// scales with the work a document actually represents.
    /// </summary>
    /// <remarks>
    /// Measured on the corpus, extraction is linear in page count at roughly 9-20 ms per page
    /// (the 4778-page Intel SDM extracts fully in ~55 s, the 1962-page `algebra_topology` in
    /// ~39 s), with no page behaving differently from its neighbours. 50 ms/page leaves about
    /// 2.5x headroom over the worst rate observed while still bounding a document that has
    /// genuinely stopped making progress.
    /// </remarks>
    public double PdfMillisecondsPerPage { get; init; } = 50.0;

    /// <summary>
    /// Ceiling on the computed guard, in seconds, however many pages a document has. Zero or
    /// negative disables the guard entirely.
    /// </summary>
    public int PdfMaxSecondsPerDocument { get; init; } = 3600;

    /// <summary>
    /// Root of the Hugging Face style cache the layout models are read from and written to.
    /// <c>null</c> uses <c>~/.cache/huggingface/hub</c>.
    /// </summary>
    /// <remarks>
    /// Hugging Face's own tooling takes this from <c>HF_HUB_CACHE</c> and friends, but this
    /// library reads no environment: a consumer who wants those honoured calls
    /// <see cref="FromEnvironment"/>, which maps them here.
    /// </remarks>
    public string? ModelCacheDirectory { get; init; }

    /// <summary>
    /// Never reach the network for a model. A model that is not already cached, and verified,
    /// then fails rather than downloading.
    /// </summary>
    public bool ModelDownloadsDisabled { get; init; }

    /// <summary>
    /// The wall-clock budget for a document of <paramref name="pageCount"/> pages, in seconds:
    /// <see cref="PdfBaseSeconds"/> plus <see cref="PdfMillisecondsPerPage"/> per page, clamped
    /// to <see cref="PdfMaxSecondsPerDocument"/>.
    /// </summary>
    /// <remarks>
    /// A fixed guard cannot serve both ends of this corpus: 25 s is generous for the median
    /// fixture and cuts a 4778-page manual off mid-document, while a flat budget large enough
    /// for the manual lets a small pathological file spin for just as long. Scaling by page
    /// count gives each document a budget proportional to the work it represents.
    ///
    /// For scale at the top end: upstream's own generator takes ~105 s per extraction on that
    /// Intel SDM — this port is roughly twice as fast — and its nominal 45 s guard never fires,
    /// because `extract` is CPU-bound synchronous work inside an async fn and tokio has no await
    /// point at which to cancel it. Goldens for such files are complete ~105 s extractions, so a
    /// guard that trips earlier cannot reproduce them however correct the extraction is.
    /// </remarks>
    internal double PdfBudgetSeconds(int pageCount)
    {
        double budget = PdfBaseSeconds + (PdfMillisecondsPerPage * Math.Max(pageCount, 0) / 1000.0);
        return Math.Clamp(budget, PdfBaseSeconds, PdfMaxSecondsPerDocument);
    }

    /// <summary>
    /// Absolute tick deadline for a document of <paramref name="pageCount"/> pages starting now,
    /// or <see cref="long.MaxValue"/> when <see cref="PdfMaxSecondsPerDocument"/> disables the
    /// guard.
    /// </summary>
    internal long PdfDeadlineFromNow(int pageCount)
    {
        if (PdfMaxSecondsPerDocument <= 0) return long.MaxValue;
        double seconds = PdfBudgetSeconds(pageCount);
        return DateTime.UtcNow.Ticks + (long)(seconds * TimeSpan.TicksPerSecond);
    }

    /// <summary>
    /// Build options from <c>XRAY_*</c> environment variables, or their <c>XBERG_*</c> aliases,
    /// for test harnesses that drive this port and the Rust original from one set of variables.
    /// The library never calls this: a caller opts in explicitly and passes the result through
    /// <see cref="ExtractionConfig.Options"/> or <see cref="Default"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset or unparseable variables leave the corresponding default in place, so a harness can
    /// set only the one variable it is varying.
    /// </para>
    /// <para>
    /// Every knob answers to two names: <c>XRAY_&lt;knob&gt;</c>, which matches this package, and
    /// <c>XBERG_&lt;knob&gt;</c>, which the upstream Rust side reads. The <c>XBERG_</c> aliases
    /// are kept rather than renamed away because driving both implementations from one variable
    /// is the whole reason these exist — a harness that sets <c>XBERG_OXIDE_SPANS</c> steers the
    /// Rust original and this port together. <c>XRAY_</c> wins where both are set; the value is
    /// resolved by name first and only then parsed, so a malformed <c>XRAY_</c> value leaves the
    /// default in place rather than silently falling through to a stale <c>XBERG_</c> one.
    /// </para>
    /// </remarks>
    public static XRayOptions FromEnvironment()
    {
        var defaults = new XRayOptions();
        return new XRayOptions
        {
            UsePortedPdfSpans =
                Flag(Own("OXIDE_SPANS")) ?? defaults.UsePortedPdfSpans,
            ExcelNumberFormats =
                Flag(Own("EXCEL_NUMBER_FORMATS")) ?? defaults.ExcelNumberFormats,
            PdfBaseSeconds =
                Integer(Own("PDF_BASE_SECONDS")) ?? defaults.PdfBaseSeconds,
            PdfMillisecondsPerPage =
                Number(Own("PDF_MS_PER_PAGE")) ?? defaults.PdfMillisecondsPerPage,
            PdfMaxSecondsPerDocument =
                Integer(Own("PDF_MAX_SECONDS")) ?? defaults.PdfMaxSecondsPerDocument,
            ModelCacheDirectory =
                HfCacheDirectory() ?? defaults.ModelCacheDirectory,
            ModelDownloadsDisabled =
                (Flag(Raw("HF_HUB_OFFLINE")) ?? false) || (Flag(Raw("TRANSFORMERS_OFFLINE")) ?? false)
                || defaults.ModelDownloadsDisabled,
        };

        // One of this package's own knobs, under either prefix: XRAY_ first, then the XBERG_
        // alias the upstream Rust side reads. Resolving by name before parsing is what keeps a
        // malformed XRAY_ value from falling through to a stale alias.
        static string? Own(string knob) => Raw("XRAY_" + knob) ?? Raw("XBERG_" + knob);

        // An environment variable's value, with empty treated as unset.
        static string? Raw(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        // The Hugging Face cache precedence, in the order the hub's own client applies it.
        static string? HfCacheDirectory()
        {
            if (Raw("HF_HUB_CACHE") is { } hubCache) return hubCache;
            if (Raw("HUGGINGFACE_HUB_CACHE") is { } legacy) return legacy;
            if (Raw("HF_HOME") is { } home) return System.IO.Path.Combine(home, "hub");
            if (Raw("XDG_CACHE_HOME") is { } xdg)
                return System.IO.Path.Combine(xdg, "huggingface", "hub");
            return null;
        }

        static bool? Flag(string? value) => value switch
        {
            null => null,
            "0" or "false" or "no" => false,
            _ => true,
        };

        static int? Integer(string? value) =>
            int.TryParse(value, out int v) ? v : null;

        static double? Number(string? value) =>
            double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

using XRay.Content.Types;

namespace XRay.Content.Core;

/// <summary>
/// Public entry point: dispatches an <see cref="ExtractInput"/> to a registered extractor,
/// derives the <see cref="ExtractedDocument"/>, and wraps it in an <see cref="ExtractionResult"/>.
/// When no extractor is registered for a MIME type, a graceful empty result is produced.
/// </summary>
public sealed class Extractor
{
    private readonly Registry _registry;

    public Extractor() : this(Registry.RegisterDefaults()) { }

    public Extractor(Registry registry) => _registry = registry;

    public ExtractionResult Extract(ExtractInput input, ExtractionConfig config)
    {
        var result = new ExtractionResult();
        try
        {
            var (bytes, mimeType) = Resolve(input, config);
            // Extension-based language detection needs the file's name, and only the caller or
            // this resolution step knows it.
            config.SourceName ??= input.Filename ?? (input.Uri is { } u ? Path.GetFileName(u) : null);
            var extractor = _registry.ForMime(mimeType);
            if (extractor is null)
            {
                // Graceful: return an empty document carrying the detected MIME + a warning.
                var empty = new ExtractedDocument { MimeType = mimeType };
                empty.ProcessingWarnings.Add(new ProcessingWarning
                {
                    Source = "registry",
                    Message = $"No extractor registered for MIME type: {mimeType}",
                });
                result.Results.Add(empty);
            }
            else
            {
                var internalDoc = extractor.Extract(bytes, mimeType, config);
                // Before derive, unlike the QR pass: working on the element stream lets an
                // image's recognised text be inserted where the image sits, so every renderer
                // places it inline rather than appending it to the end of the document.
                Ocr.OcrProcessor.Process(internalDoc, bytes, mimeType, config);
                var extracted = Derive.DeriveExtractionResult(
                    internalDoc, config.IncludeDocumentStructure, config.OutputFormat, config.HtmlOutput,
                    config.RenderPagesInOutputFormat);
                // Record the format the content was rendered in (Rust `pipeline::format`).
                // Set after derive so the extractor-supplied value the renderer consults for
                // `PreRenderedContent` is not disturbed.
                extracted.Metadata.OutputFormat = config.OutputFormat.ToString();
                // Runs after the content is rendered, because the payloads are appended to that
                // rendered text and not just recorded per image.
                QrPostProcessor.Process(extracted, config);
                result.Results.Add(extracted);
            }
        }
        catch (Exception ex)
        {
            // Upstream's `extraction_error_type` / `extraction_error_code` taxonomy. Only the
            // variants this port can actually raise are distinguished; everything else lands on
            // the same catch-all upstream uses for the variants it does not name.
            var (errorType, code) = ex switch
            {
                SecurityException => ("security", 1006u),
                ValidationException => ("validation", 1002u),
                IOException => ("io", 1001u),
                _ => ("other", 1099u),
            };
            result.Errors.Add(new ExtractionErrorItem
            {
                Index = 0,
                Code = code,
                ErrorType = errorType,
                Source = input.Filename ?? input.Uri ?? "input",
                Message = ex.Message,
            });
        }

        result.Summary = new ExtractionSummary
        {
            Inputs = 1,
            Results = result.Results.Count,
            Errors = result.Errors.Count,
        };
        return result;
    }

    public Task<ExtractionResult> ExtractAsync(ExtractInput input, ExtractionConfig config) =>
        Task.FromResult(Extract(input, config));

    private static (byte[] Bytes, string MimeType) Resolve(ExtractInput input, ExtractionConfig config)
    {
        bool sourceCode = config.Options.SourceCodeDetection;
        if (input.Kind == ExtractInputKind.Bytes)
        {
            byte[] bytes = input.Bytes ?? Array.Empty<byte>();
            string mime = input.MimeType
                ?? Mime.DetectMimeTypeFromBytes(bytes, sourceCode)
                ?? Mime.OctetStream;
            return (bytes, mime);
        }

        // URI / local path.
        string uri = input.Uri ?? throw new InvalidOperationException("ExtractInput.Uri is null");
        string path = uri.StartsWith("file://", StringComparison.Ordinal) ? new Uri(uri).LocalPath : uri;
        byte[] fileBytes = File.ReadAllBytes(path);
        // A caller-supplied type is taken at its word; otherwise the extension is only a starting
        // point, and the file's own content overrules it where the two disagree.
        string mimeType = input.MimeType
            ?? Mime.ResolveWithContent(
                Mime.DetectMimeType(path, checkExists: false, sourceCode), fileBytes, sourceCode);
        return (fileBytes, mimeType);
    }
}

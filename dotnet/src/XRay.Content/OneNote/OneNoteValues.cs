// [MS-ONE] 2.3.1 Time32, 2.3.32 FILETIME timestamps, 2.3.86 NoteTagShape, and the Graph's
// documented note-tag names (https://learn.microsoft.com/graph/onenote-note-tags).

namespace XRay.Content.OneNote;

internal static class OneNoteTime
{
    private static readonly DateTimeOffset Time32Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>[MS-ONE] 2.3.1: seconds since 1980-01-01 UTC.</summary>
    public static DateTimeOffset? FromTime32(ulong? value) => value is { } v and > 0 ? Time32Epoch.AddSeconds((uint)v) : null;

    /// <summary>[MS-DTYP] FILETIME: 100 ns ticks since 1601-01-01 UTC.</summary>
    public static DateTimeOffset? FromFileTime(ulong? value)
    {
        if (value is not { } v || v == 0 || v > 0x24C85A5ED1C03FFF) return null;
        return new DateTimeOffset(DateTime.FromFileTimeUtc((long)v), TimeSpan.Zero);
    }
}

/// <summary>[MS-ONE] 2.3.86 NoteTagShape.</summary>
public static class NoteTagShapes
{
    private static readonly HashSet<int> s_checkable = new()
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 28, 30, 32, 48, 50, 52, 69, 71, 73,
        89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99,
    };

    /// <summary>Whether the shape is a check box that can be ticked.</summary>
    public static bool IsCheckable(int shape) => s_checkable.Contains(shape);

    /// <summary>The task-tag shapes, 89-93 ([MS-ONE] 2.1.9).</summary>
    public static bool IsTaskShape(int shape) => shape is >= 89 and <= 93;
}

/// <summary>The note tags the Graph names in page HTML (<c>data-tag</c>).</summary>
public static class GraphNoteTags
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "to-do", "important", "question", "definition", "highlight", "contact", "address",
        "phone-number", "web-site-to-visit", "idea", "password", "critical", "project-a",
        "project-b", "remember-for-later", "movie-to-see", "book-to-read", "music-to-listen-to",
        "source-for-article", "remember-for-blog", "discuss-with-person-a", "discuss-with-person-b",
        "discuss-with-manager", "send-in-email", "schedule-meeting", "call-back",
        "to-do-priority-1", "to-do-priority-2", "client-request",
    };

    private static readonly HashSet<string> s_names = new(Names, StringComparer.Ordinal);

    /// <summary>The Graph name for a tag's label, when the label is one of OneNote's English
    /// defaults. Custom and localised labels have no Graph name.</summary>
    public static string? FromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char c in label.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string key = sb.ToString().Trim('-');
        if (key == "web-site-to-visit" || key == "website-to-visit") return "web-site-to-visit";
        return s_names.Contains(key) ? key : null;
    }
}

/// <summary>Media types of the data OneNote embeds.</summary>
internal static class OneNoteMedia
{
    public static string? Sniff(byte[]? data, string? extension)
    {
        if (data is { Length: >= 12 })
        {
            if (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return "image/png";
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
            if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return "image/gif";
            if (data[0] == 'B' && data[1] == 'M') return "image/bmp";
            if ((data[0] == 'I' && data[1] == 'I' && data[2] == 42) || (data[0] == 'M' && data[1] == 'M' && data[3] == 42)) return "image/tiff";
            if (data[0] == 0x01 && data[1] == 0 && data[2] == 0 && data[3] == 0 && data[40] == ' ' && data[41] == 'E' && data[42] == 'M' && data[43] == 'F') return "image/emf";
            if (data[0] == 0xD7 && data[1] == 0xCD && data[2] == 0xC6 && data[3] == 0x9A) return "image/wmf";
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') return "image/webp";
            if (data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F') return "application/pdf";
        }
        return FromExtension(extension);
    }

    public static string? FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        string ext = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" or ".dib" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".emf" => "image/emf",
            ".wmf" => "image/wmf",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".htm" or ".html" => "text/html",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".zip" => "application/zip",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".wma" => "audio/x-ms-wma",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".wmv" => "video/x-ms-wmv",
            ".avi" => "video/x-msvideo",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".xps" => "application/vnd.ms-xpsdocument",
            ".one" => "application/onenote",
            _ => null,
        };
    }
}

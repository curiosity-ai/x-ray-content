// OneNote's deep links, and the ids the Microsoft Graph gives notebooks, sections and pages.
// The link grammar is not specified by Microsoft; it is the one OneNote's "Copy Link to Page"
// and the Graph's links.oneNoteClientUrl / links.oneNoteWebUrl produce:
//   onenote:<section file URL>#<page title>&section-id={GUID}&page-id={GUID}&end
//   <notebook URL>?wd=target(<section path>.one|<section GUID>/<page title>|<page GUID>/)
// [MS-ONE] 2.2.58 is the one documented piece: a page's NotebookManagementEntityGuid is what a
// link to it carries.

using System.Text;
using System.Text.RegularExpressions;
using XRay.Content.OneNote.Store;

namespace XRay.Content.OneNote;

/// <summary>What a OneNote link points at.</summary>
public sealed record OneNoteLinkTarget
{
    /// <summary>The section file URL of an <c>onenote:</c> link, or the section path of a
    /// <c>wd=target</c> link (relative to the notebook, section groups included).</summary>
    public string? Section { get; init; }

    public Guid? SectionId { get; init; }
    public string? PageTitle { get; init; }
    public Guid? PageId { get; init; }

    /// <summary>The paragraph (or other object) a link points into, when it names one.</summary>
    public ExGuid? ObjectId { get; init; }

    /// <summary>The <c>base-path</c> of a link stored inside a page, relative to which
    /// <see cref="Section"/> resolves.</summary>
    public string? BasePath { get; init; }

    /// <summary>The <c>sourcedoc</c> of a SharePoint <c>Doc.aspx</c> link: the notebook folder.</summary>
    public Guid? SourceDoc { get; init; }

    /// <summary>The <c>wdsectionfileid</c> of a SharePoint link: the section's file in the library.</summary>
    public Guid? SectionFileId { get; init; }
}

/// <summary>Builds and parses OneNote links.</summary>
public static class OneNoteLinks
{
    /// <summary>The <c>onenote:</c> link that opens a page (or a paragraph of it) in the desktop
    /// app. <paramref name="sectionFileUrl"/> is the URL of the <c>.one</c> file — its SharePoint
    /// or OneDrive URL, or a <c>file:</c> path.</summary>
    public static string ClientUrl(string sectionFileUrl, Guid sectionId, string? pageTitle = null, Guid? pageId = null, ExGuid? objectId = null)
    {
        var sb = new StringBuilder("onenote:");
        sb.Append(EncodePath(sectionFileUrl)).Append('#');
        if (pageId is not null && pageTitle is not null) sb.Append(Uri.EscapeDataString(pageTitle)).Append('&');
        sb.Append("section-id=").Append(sectionId.ToString("B").ToUpperInvariant());
        if (pageId is { } p) sb.Append("&page-id=").Append(p.ToString("B").ToUpperInvariant());
        if (objectId is { } o && pageId is not null) sb.Append("&object-id=").Append(o.Guid.ToString("B").ToUpperInvariant()).Append('&').Append(o.N);
        else sb.Append("&end");
        return sb.ToString();
    }

    /// <summary>The browser link that opens a page in OneNote for the web.
    /// <paramref name="notebookUrl"/> is the notebook folder's URL as the drive reports it — the
    /// <c>webUrl</c> of the driveItem whose <c>package.type</c> is <c>oneNote</c>, for SharePoint
    /// a <c>_layouts/15/Doc.aspx?sourcedoc={...}</c> URL. <paramref name="sectionPath"/> is the
    /// section's path inside the notebook, e.g. <c>Group/Section.one</c>.</summary>
    public static string WebUrl(string notebookUrl, string sectionPath, Guid sectionId, string? pageTitle = null, Guid? pageId = null, ExGuid? objectId = null)
    {
        string url = notebookUrl;
        int hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];
        url = Regex.Replace(url, "([?&])(wd|wdpartid|wdsectionfileid|wdorigin|file)=[^&]*", "$1").Replace("?&", "?").Replace("&&", "&").TrimEnd('&', '?');
        if (url.Contains("Doc.aspx", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(url, "[?&]action=", RegexOptions.IgnoreCase)) url += (url.Contains('?') ? "&" : "?") + "action=edit";
        url += (url.Contains('?') ? "&" : "?") + "wd=" + Uri.EscapeDataString(WdTarget(sectionPath, sectionId, pageTitle, pageId));
        if (objectId is { } o) url += "&wdpartid=" + Uri.EscapeDataString(o.Guid.ToString("B") + "{" + o.N + "}");
        return url;
    }

    /// <summary>The unencoded <c>target(...)</c> of a web link.</summary>
    public static string WdTarget(string sectionPath, Guid sectionId, string? pageTitle = null, Guid? pageId = null)
    {
        var sb = new StringBuilder("target(");
        sb.Append(EscapeTarget(sectionPath.Replace('\\', '/').Trim('/'), keepSlashes: true)).Append('|').Append(sectionId.ToString("D")).Append('/');
        if (pageId is { } p)
            sb.Append(EscapeTarget(pageTitle ?? "", keepSlashes: false)).Append('|').Append(p.ToString("D")).Append('/');
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>The client link for a parsed page, given where its section file lives.</summary>
    public static string ClientUrl(OneNotePage page, string sectionFileUrl) =>
        ClientUrl(sectionFileUrl, page.Section?.Id ?? Guid.Empty, page.Title, page.Id);

    /// <summary>The web link for a parsed page, given its notebook's URL.</summary>
    public static string WebUrl(OneNotePage page, string notebookUrl) =>
        WebUrl(notebookUrl, page.Section?.RelativePath ?? page.Section?.FileName ?? "", page.Section?.Id ?? Guid.Empty, page.Title, page.Id);

    private static string EscapeTarget(string s, bool keepSlashes)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\\' or '|' or ')' or '(' || c == '/' && !keepSlashes) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string EncodePath(string url)
    {
        if (url.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) url = url[8..];
        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return new Uri(Path.GetFullPath(url)).AbsoluteUri;
        return url.Replace(" ", "%20");
    }

    private static readonly Regex s_guid = new(@"\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?", RegexOptions.Compiled);

    /// <summary>Parses an <c>onenote:</c> link, a <c>wd=target</c> web link, or the link text of
    /// a hyperlink stored inside a page.</summary>
    public static bool TryParse(string link, out OneNoteLinkTarget target)
    {
        target = new OneNoteLinkTarget();
        if (string.IsNullOrWhiteSpace(link)) return false;
        string s = link.Trim();

        if (s.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = s[8..];
            int hash = rest.IndexOf('#');
            string section = hash >= 0 ? rest[..hash] : rest;
            string fragment = hash >= 0 ? rest[(hash + 1)..] : "";
            var parts = fragment.Split('&');
            string? title = null;
            Guid? sectionId = null, pageId = null;
            ExGuid? objectId = null;
            string? basePath = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.StartsWith("section-id=", StringComparison.OrdinalIgnoreCase)) sectionId = ParseGuid(part[11..]);
                else if (part.StartsWith("page-id=", StringComparison.OrdinalIgnoreCase)) pageId = ParseGuid(part[8..]);
                else if (part.StartsWith("object-id=", StringComparison.OrdinalIgnoreCase) && ParseGuid(part[10..]) is { } og)
                    objectId = new ExGuid(og, i + 1 < parts.Length && uint.TryParse(parts[i + 1], out var n) ? n : 0);
                else if (part.StartsWith("base-path=", StringComparison.OrdinalIgnoreCase)) basePath = Uri.UnescapeDataString(part[10..]);
                else if (i == 0 && !part.Contains('=')) title = Uri.UnescapeDataString(part);
            }
            target = new OneNoteLinkTarget
            {
                Section = section.Length == 0 ? null : Uri.UnescapeDataString(section),
                SectionId = sectionId,
                PageId = pageId,
                PageTitle = title,
                ObjectId = objectId,
                BasePath = basePath,
            };
            return sectionId is not null || pageId is not null;
        }

        if (!Uri.TryCreate(s, System.UriKind.Absolute, out var uri)) return false;
        string? wd = null;
        Guid? sourceDoc = null, sectionFileId = null;
        ExGuid? part2 = null;
        foreach (var kv in uri.Query.TrimStart('?').Split('&'))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0) continue;
            string key = kv[..eq];
            string value = Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
            if (key.Equals("wd", StringComparison.OrdinalIgnoreCase)) wd = value;
            else if (key.Equals("sourcedoc", StringComparison.OrdinalIgnoreCase)) sourceDoc = ParseGuid(value);
            else if (key.Equals("wdsectionfileid", StringComparison.OrdinalIgnoreCase)) sectionFileId = ParseGuid(value);
            else if (key.Equals("wdpartid", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(value, @"\{?([0-9A-Fa-f-]{36})\}?\{(\d+)\}");
                if (m.Success) part2 = new ExGuid(Guid.Parse(m.Groups[1].Value), uint.Parse(m.Groups[2].Value));
            }
        }
        if (wd is null || !wd.StartsWith("target(", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = SplitTarget(wd[7..].TrimEnd(')'));
        if (segments.Count < 2) return false;
        target = new OneNoteLinkTarget
        {
            Section = segments[0],
            SectionId = ParseGuid(segments[1].TrimEnd('/')),
            PageTitle = segments.Count >= 3 ? segments[1].Contains('/') ? segments[1][(segments[1].IndexOf('/') + 1)..] : null : null,
            PageId = segments.Count >= 3 ? ParseGuid(segments[2].TrimEnd('/')) : null,
            ObjectId = part2,
            SourceDoc = sourceDoc,
            SectionFileId = sectionFileId,
        };
        if (segments.Count >= 3)
        {
            int slash = segments[1].IndexOf('/');
            target = target with { SectionId = ParseGuid(slash > 0 ? segments[1][..slash] : segments[1]) };
        }
        return target.SectionId is not null;
    }

    /// <summary>Splits <c>a|b/c|d/</c> on unescaped <c>|</c>, dropping the backslash escapes.</summary>
    private static List<string> SplitTarget(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[++i]);
                continue;
            }
            if (c == '|')
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static Guid? ParseGuid(string s)
    {
        var m = s_guid.Match(Uri.UnescapeDataString(s));
        return m.Success ? Guid.Parse(m.Groups[1].Value) : null;
    }
}

/// <summary>A Microsoft Graph OneNote id: <c>1-{guid}</c> for a SharePoint or OneDrive for
/// Business notebook or section, <c>0-{driveItem id}</c> for a personal one, and
/// <c>{prefix}-{32 hex}!{n}-{parent section id}</c> for a page. The parts after the prefix are
/// not documented; what is known is that a page id embeds its section's id, and that neither
/// equals the GUIDs a OneNote file and its links carry — match those through
/// <c>links.oneNoteClientUrl</c> instead (<see cref="OneNoteLinks.TryParse"/>).</summary>
public sealed record GraphOneNoteId(string Raw, int Prefix, string Key, string? PageKey, int? PageNumber, string? ParentSectionId)
{
    public bool IsPage => PageKey is not null;

    /// <summary>The GUID of a business notebook or section id (<c>1-{guid}</c>).</summary>
    public Guid? Guid => System.Guid.TryParse(Key, out var g) ? g : null;

    public static bool TryParse(string id, out GraphOneNoteId parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(id)) return false;
        var m = Regex.Match(id, @"^(\d+)-([0-9A-Fa-f]{32})!(\d+)-(.+)$");
        if (m.Success)
        {
            parsed = new GraphOneNoteId(id, int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value), m.Groups[1].Value + "-" + m.Groups[4].Value);
            return true;
        }
        m = Regex.Match(id, @"^(\d+)-(.+)$");
        if (!m.Success) return false;
        parsed = new GraphOneNoteId(id, int.Parse(m.Groups[1].Value), m.Groups[2].Value, null, null, null);
        return true;
    }
}

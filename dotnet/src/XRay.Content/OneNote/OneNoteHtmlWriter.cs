// Renders a page as the HTML the Microsoft Graph returns for GET .../onenote/pages/{id}/content
// ("output HTML", https://learn.microsoft.com/graph/onenote-output-html): absolutely positioned
// outline divs, element ids built from object ids, data-tag on tagged paragraphs, <img> with
// data-src-type, <object data-attachment> for attachments. Ink, which the Graph leaves out, is
// optionally drawn as SVG.

using System.Globalization;
using System.Net;
using System.Text;
using XRay.Content.OneNote.Store;

namespace XRay.Content.OneNote;

/// <summary>Options for <see cref="OneNoteHtmlWriter"/>.</summary>
public sealed class OneNoteHtmlOptions
{
    /// <summary>Position outlines, images and attachments where the page places them, as the
    /// Graph does. When false the content flows top to bottom in page order.</summary>
    public bool AbsolutePositioning { get; init; } = true;

    /// <summary>Embed images as <c>data:</c> URIs when <see cref="ResourceUrl"/> gives no URL.</summary>
    public bool EmbedImages { get; init; } = true;

    /// <summary>Largest image embedded as a data URI, in bytes.</summary>
    public long MaxEmbeddedImageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>The URL to reference an image's or attachment's data by — a blob store, or the
    /// Graph's <c>.../onenote/resources/{id}/$value</c>. Null falls back to embedding.</summary>
    public Func<OneNoteElement, FileDataReference, string?>? ResourceUrl { get; init; }

    /// <summary>Draw ink strokes as inline SVG. The Graph's output omits ink.</summary>
    public bool IncludeInk { get; init; } = true;

    /// <summary>Show the title block (title, date, time) in the body, as OneNote does. The
    /// Graph puts the title only in <c>&lt;title&gt;</c>.</summary>
    public bool IncludeTitleBlock { get; init; }

    public static readonly OneNoteHtmlOptions Default = new();
}

/// <summary>Writes OneNote pages as HTML.</summary>
public static class OneNoteHtmlWriter
{
    /// <summary>One half-inch, the unit [MS-ONE] measures layout in, in CSS pixels.</summary>
    private const double PixelsPerUnit = 48.0;

    public static string Write(OneNotePage page, OneNoteHtmlOptions? options = null)
    {
        options ??= OneNoteHtmlOptions.Default;
        var w = new Writer(options);
        w.Page(page);
        return w.ToString();
    }

    /// <summary>The id the Graph's page HTML gives an element read from <paramref name="objectId"/>,
    /// e.g. <c>p:{4f5c46b3-265d-0e58-3101-8fb2a117edf3}{12}</c>.</summary>
    public static string ElementId(string prefix, ExGuid objectId) => $"{prefix}:{objectId.Guid.ToString("B")}{{{objectId.N}}}";

    private sealed class Writer
    {
        private readonly OneNoteHtmlOptions _o;
        private readonly StringBuilder _sb = new();

        public Writer(OneNoteHtmlOptions options) => _o = options;

        public override string ToString() => _sb.ToString();

        public void Page(OneNotePage page)
        {
            _sb.Append("<html lang=\"").Append(Lang(page)).Append("\">\n<head>\n<title>").Append(Enc(page.Title)).Append("</title>\n");
            _sb.Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />\n");
            if (page.CreatedDateTime is { } created)
                _sb.Append("<meta name=\"created\" content=\"").Append(created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append("\" />\n");
            if (page.LastModifiedDateTime is { } modified)
                _sb.Append("<meta name=\"lastModified\" content=\"").Append(modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append("\" />\n");
            _sb.Append("<meta name=\"onenote-page-id\" content=\"").Append(page.Id.ToString("B")).Append("\" />\n");
            _sb.Append("</head>\n<body data-absolute-enabled=\"").Append(_o.AbsolutePositioning ? "true" : "false").Append("\" style=\"font-family:Calibri;font-size:11pt\"");
            if (page.IsRightToLeft) _sb.Append(" dir=\"rtl\"");
            _sb.Append(">\n");

            if (_o.IncludeTitleBlock && page.TitleBlock is { } title)
            {
                _sb.Append("<div data-id=\"_title\">\n<h1 style=\"font-size:20pt;margin-top:0pt;margin-bottom:0pt\">").Append(Enc(title.Text)).Append("</h1>\n");
                string when = string.Join(" ", new[] { title.Date, title.Time }.Where(s => !string.IsNullOrEmpty(s)));
                if (when.Length > 0) _sb.Append("<p style=\"color:#767676;font-size:10pt;margin-top:0pt;margin-bottom:0pt\">").Append(Enc(when)).Append("</p>\n");
                _sb.Append("</div>\n");
            }

            bool first = true;
            foreach (var element in page.Content)
            {
                switch (element)
                {
                    case OneNoteOutline outline:
                        _sb.Append("<div id=\"").Append(ElementId("div", outline.ObjectId)).Append('"');
                        if (first) _sb.Append(" data-id=\"_default\"");
                        Position(outline.Layout, includeHeight: false);
                        _sb.Append(">\n");
                        Children(outline.Children);
                        _sb.Append("</div>\n");
                        first = false;
                        break;
                    case OneNoteImage image:
                        Image(image, positioned: true);
                        break;
                    case OneNoteEmbeddedFile file:
                        File(file, positioned: true);
                        break;
                    case OneNoteInk ink when _o.IncludeInk:
                        Ink(ink, positioned: true);
                        break;
                    case OneNoteEmbeddedMedia media:
                        Media(media);
                        break;
                    default:
                        Element(element);
                        break;
                }
            }
            _sb.Append("</body>\n</html>\n");
        }

        private static string Lang(OneNotePage page)
        {
            uint? lcid = page.Descendants().OfType<OneNoteRichText>().Select(t => t.LanguageId).FirstOrDefault(l => l is not null);
            if (lcid is { } id)
            {
                try
                {
                    return CultureInfo.GetCultureInfo((int)id).Name is { Length: > 0 } name ? name : "en-US";
                }
                catch (ArgumentException)
                {
                }
            }
            return "en-US";
        }

        private void Position(OneNoteLayout layout, bool includeHeight)
        {
            if (!_o.AbsolutePositioning || layout.X is null && layout.Y is null) return;
            _sb.Append(" style=\"position:absolute;left:").Append(Px(layout.X ?? 0)).Append("px;top:").Append(Px(layout.Y ?? 0)).Append("px");
            if (layout.Width is { } w) _sb.Append(";width:").Append(Px(w)).Append("px");
            if (includeHeight && layout.Height is { } h) _sb.Append(";height:").Append(Px(h)).Append("px");
            _sb.Append('"');
        }

        private static string Px(float units) => Math.Round(units * PixelsPerUnit).ToString(CultureInfo.InvariantCulture);

        private void Children(IReadOnlyList<OneNoteOutlineElement> children)
        {
            int i = 0;
            while (i < children.Count)
            {
                var oe = children[i];
                if (oe.List is { } marker && oe.Content is OneNoteRichText)
                {
                    bool ordered = marker.IsNumbered;
                    _sb.Append(ordered ? "<ol>\n" : "<ul>\n");
                    while (i < children.Count && children[i].List is { } m && m.IsNumbered == ordered && children[i].Content is OneNoteRichText text)
                    {
                        _sb.Append("<li id=\"").Append(ElementId("li", children[i].ObjectId)).Append('"');
                        if (ordered && m.Number is { } n) _sb.Append(" value=\"").Append(n).Append('"');
                        if (!ordered && m.Text.Length > 0 && m.Text != "•") _sb.Append(" style=\"list-style-type:'").Append(Enc(m.Text)).Append(" '\"");
                        Tags(text.NoteTags);
                        _sb.Append('>');
                        Inline(text);
                        if (children[i].Children.Count > 0)
                        {
                            _sb.Append('\n');
                            Children(children[i].Children);
                        }
                        _sb.Append("</li>\n");
                        i++;
                    }
                    _sb.Append(ordered ? "</ol>\n" : "</ul>\n");
                    continue;
                }
                if (oe.Content is not null) Element(oe.Content);
                if (oe.Children.Count > 0)
                {
                    _sb.Append("<div style=\"margin-left:36px\">\n");
                    Children(oe.Children);
                    _sb.Append("</div>\n");
                }
                i++;
            }
        }

        private void Element(OneNoteElement element)
        {
            switch (element)
            {
                case OneNoteRichText text: Paragraph(text); break;
                case OneNoteTable table: Table(table); break;
                case OneNoteImage image: Image(image, positioned: false); break;
                case OneNoteEmbeddedFile file: File(file, positioned: false); break;
                case OneNoteInk ink when _o.IncludeInk: Ink(ink, positioned: false); break;
                case OneNoteEmbeddedMedia media: Media(media); break;
                case OneNoteOutlineElement oe: Children(new[] { oe }); break;
                case OneNoteOutline outline: Children(outline.Children); break;
            }
        }

        private void Paragraph(OneNoteRichText text)
        {
            string tag = text.HeadingLevel is { } h ? "h" + h : "p";
            _sb.Append('<').Append(tag).Append(" id=\"").Append(ElementId(tag, text.ObjectId)).Append('"');
            var style = new StringBuilder("margin-top:0pt;margin-bottom:0pt");
            if (text.ParagraphStyle?.StyleId == "code") style.Append(";font-family:Consolas");
            if (text.ParagraphStyle?.StyleId == "cite") style.Append(";font-style:italic;color:#595959");
            if (text.HeadingLevel is not null && text.ParagraphStyle is { } ps)
            {
                if (ps.FontSize is { } size) style.Append(";font-size:").Append(size.ToString(CultureInfo.InvariantCulture)).Append("pt");
                if (ps.FontColor is { IsAutomatic: false } color) style.Append(";color:").Append(color.ToHex());
            }
            if (text.Alignment is 1) style.Append(";text-align:center");
            else if (text.Alignment is 2) style.Append(";text-align:right");
            _sb.Append(" style=\"").Append(style).Append('"');
            if (text.IsRightToLeft) _sb.Append(" dir=\"rtl\"");
            Tags(text.NoteTags);
            _sb.Append('>');
            Inline(text);
            _sb.Append("</").Append(tag).Append(">\n");
        }

        private void Tags(List<OneNoteNoteTag> tags)
        {
            if (tags.Count == 0) return;
            var named = tags.Select(t => t.GraphDataTag).Where(t => t is not null).ToList();
            if (named.Count > 0) _sb.Append(" data-tag=\"").Append(Enc(string.Join(",", named))).Append('"');
            var custom = tags.Where(t => t.GraphDataTag is null).Select(t => t.Label ?? $"shape-{t.Shape}").ToList();
            if (custom.Count > 0) _sb.Append(" data-onenote-tag=\"").Append(Enc(string.Join(",", custom))).Append('"');
        }

        private void Inline(OneNoteRichText text)
        {
            int linkEnd = -1;
            foreach (var run in text.Runs)
            {
                var link = text.Hyperlinks.FirstOrDefault(l => l.Start == run.Start);
                if (link is not null && run.Start >= linkEnd)
                {
                    _sb.Append("<a href=\"").Append(Enc(link.Url)).Append("\">");
                    linkEnd = link.Start + link.Length;
                }
                string css = RunStyle(run.Style, inLink: linkEnd > run.Start);
                string body = Enc(run.Text).Replace("\n", "<br />");
                if (css.Length > 0) _sb.Append("<span style=\"").Append(css).Append("\">").Append(body).Append("</span>");
                else _sb.Append(body);
                if (linkEnd >= 0 && run.Start + run.Length >= linkEnd)
                {
                    _sb.Append("</a>");
                    linkEnd = -1;
                }
            }
            if (linkEnd >= 0) _sb.Append("</a>");
            foreach (var ink in text.InlineInk)
                if (_o.IncludeInk) Ink(ink, positioned: false);
        }

        private static string RunStyle(OneNoteTextStyle s, bool inLink)
        {
            var css = new List<string>();
            if (s.Font is { Length: > 0 } font && font != "Calibri") css.Add("font-family:" + font.Replace("\"", "").Replace(";", ""));
            if (s.FontSize is { } size && Math.Abs(size - 11) > 0.01) css.Add("font-size:" + size.ToString(CultureInfo.InvariantCulture) + "pt");
            if (s.FontColor is { IsAutomatic: false } color) css.Add("color:" + color.ToHex());
            if (s.Highlight is { IsAutomatic: false } hl) css.Add("background-color:" + hl.ToHex());
            if (s.Bold) css.Add("font-weight:bold");
            if (s.Italic) css.Add("font-style:italic");
            var deco = new List<string>();
            if (s.Underline && !inLink) deco.Add("underline");
            if (s.Strikethrough) deco.Add("line-through");
            if (deco.Count > 0) css.Add("text-decoration:" + string.Join(" ", deco));
            if (s.Superscript) css.Add("vertical-align:super");
            if (s.Subscript) css.Add("vertical-align:sub");
            return string.Join(";", css);
        }

        private void Table(OneNoteTable table)
        {
            string border = table.BordersVisible ? "1px solid" : "0px";
            _sb.Append("<table id=\"").Append(ElementId("table", table.ObjectId)).Append("\" style=\"border:").Append(border).Append(";border-collapse:collapse\"");
            Tags(table.NoteTags);
            _sb.Append(">\n");
            foreach (var row in table.Rows)
            {
                _sb.Append("<tr id=\"").Append(ElementId("tr", row.ObjectId)).Append("\">\n");
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    var cell = row.Cells[c];
                    _sb.Append("<td id=\"").Append(ElementId("td", cell.ObjectId)).Append("\" style=\"border:").Append(border);
                    if (c < table.ColumnWidths.Count) _sb.Append(";width:").Append(Px(table.ColumnWidths[c])).Append("px");
                    if (cell.Background is { IsAutomatic: false } bg) _sb.Append(";background-color:").Append(bg.ToHex());
                    _sb.Append("\">");
                    Children(cell.Children);
                    _sb.Append("</td>\n");
                }
                _sb.Append("</tr>\n");
            }
            _sb.Append("</table>\n");
        }

        private void Image(OneNoteImage image, bool positioned)
        {
            string type = image.ContentType ?? "image/png";
            string? src = image.Data is { } data ? Url(image, data, type) : null;
            if (image.Hyperlink is { Length: > 0 } link) _sb.Append("<a href=\"").Append(Enc(link)).Append("\">");
            _sb.Append("<img id=\"").Append(ElementId("img", image.ObjectId)).Append('"');
            if (positioned) Position(image.Layout, includeHeight: false);
            if (image.Width is { } w) _sb.Append(" width=\"").Append(Px(w)).Append('"');
            if (image.Height is { } h) _sb.Append(" height=\"").Append(Px(h)).Append('"');
            if ((image.AltText ?? image.FileName) is { } alt) _sb.Append(" alt=\"").Append(Enc(alt)).Append('"');
            if (src is not null)
            {
                _sb.Append(" src=\"").Append(Enc(src)).Append("\" data-src-type=\"").Append(type).Append('"');
                _sb.Append(" data-fullres-src=\"").Append(Enc(src)).Append("\" data-fullres-src-type=\"").Append(type).Append('"');
            }
            if (image.OcrText is { Length: > 0 } ocr) _sb.Append(" data-text=\"").Append(Enc(ocr)).Append('"');
            Tags(image.NoteTags);
            _sb.Append(" />");
            if (image.Hyperlink is { Length: > 0 }) _sb.Append("</a>");
            _sb.Append('\n');
        }

        private void File(OneNoteEmbeddedFile file, bool positioned)
        {
            string type = file.ContentType ?? "application/octet-stream";
            _sb.Append("<object id=\"").Append(ElementId("object", file.ObjectId)).Append('"');
            if (positioned) Position(file.Layout, includeHeight: false);
            _sb.Append(" data-attachment=\"").Append(Enc(file.FileName ?? "attachment")).Append("\" type=\"").Append(type).Append('"');
            if (file.Data is { } data && _o.ResourceUrl?.Invoke(file, data) is { } url) _sb.Append(" data=\"").Append(Enc(url)).Append('"');
            Tags(file.NoteTags);
            _sb.Append(" />\n");
        }

        private void Media(OneNoteEmbeddedMedia media)
        {
            if (media.Url is not { Length: > 0 } url) return;
            _sb.Append("<iframe id=\"").Append(ElementId("iframe", media.ObjectId)).Append('"');
            Position(media.Layout, includeHeight: true);
            _sb.Append(" data-original-src=\"").Append(Enc(url)).Append("\" src=\"").Append(Enc(url)).Append("\"></iframe>\n");
        }

        private void Ink(OneNoteInk ink, bool positioned)
        {
            var strokes = ink.AllStrokes().Where(s => s.Points.Count > 0).ToList();
            if (strokes.Count == 0 || ink.BoundingBox is not { } box) return;
            double scale = PixelsPerUnit / 1270.0;
            double width = Math.Max(1, box.Width * scale), height = Math.Max(1, box.Height * scale);
            _sb.Append("<svg id=\"").Append(ElementId("ink", ink.ObjectId)).Append("\" xmlns=\"http://www.w3.org/2000/svg\" width=\"")
                .Append(Num(width)).Append("\" height=\"").Append(Num(height)).Append("\" viewBox=\"")
                .Append(Num(box.X)).Append(' ').Append(Num(box.Y)).Append(' ').Append(Num(Math.Max(1, box.Width))).Append(' ').Append(Num(Math.Max(1, box.Height))).Append('"');
            if (positioned && _o.AbsolutePositioning)
            {
                double left = (ink.Layout.X ?? 0) * PixelsPerUnit + box.X * scale;
                double top = (ink.Layout.Y ?? 0) * PixelsPerUnit + box.Y * scale;
                _sb.Append(" style=\"position:absolute;left:").Append(Num(left)).Append("px;top:").Append(Num(top)).Append("px\"");
            }
            _sb.Append(">\n");
            foreach (var stroke in strokes)
            {
                _sb.Append("<path fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"").Append(stroke.Color is { IsAutomatic: false } c ? c.ToHex() : "#000000")
                    .Append("\" stroke-width=\"").Append(Num(Math.Max(1, stroke.Width))).Append('"');
                if (stroke.Transparency is { } t and > 0) _sb.Append(" stroke-opacity=\"").Append(Num((255 - t) / 255.0)).Append('"');
                _sb.Append(" d=\"M");
                for (int i = 0; i < stroke.Points.Count; i++)
                {
                    if (i > 0) _sb.Append(" L");
                    _sb.Append(Num(stroke.Points[i].X)).Append(' ').Append(Num(stroke.Points[i].Y));
                }
                _sb.Append("\" />\n");
            }
            _sb.Append("</svg>\n");
        }

        private string? Url(OneNoteElement element, FileDataReference data, string type)
        {
            if (_o.ResourceUrl?.Invoke(element, data) is { } url) return url;
            if (!_o.EmbedImages || data.Length is > 0 && data.Length > _o.MaxEmbeddedImageBytes) return null;
            var bytes = data.ReadAll();
            if (bytes is null || bytes.Length == 0 || bytes.Length > _o.MaxEmbeddedImageBytes) return null;
            return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
        }

        private static string Num(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        private static string Enc(string s) => WebUtility.HtmlEncode(s);
    }
}

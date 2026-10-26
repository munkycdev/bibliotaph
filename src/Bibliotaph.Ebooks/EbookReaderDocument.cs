using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Bibliotaph.Ebooks;

/// <summary>
/// The page the reader shows an e-book on: every chapter one after another, cleaned of anything that could run or
/// reach out, with an empty <c>bt-page</c> element where each of its pages starts, so the reader can go to a page and
/// say which page is in view. A fixed-layout book shows each chapter file as drawn, in a frame of its own.
/// </summary>
/// <remarks>
/// The page knows nothing of the web: pictures and stylesheets are asked for at <see cref="ResourcePrefix"/> plus their
/// path in the archive, which the reader answers from the book. Links within the book go to the place they name; links
/// to the web keep their address for the reader to offer; everything else that names an address is dropped.
/// </remarks>
public static partial class EbookReaderDocument
{
    /// <summary>Where a file in the archive is asked for, by its path.</summary>
    public const string ResourcePrefix = "/res/";

    /// <summary>The element that marks a page's start, with the page's index in <c>data-page</c>.</summary>
    public const string MarkerName = "bt-page";

    /// <summary>Deeper than this, an element's content is kept as its text: a crafted file could nest elements without end.</summary>
    const int MaxDepth = 200;

    const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Taken out with everything in them: they run, embed, ask or fetch.</summary>
    static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "link", "meta", "base", "iframe", "frame", "frameset", "object", "embed", "applet", "audio", "video",
        "source", "track", "canvas", "noscript", "template", "title", "head", "input", "select", "textarea", "option", "portal",
        "foreignObject", "animate", "animateMotion", "animateTransform", "set", "handler", "listener", "dialog",
    };

    /// <summary>Replaced by what is in them: a form or a button around text keeps the text, not the control.</summary>
    static readonly HashSet<string> Unwrapped = new(StringComparer.OrdinalIgnoreCase) { "form", "button", "label", "fieldset", "legend", "font", "html", "body" };

    /// <summary>Kept on HTML elements; every other attribute (style, class, events, addresses) goes.</summary>
    static readonly HashSet<string> KeptAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "alt", "title", "lang", "dir", "colspan", "rowspan", "start", "reversed", "value", "type", "scope", "headers", "abbr",
        "datetime", "role", "epub:type",
    };

    /// <summary>
    /// The reader's page for <paramref name="book"/> cut into <paramref name="layout"/>'s pages. <paramref name="head"/>
    /// goes in the page's head (the reader's stylesheet) and <paramref name="script"/> at the end of its body. The
    /// book's files are asked for under <paramref name="resources"/>, which a browser holding several books at once
    /// makes the book's own.
    /// </summary>
    public static string Build(IEbook book, EbookLayout layout, string head, string script, string resources = ResourcePrefix,
        CancellationToken ct = default)
    {
        var fixedLayout = book.Spine.Count > 0 && book.Spine.All(s => s.FixedLayout);
        var chapterOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < book.Spine.Count; i++) chapterOf.TryAdd(book.Spine[i].Path, i);
        var starts = layout.Pages.GroupBy(p => p.Start.Chapter).ToDictionary(g => g.Key, g => g.ToList());

        var html = new StringBuilder();
        var language = book.Metadata.Language is { Length: > 0 and <= 35 } lang && LanguageTag().IsMatch(lang) ? lang : "en";
        html.Append("<!DOCTYPE html><html lang=\"").Append(language).Append("\"><head><meta charset=\"utf-8\">");
        html.Append("<title>").Append(WebUtility.HtmlEncode(book.Metadata.Title ?? "")).Append("</title>");
        html.Append(head).Append("</head><body class=\"").Append(fixedLayout ? "bt-fixed" : "bt-flow").Append("\"><main id=\"bt-book\">");
        for (var c = 0; c < book.Spine.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            var pages = starts.GetValueOrDefault(c) ?? [];
            var item = book.Spine[c];
            html.Append("<section class=\"bt-chapter\" id=\"bt-c").Append(c.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-chapter=\"").Append(c.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (fixedLayout)
            {
                var (width, height) = Viewport(book, item.Path);
                html.Append(" data-width=\"").Append(width.ToString(CultureInfo.InvariantCulture))
                    .Append("\" data-height=\"").Append(height.ToString(CultureInfo.InvariantCulture)).Append("\">");
                foreach (var page in pages) html.Append(Marker(page.Index));
                html.Append("<iframe class=\"bt-sheet\" sandbox=\"allow-same-origin\" loading=\"lazy\" title=\"Page ")
                    .Append((pages.FirstOrDefault()?.Index + 1 ?? c + 1).ToString(CultureInfo.InvariantCulture))
                    .Append("\" src=\"").Append(WebUtility.HtmlEncode(ResourceUrl(item.Path, resources))).Append("\"></iframe>");
            }
            else
            {
                html.Append('>');
                html.Append(Chapter(book, c, pages, chapterOf, resources));
            }
            html.Append("</section>");
        }
        html.Append("</main>").Append(script).Append("</body></html>");
        return html.ToString();
    }

    /// <summary>A file's address on the reader's page: <see cref="ResourcePrefix"/> (or another) and its path, each part escaped.</summary>
    public static string ResourceUrl(string path, string resources = ResourcePrefix) =>
        resources + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>The archive path a reader's address asks for, or null when it isn't one of <see cref="ResourceUrl"/>'s.</summary>
    public static string? ResourcePath(string absolutePath) =>
        absolutePath.StartsWith(ResourcePrefix, StringComparison.Ordinal) && absolutePath.Length > ResourcePrefix.Length
            ? string.Join('/', absolutePath[ResourcePrefix.Length..].Split('/').Select(Uri.UnescapeDataString))
            : null;

    static string Marker(int page) =>
        $"<{MarkerName} id=\"bt-p{page.ToString(CultureInfo.InvariantCulture)}\" data-page=\"{page.ToString(CultureInfo.InvariantCulture)}\"></{MarkerName}>";

    /// <summary>One chapter's body, cleaned, its ids and links made to work on the one page, with its pages' markers.</summary>
    static string Chapter(IEbook book, int chapter, List<EbookPage> pages, Dictionary<string, int> chapterOf, string resources)
    {
        var item = book.Spine[chapter];
        if (book.IsEncrypted(item.Path)) return string.Concat(pages.Select(p => Marker(p.Index)));
        if (item.MediaType == "image/svg+xml")
            return string.Concat(pages.Select(p => Marker(p.Index))) + $"<img src=\"{WebUtility.HtmlEncode(ResourceUrl(item.Path, resources))}\" alt=\"\">";

        var document = HtmlText.Parse(book.ReadText(item.Path));
        var body = (IElement?)document.Body ?? document.DocumentElement;
        // A marker written into the book itself would be taken for one of the reader's.
        foreach (var planted in body.QuerySelectorAll(MarkerName).ToList()) planted.Remove();
        var markers = new HashSet<IElement>();
        Mark(document, body, pages, markers);
        new Cleaner(book, item.Path, chapter, chapterOf, markers, resources).Clean(body, 0);
        return body.InnerHtml;
    }

    /// <summary>
    /// Puts each page's marker where its first block starts: before the element the block opens with, or, for a page
    /// that starts part way through a long paragraph, between the words where its text starts.
    /// </summary>
    static void Mark(IHtmlDocument document, IElement body, List<EbookPage> pages, HashSet<IElement> markers)
    {
        var (blocks, sources) = HtmlText.Sources(document);
        // Back to front, so splitting a text node for one page leaves the nodes found for earlier pages as they were.
        foreach (var page in Enumerable.Reverse(pages))
        {
            var marker = document.CreateElement(MarkerName);
            marker.Id = "bt-p" + page.Index.ToString(CultureInfo.InvariantCulture);
            marker.SetAttribute("data-page", page.Index.ToString(CultureInfo.InvariantCulture));
            markers.Add(marker);

            var (block, offset) = (page.Start.Block, page.Start.Offset);
            if (block <= 0 && offset == 0) Place(marker, body, body.FirstChild, markers);
            else if (block >= sources.Count) Place(marker, body, null, markers);
            else if (offset > 0 && Split(sources[block], Visible(blocks[block].Text[..Math.Min(offset, blocks[block].Text.Length)])) is { } at)
                Place(marker, at.Parent!, at, markers);
            else if ((sources[block].FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Data)) ?? sources[block].FirstOrDefault()) is { } first)
            {
                var start = Opening(first, body);
                Place(marker, start.Parent!, start, markers);
            }
            else Place(marker, body, body.FirstChild, markers);
        }
    }

    /// <summary>
    /// Puts a marker before <paramref name="before"/> (or at the end), and before the markers of later pages already
    /// there, so pages that start at the same place (a blank printed page) keep their order.
    /// </summary>
    static void Place(IElement marker, INode parent, INode? before, HashSet<IElement> markers)
    {
        var at = before;
        for (var previous = before is null ? parent.LastChild : before.PreviousSibling; previous is not null; previous = previous.PreviousSibling)
        {
            if (previous is IElement element && markers.Contains(element)) at = previous;
            else if (previous is not IText text || !string.IsNullOrWhiteSpace(text.Data)) break;
        }
        parent.InsertBefore(marker, at);
    }

    static int Visible(string text) => text.Count(c => !char.IsWhiteSpace(c));

    /// <summary>
    /// The text node whose first character is the <paramref name="before"/>th visible character of a block, split off
    /// there. White space is collapsed in the block's text, so the place is found by counting the other characters.
    /// </summary>
    static IText? Split(List<IText> nodes, int before)
    {
        var seen = 0;
        foreach (var node in nodes)
        {
            var data = node.Data;
            for (var i = 0; i < data.Length; i++)
            {
                if (char.IsWhiteSpace(data[i])) continue;
                if (seen == before) return i == 0 ? node : node.Split(i);
                seen++;
            }
        }
        return null;
    }

    /// <summary>
    /// The outermost element that starts with <paramref name="node"/>, so a heading's marker goes before the heading
    /// rather than inside it.
    /// </summary>
    static INode Opening(INode node, IElement body)
    {
        var at = node;
        while (at.Parent is IElement parent && !ReferenceEquals(parent, body) && FirstVisible(parent) is { } first && ReferenceEquals(first, at)) at = parent;
        return at;
    }

    static INode? FirstVisible(IElement parent)
    {
        foreach (var child in parent.ChildNodes)
            if (Visible(child)) return child;
        return null;
    }

    static bool Visible(INode node) => node switch
    {
        IText text => !string.IsNullOrWhiteSpace(text.Data),
        IElement element => element.LocalName is "img" or "svg" or "hr" or "image" or "table"
            || !string.IsNullOrWhiteSpace(element.TextContent) || element.QuerySelector("img, svg, image") is not null,
        _ => false,
    };

    /// <summary>The size a fixed-layout page was drawn at, from its viewport, else a 2:3 page.</summary>
    static (int Width, int Height) Viewport(IEbook book, string path)
    {
        try
        {
            if (!book.IsEncrypted(path))
            {
                var text = book.ReadText(path);
                if (ViewportMeta().Match(text.Length > 8192 ? text[..8192] : text) is { Success: true } meta
                    && WidthOf().Match(meta.Groups[1].Value) is { Success: true } w && HeightOf().Match(meta.Groups[1].Value) is { Success: true } h
                    && int.TryParse(w.Groups[1].Value, CultureInfo.InvariantCulture, out var width) && int.TryParse(h.Groups[1].Value, CultureInfo.InvariantCulture, out var height)
                    && width is > 0 and <= 20000 && height is > 0 and <= 20000)
                    return (width, height);
            }
        }
        catch (EbookException)
        {
            // A page that can't be read gets the usual size, and its frame shows nothing.
        }
        return (600, 900);
    }

    /// <summary>
    /// A fixed-layout page as its frame shows it: its own stylesheets and layout kept, anything that runs, embeds or
    /// asks taken out, and nothing linked.
    /// </summary>
    public static string FixedPage(IEbook book, string path)
    {
        var document = HtmlText.Parse(book.ReadText(path));
        foreach (var element in document.QuerySelectorAll("*").ToList())
        {
            var name = element.LocalName;
            var keep = name switch
            {
                "link" => element.GetAttribute("rel")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase) == true,
                "meta" => element.GetAttribute("name") == "viewport",
                "style" or "head" or "title" => true,
                _ => !Dropped.Contains(name),
            };
            if (!keep)
            {
                element.Remove();
                continue;
            }
            foreach (var attribute in element.Attributes.ToList())
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.Equals("srcset", StringComparison.OrdinalIgnoreCase)
                    || name == "a" && attribute.Name.EndsWith("href", StringComparison.OrdinalIgnoreCase)
                    || IsAddress(attribute.Name) && HasScheme(attribute.Value))
                    element.RemoveAttribute(attribute.NamespaceUri, attribute.LocalName);
            }
        }
        return "<!DOCTYPE html>" + document.DocumentElement.OuterHtml;
    }

    /// <summary>
    /// A file of the book the reader's page asks for: a picture, a stylesheet or a font, or a fixed-layout page cleaned
    /// for its frame. Null for anything else (a chapter as written, which could run scripts), or what isn't there.
    /// </summary>
    public static (byte[] Bytes, string MediaType)? ReadFile(IEbook book, string path)
    {
        if (!book.Contains(path) || book.IsEncrypted(path)) return null;
        try
        {
            var type = book.MediaTypeOf(path) is { Length: > 0 } declared ? declared : TypeOf(path);
            if (book.Spine.Any(s => s.Path == path) && type != "image/svg+xml")
                return book.Spine.Count > 0 && book.Spine.All(s => s.FixedLayout) ? (Encoding.UTF8.GetBytes(FixedPage(book, path)), "text/html") : null;
            return type is { } served && Served(served) ? (book.ReadBytes(path), served) : null;
        }
        catch (Exception ex) when (ex is EbookException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    static bool Served(string type) => type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("font/", StringComparison.Ordinal)
        || type is "text/css" or "application/font-woff" or "application/font-sfnt" or "application/vnd.ms-opentype" or "application/x-font-ttf";

    static string? TypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".css" => "text/css",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        _ => null,
    };

    static bool IsAddress(string attribute) => attribute.EndsWith("href", StringComparison.OrdinalIgnoreCase)
        || attribute.Equals("src", StringComparison.OrdinalIgnoreCase) || attribute.Equals("poster", StringComparison.OrdinalIgnoreCase)
        || attribute.Equals("background", StringComparison.OrdinalIgnoreCase) || attribute.Equals("action", StringComparison.OrdinalIgnoreCase)
        || attribute.Equals("data", StringComparison.OrdinalIgnoreCase) || attribute.Equals("formaction", StringComparison.OrdinalIgnoreCase);

    /// <summary>An address with a scheme (http:, javascript:, data:) or a host (//host), rather than a path in the book.</summary>
    static bool HasScheme(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || Scheme().IsMatch(trimmed);
    }

    /// <summary>
    /// Cleans one chapter's body in place: what runs, embeds or fetches goes; attributes are kept only from a short
    /// list; ids are made unique to the chapter; pictures point at the book's files; links within the book go to
    /// the chapter and place they name, and web links keep their address.
    /// </summary>
    sealed class Cleaner(IEbook book, string path, int chapter, Dictionary<string, int> chapterOf, HashSet<IElement> markers, string resources)
    {
        readonly string _prefix = "bt-c" + chapter.ToString(CultureInfo.InvariantCulture) + "-";

        public void Clean(IElement element, int depth)
        {
            foreach (var child in element.ChildNodes.ToList())
            {
                switch (child)
                {
                    case IText:
                        break;
                    case IElement e when markers.Contains(e):
                        break;
                    case IElement e when Dropped.Contains(e.LocalName):
                        Hoist(e);
                        e.Remove();
                        break;
                    case IElement e when Unwrapped.Contains(e.LocalName) && e.NamespaceUri != SvgNamespace:
                        Clean(e, depth + 1);
                        foreach (var inner in e.ChildNodes.ToList()) element.InsertBefore(inner, e);
                        e.Remove();
                        break;
                    case IElement e when depth >= MaxDepth:
                        Hoist(e);
                        element.ReplaceChild(e.Owner!.CreateTextNode(e.TextContent), e);
                        break;
                    case IElement e:
                        Attributes(e);
                        Clean(e, depth + 1);
                        break;
                    default:
                        child.Parent?.RemoveChild(child);
                        break;
                }
            }
        }

        /// <summary>Keeps the page markers inside an element being taken out, just before it.</summary>
        void Hoist(IElement element)
        {
            foreach (var marker in element.QuerySelectorAll(MarkerName).Where(markers.Contains).ToList())
                element.Parent!.InsertBefore(marker, element);
        }

        void Attributes(IElement element)
        {
            var svg = element.NamespaceUri == SvgNamespace;
            var name = element.LocalName;
            foreach (var attribute in element.Attributes.ToList())
            {
                var key = attribute.Name;
                var value = attribute.Value;
                string? keep = null;
                if (key.Equals("id", StringComparison.OrdinalIgnoreCase)) keep = _prefix + value;
                else if (key.Equals("xml:lang", StringComparison.OrdinalIgnoreCase))
                {
                    if (!element.HasAttribute("lang") && LanguageTag().IsMatch(value)) element.SetAttribute("lang", value);
                }
                else if (key.EndsWith("href", StringComparison.OrdinalIgnoreCase)) keep = name is "a" or "area" ? Link(value, element) : svg && name is "image" or "use" ? Picture(value) : null;
                else if (key.Equals("src", StringComparison.OrdinalIgnoreCase)) keep = name == "img" ? Picture(value) : null;
                else if (key.StartsWith("on", StringComparison.OrdinalIgnoreCase) || key.Equals("style", StringComparison.OrdinalIgnoreCase)) keep = null;
                else if (svg) keep = IsAddress(key) ? null : value;
                else if (KeptAttributes.Contains(key) || key.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)) keep = value;
                else if (name is "img" && key is "width" or "height") keep = value;

                if (keep is null) element.RemoveAttribute(attribute.NamespaceUri, attribute.LocalName);
                else if (keep != value) element.SetAttribute(attribute.NamespaceUri, attribute.Name, keep);
            }
            if (name == "img" && element.GetAttribute("src") is null)
            {
                // A picture that isn't in the book shows its words, if it has any.
                var alt = element.GetAttribute("alt");
                if (alt is { Length: > 0 }) element.Parent!.ReplaceChild(element.Owner!.CreateTextNode(alt), element);
                else element.Remove();
            }
            else if (name == "img") element.SetAttribute("loading", "lazy");
        }

        /// <summary>
        /// A link's new address: a place in the book becomes the place on the reader's page; a web or mail link keeps
        /// its address; anything else is no link at all.
        /// </summary>
        string? Link(string href, IElement anchor)
        {
            var value = href.Trim();
            if (value.Length == 0) return null;
            if (HasScheme(value))
            {
                if (WebLink().IsMatch(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                {
                    anchor.SetAttribute("rel", "noreferrer");
                    return uri.AbsoluteUri;
                }
                return null;
            }
            var (target, fragment) = EpubBook.Resolve(path, value);
            if (!chapterOf.TryGetValue(target, out var to)) return null;
            var id = "bt-c" + to.ToString(CultureInfo.InvariantCulture) + (fragment is null ? "" : "-" + fragment);
            return "#" + Uri.EscapeDataString(id);
        }

        /// <summary>A picture's address on the reader's page, or null when the book doesn't have it.</summary>
        string? Picture(string src)
        {
            var value = src.Trim();
            if (value.Length == 0 || HasScheme(value)) return null;
            var (target, fragment) = EpubBook.Resolve(path, value);
            if (!book.Contains(target) || book.IsEncrypted(target)) return null;
            return ResourceUrl(target, resources) + (fragment is null ? "" : "#" + Uri.EscapeDataString(fragment));
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^(?:https?|mailto):", RegexOptions.IgnoreCase)]
    private static partial Regex WebLink();

    [GeneratedRegex(@"^[A-Za-z]{1,8}(?:-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    [GeneratedRegex("""<meta\s[^>]*name\s*=\s*["']viewport["'][^>]*content\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ViewportMeta();

    [GeneratedRegex(@"width\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WidthOf();

    [GeneratedRegex(@"height\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HeightOf();
}

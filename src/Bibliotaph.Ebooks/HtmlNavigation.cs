using AngleSharp.Dom;

namespace Bibliotaph.Ebooks;

/// <summary>An EPUB 3 navigation document: its contents (<c>toc</c>) and its page list (<c>page-list</c>).</summary>
static class HtmlNavigation
{
    public static (IReadOnlyList<EpubNavPoint> Contents, IReadOnlyList<EpubNavPoint> Pages) Read(string html, string navPath)
    {
        var document = HtmlText.Parse(html);
        var navs = document.QuerySelectorAll("nav").ToList();
        static bool Is(IElement nav, string type) => nav.GetAttribute("epub:type")?.Split(' ').Contains(type) == true
            || nav.GetAttribute("role") == "doc-" + type.Replace("page-list", "pagelist", StringComparison.Ordinal);
        var toc = navs.FirstOrDefault(n => Is(n, "toc")) ?? navs.FirstOrDefault(n => !Is(n, "page-list") && !Is(n, "landmarks"));
        var pages = navs.FirstOrDefault(n => Is(n, "page-list"));
        return (Points(toc, navPath), Points(pages, navPath));
    }

    static List<EpubNavPoint> Points(IElement? nav, string navPath)
    {
        var points = new List<EpubNavPoint>();
        if (nav?.Children.FirstOrDefault(c => c.LocalName == "ol") is not { } list) return points;

        void Walk(IElement ol, int depth)
        {
            foreach (var li in ol.Children.Where(c => c.LocalName == "li"))
            {
                if (points.Count >= EpubBook.MaxNavPoints) return;
                var link = li.Children.FirstOrDefault(c => c.LocalName is "a" or "span");
                if (link?.GetAttribute("href") is { Length: > 0 } href)
                {
                    var (path, fragment) = EpubBook.Resolve(navPath, href);
                    points.Add(new EpubNavPoint(HtmlText.Collapse(link.TextContent), path, fragment, depth));
                }
                if (depth < EpubBook.MaxNavDepth && li.Children.FirstOrDefault(c => c.LocalName == "ol") is { } nested)
                    Walk(nested, link?.GetAttribute("href") is { Length: > 0 } ? depth + 1 : depth);
            }
        }

        Walk(list, 0);
        return points;
    }
}

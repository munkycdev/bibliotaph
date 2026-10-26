using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Bibliotaph.Ebooks.Tests;

public sealed class EbookReaderDocumentTests
{
    static (string Html, EbookLayout Layout) Build(SyntheticEpub epub)
    {
        using var book = epub.Open();
        var layout = EbookPaging.Cut(book);
        return (EbookReaderDocument.Build(book, layout, "<style>body{}</style>", "<script>reader()</script>"), layout);
    }

    static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>The text shown between each page's marker and the next, without white space, page by page.</summary>
    static List<string> TextByPage(IDocument document, int pages)
    {
        var text = Enumerable.Range(0, pages).Select(_ => new System.Text.StringBuilder()).ToList();
        var page = -1;
        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IElement { LocalName: EbookReaderDocument.MarkerName } marker) page = int.Parse(marker.GetAttribute("data-page")!);
                else if (child is IText t && page >= 0) text[page].Append(string.Concat(t.Data.Where(c => !char.IsWhiteSpace(c))));
                else if (child is IElement { LocalName: not ("script" or "style") }) Walk(child);
            }
        }
        Walk(document.GetElementById("bt-book")!);
        return [.. text.Select(t => t.ToString())];
    }

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public void Every_page_has_its_marker_in_order_and_the_text_between_markers_is_the_page_text()
    {
        var paragraphs = string.Concat(Enumerable.Range(0, 9).Select(i => $"<p>{SyntheticEpub.Words(700, $"gull{i}")}</p>"));
        var (html, layout) = Build(new SyntheticEpub()
            .Chapter("one.xhtml", "<h1>The Harbour</h1>" + paragraphs + $"<p>{SyntheticEpub.Words(6000, "long")}</p>")
            .Chapter("two.xhtml", "<h1>The Reef</h1><p>A kraken <em>sleeps</em> beneath the lighthouse.</p>"));

        var document = Parse(html);
        var markers = document.QuerySelectorAll(EbookReaderDocument.MarkerName).Select(m => int.Parse(m.GetAttribute("data-page")!)).ToList();
        Assert.True(layout.Pages.Count > 4);
        Assert.Equal(Enumerable.Range(0, layout.Pages.Count), markers);
        Assert.Equal(layout.Pages.Select(p => Squeeze(p.Text)), TextByPage(document, layout.Pages.Count));
        // A page starting at a heading has its marker before the heading, not inside it.
        Assert.Equal("h1", document.GetElementById("bt-p" + layout.Pages[^1].Index)!.NextElementSibling!.LocalName);
    }

    [Fact]
    public void Printed_pages_have_their_markers_where_the_book_marks_them()
    {
        var (html, layout) = Build(new SyntheticEpub()
            .Chapter("cover.xhtml", "<h1>The Sunken Lantern</h1>")
            .Chapter("one.xhtml", """
                <span epub:type="pagebreak" id="p1" title="1"/>
                <p>First printed page.</p>
                <div role="doc-pagebreak" aria-label="2"></div>
                <p>Second printed page.</p>
                <span epub:type="pagebreak" title="3"></span><span epub:type="pagebreak" title="4"></span>
                <p>After a blank page.</p>
                """));
        Assert.True(layout.PrintPages);
        Assert.Equal(layout.Pages.Select(p => Squeeze(p.Text)), TextByPage(Parse(html), layout.Pages.Count));
    }

    [Fact]
    public void Nothing_that_runs_embeds_or_reaches_out_is_kept_but_its_words_are()
    {
        var (html, _) = Build(new SyntheticEpub().Chapter("one.xhtml", """
            <p onclick="steal()" style="color: white" class="x">Words <script>steal()</script>stay.</p>
            <iframe src="https://example.com/"></iframe><object data="x.swf">Fallback</object>
            <form action="https://example.com/"><button formaction="https://example.com/">Pressed</button><input value="secret"/></form>
            <a href="javascript:steal()">Not a link</a>
            <img src="https://example.com/track.gif" alt="Map of the coast"/>
            <svg xmlns="http://www.w3.org/2000/svg" onload="steal()"><script>steal()</script><foreignObject><p>x</p></foreignObject><circle r="4" fill="red"/></svg>
            <bt-page data-page="99"></bt-page>
            """));
        var document = Parse(html);
        var book = document.GetElementById("bt-book")!;
        Assert.Empty(book.QuerySelectorAll("script, iframe, object, form, button, input, foreignObject"));
        Assert.DoesNotContain(book.QuerySelectorAll("section *"), e => e.Attributes.Any(a => a.Name.StartsWith("on", StringComparison.Ordinal) || a.Name is "style" or "class" or "action"));
        Assert.DoesNotContain("example.com", book.InnerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", book.InnerHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Words stay.", book.TextContent, StringComparison.Ordinal);
        Assert.Contains("Pressed", book.TextContent, StringComparison.Ordinal);
        Assert.Contains("Map of the coast", book.TextContent, StringComparison.Ordinal);
        Assert.Single(book.QuerySelectorAll("circle"));
        // A marker written into the book is never taken for a page.
        Assert.Equal(["0"], book.QuerySelectorAll(EbookReaderDocument.MarkerName).Select(m => m.GetAttribute("data-page")));
        // The reader's own head and script are the only ones.
        Assert.Equal(["reader()"], document.QuerySelectorAll("script").Select(s => s.TextContent));
    }

    [Fact]
    public void Links_go_to_places_on_the_reader_page_and_pictures_to_the_book_files()
    {
        var (html, _) = Build(new SyntheticEpub()
            .File("img/the map.png", [1, 2, 3], "image/png")
            .Chapter("one.xhtml", """
                <p id="top">See <a href="two.xhtml#reef">the reef</a>, <a href="two.xhtml">chapter two</a>, <a href="#top">the top</a>
                and <a href="https://www.example.org/errata?x=1">the errata</a>.</p>
                <img src="img/the%20map.png" alt="Map"/><img src="img/missing.png"/>
                """)
            .Chapter("two.xhtml", """<h2 id="reef">The Reef</h2>"""));
        var book = Parse(html).GetElementById("bt-book")!;
        var links = book.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal(["#bt-c1-reef", "#bt-c1", "#bt-c0-top", "https://www.example.org/errata?x=1"], links);
        Assert.NotNull(book.QuerySelector("#bt-c1-reef"));
        Assert.NotNull(book.QuerySelector("#bt-c0-top"));
        var image = Assert.Single(book.QuerySelectorAll("img"));
        Assert.Equal("/res/OEBPS/img/the%20map.png", image.GetAttribute("src"));
        Assert.Equal("OEBPS/img/the map.png", EbookReaderDocument.ResourcePath(image.GetAttribute("src")!));
    }

    [Fact]
    public void A_browser_holding_several_books_asks_for_each_ones_files_under_its_own_address()
    {
        using var book = new SyntheticEpub()
            .File("img/map.png", [1, 2, 3], "image/png")
            .Chapter("one.xhtml", """<p>Map.</p><img src="img/map.png" alt="Map"/>""")
            .Open();
        var html = EbookReaderDocument.Build(book, EbookPaging.Cut(book), "", "", "/ebook-res/abc/res/");
        Assert.Equal("/ebook-res/abc/res/OEBPS/img/map.png", Parse(html).QuerySelector("#bt-book img")!.GetAttribute("src"));
    }

    [Fact]
    public void The_page_is_given_pictures_and_stylesheets_but_never_a_chapter_as_written()
    {
        using var flowing = new SyntheticEpub()
            .File("img/map.png", [1, 2, 3], "image/png")
            .File("style.css", "p{}"u8.ToArray(), "text/css")
            .Chapter("one.xhtml", "<p>One.</p>")
            .Open();
        var map = EbookReaderDocument.ReadFile(flowing, "OEBPS/img/map.png");
        Assert.Equal([1, 2, 3], map?.Bytes);
        Assert.Equal("image/png", map?.MediaType);
        Assert.Equal("text/css", EbookReaderDocument.ReadFile(flowing, "OEBPS/style.css")?.MediaType);
        Assert.Null(EbookReaderDocument.ReadFile(flowing, "OEBPS/one.xhtml"));
        Assert.Null(EbookReaderDocument.ReadFile(flowing, "OEBPS/missing.png"));

        using var fixedLayout = new SyntheticEpub { FixedLayout = true }.Chapter("p1.xhtml", "<p>Panel.</p>").Open();
        var page = EbookReaderDocument.ReadFile(fixedLayout, "OEBPS/p1.xhtml");
        Assert.Equal("text/html", page?.MediaType);
        Assert.Contains("Panel.", System.Text.Encoding.UTF8.GetString(page!.Value.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixed_layout_book_shows_each_page_file_in_a_frame_at_its_size()
    {
        var (html, layout) = Build(new SyntheticEpub { FixedLayout = true, Head = """<meta name="viewport" content="width=1200, height=1800"/>""" }
            .Chapter("p1.xhtml", "<p>Panel one.</p>")
            .Chapter("p2.xhtml", "<p>Panel two.</p>"));
        var document = Parse(html);
        Assert.Equal("bt-fixed", document.Body!.ClassName);
        var sections = document.QuerySelectorAll("section.bt-chapter").ToList();
        Assert.Equal(2, sections.Count);
        Assert.All(sections, s => Assert.Equal(("1200", "1800"), (s.GetAttribute("data-width"), s.GetAttribute("data-height"))));
        Assert.Equal(["/res/OEBPS/p1.xhtml", "/res/OEBPS/p2.xhtml"], document.QuerySelectorAll("iframe").Select(f => f.GetAttribute("src")));
        Assert.All(document.QuerySelectorAll("iframe"), f => Assert.Equal("allow-same-origin", f.GetAttribute("sandbox")));
        Assert.Equal(2, layout.Pages.Count);
    }

    [Fact]
    public void A_fixed_layout_page_keeps_its_styles_but_nothing_that_runs_or_links()
    {
        using var book = new SyntheticEpub { FixedLayout = true, Head = """<link rel="stylesheet" href="page.css"/><script src="x.js"></script>""" }
            .Chapter("p1.xhtml", """<div style="position:absolute" onclick="x()"><a href="p2.xhtml">Next</a><img src="https://example.com/t.gif"/></div>""")
            .Open();
        var page = Parse(EbookReaderDocument.FixedPage(book, "OEBPS/p1.xhtml"));
        Assert.Empty(page.QuerySelectorAll("script"));
        Assert.Equal("page.css", page.QuerySelector("link")!.GetAttribute("href"));
        Assert.Equal("position:absolute", page.QuerySelector("div")!.GetAttribute("style"));
        Assert.Null(page.QuerySelector("div")!.GetAttribute("onclick"));
        Assert.Null(page.QuerySelector("a")!.GetAttribute("href"));
        Assert.Null(page.QuerySelector("img")!.GetAttribute("src"));
    }

    [Fact]
    public void Elements_nested_without_end_are_kept_as_their_text()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", 5000)) + "Deep words." + string.Concat(Enumerable.Repeat("</div>", 5000));
        var (html, _) = Build(new SyntheticEpub().Chapter("one.xhtml", deep));
        Assert.Contains("Deep words.", Parse(html).GetElementById("bt-book")!.TextContent, StringComparison.Ordinal);
    }
}

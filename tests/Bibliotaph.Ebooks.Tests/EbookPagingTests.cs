namespace Bibliotaph.Ebooks.Tests;

public sealed class EbookPagingTests
{
    static EbookLayout Cut(SyntheticEpub epub)
    {
        using var book = epub.Open();
        return EbookPaging.Cut(book);
    }

    [Fact]
    public void A_flowing_book_starts_a_page_per_chapter_and_ends_pages_before_they_pass_the_target()
    {
        var paragraphs = string.Concat(Enumerable.Range(0, 12).Select(i => $"<p>{SyntheticEpub.Words(600, $"tide{i}")}</p>"));
        var layout = Cut(new SyntheticEpub()
            .Chapter("one.xhtml", "<h1>Arrival</h1>" + paragraphs)
            .Chapter("two.xhtml", "<h1>Departure</h1><p>Short.</p>"));

        Assert.False(layout.PrintPages);
        Assert.All(layout.Pages, p => Assert.True(p.Text.Length <= EbookPaging.Target, $"page {p.Index} has {p.Text.Length}"));
        Assert.All(layout.Pages, p => Assert.Null(p.Label));
        // A heading and 12 paragraphs of 600 characters, four to a page, and the second chapter on a page of its own.
        Assert.Equal(4, layout.Pages.Count);
        Assert.StartsWith("Arrival\nThe tide0", layout.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(new EbookLocator(0, 0), layout.Pages[0].Start);
        Assert.Equal(new EbookLocator(0, 5), layout.Pages[1].Start);
        Assert.Equal("Departure\nShort.", layout.Pages[3].Text);
        Assert.Equal(new EbookLocator(1, 0), layout.Pages[3].Start);
        Assert.Equal([0, 3], layout.Outline.Select(o => o.PageIndex));
        Assert.Equal(Enumerable.Range(0, 4), layout.Pages.Select(p => p.Index));
    }

    [Fact]
    public void A_heading_starts_a_new_page_once_the_page_is_half_full()
    {
        var layout = Cut(new SyntheticEpub().Chapter("one.xhtml",
            $"<p>{SyntheticEpub.Words(1400)}</p><h2>Second part</h2><p>{SyntheticEpub.Words(300, "reef")}</p><h3>Small</h3><p>End.</p>"));
        Assert.Equal(2, layout.Pages.Count);
        Assert.StartsWith("Second part", layout.Pages[1].Text, StringComparison.Ordinal);
        Assert.Contains("Small", layout.Pages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_paragraph_is_split_after_a_sentence_and_the_page_remembers_the_offset()
    {
        var text = SyntheticEpub.Words(6000);
        var layout = Cut(new SyntheticEpub().Chapter("one.xhtml", $"<p>{text}</p>"));
        Assert.Equal(3, layout.Pages.Count);
        Assert.EndsWith(".", layout.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, layout.Pages[1].Start.Block);
        Assert.True(layout.Pages[1].Start.Offset > EbookPaging.Target / 2);
        Assert.Equal(text.Replace(" ", "", StringComparison.Ordinal), string.Concat(layout.Pages.Select(p => p.Text)).Replace(" ", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Page_break_markers_make_printed_pages_with_their_numbers()
    {
        var layout = Cut(new SyntheticEpub()
            .Chapter("cover.xhtml", "<h1>The Sunken Lantern</h1>")
            .Chapter("one.xhtml", """
                <span epub:type="pagebreak" id="p1" title="1"/>
                <p>First printed page.</p>
                <p>Still the first.</p>
                <div role="doc-pagebreak" aria-label="2"></div>
                <p>Second printed page.</p>
                <span epub:type="pagebreak" title="3"></span><span epub:type="pagebreak" title="4"></span>
                <p>After a blank page.</p>
                """)
            .Chapter("two.xhtml", "<p>Chapter two continues page four.</p><span epub:type=\"pagebreak\">5</span><p>Five.</p>"));

        Assert.True(layout.PrintPages);
        Assert.Equal([null, "1", "2", "3", "4", "5"], layout.Pages.Select(p => p.Label));
        Assert.Equal("The Sunken Lantern", layout.Pages[0].Text);
        Assert.Equal("First printed page.\nStill the first.", layout.Pages[1].Text);
        Assert.Equal("", layout.Pages[3].Text);
        Assert.Equal("After a blank page.\nChapter two continues page four.", layout.Pages[4].Text);
        Assert.Equal("Five.", layout.Pages[5].Text);
    }

    [Fact]
    public void The_page_list_names_printed_pages_by_the_ids_it_points_at()
    {
        var layout = Cut(new SyntheticEpub
        {
            NavBody = """
                <nav epub:type="toc"><ol><li><a href="one.xhtml">Start</a><ol><li><a href="one.xhtml#sec">Section</a></li></ol></li></ol></nav>
                <nav epub:type="page-list"><ol><li><a href="one.xhtml#a">iv</a></li><li><a href="one.xhtml#b">v</a></li><li><a href="one.xhtml#c">vi</a></li></ol></nav>
                """,
        }.Chapter("one.xhtml", """<p id="a">Four.</p><p id="b">Five.</p><h2 id="sec">Section</h2><p id="c">Six.</p>"""));

        Assert.Equal(["iv", "v", "vi"], layout.Pages.Select(p => p.Label));
        Assert.Equal("Five.\nSection", layout.Pages[1].Text);
        Assert.Equal([("Start", 0, 0), ("Section", 1, 1)], layout.Outline.Select(o => (o.Title, o.PageIndex, o.Depth)));
    }

    [Fact]
    public void An_EPUB_2_NCX_gives_the_contents_and_page_list()
    {
        var layout = Cut(new SyntheticEpub
        {
            NavBody = "<p>No navigation here.</p>",
            Ncx = """
                <navMap>
                  <navPoint id="n1"><navLabel><text>Part One</text></navLabel><content src="one.xhtml"/>
                    <navPoint id="n2"><navLabel><text>The Reef</text></navLabel><content src="two.xhtml#reef"/></navPoint>
                  </navPoint>
                </navMap>
                <pageList>
                  <pageTarget type="normal" value="1"><navLabel><text>1</text></navLabel><content src="one.xhtml"/></pageTarget>
                  <pageTarget type="normal" value="2"><navLabel><text>2</text></navLabel><content src="two.xhtml"/></pageTarget>
                  <pageTarget type="normal" value="3"><navLabel><text>3</text></navLabel><content src="two.xhtml#p3"/></pageTarget>
                </pageList>
                """,
        }.Chapter("one.xhtml", "<p>One.</p>").Chapter("two.xhtml", """<p>Two.</p><p id="p3">Three.</p><h2 id="reef">The Reef</h2>"""));

        Assert.Equal(["1", "2", "3"], layout.Pages.Select(p => p.Label));
        Assert.Equal([("Part One", 0, 0), ("The Reef", 2, 1)], layout.Outline.Select(o => (o.Title, o.PageIndex, o.Depth)));
    }

    [Fact]
    public void A_fixed_layout_book_has_a_page_per_chapter_file()
    {
        var layout = Cut(new SyntheticEpub { FixedLayout = true }
            .Chapter("p1.xhtml", """<img src="a.jpg"/>""")
            .Chapter("p2.xhtml", "<p>Panel one.</p><p>Panel two.</p>")
            .Chapter("p3.xhtml", "<p>" + SyntheticEpub.Words(4000) + "</p>"));
        Assert.Equal(3, layout.Pages.Count);
        Assert.Equal("", layout.Pages[0].Text);
        Assert.Equal("Panel one.\nPanel two.", layout.Pages[1].Text);
        Assert.False(layout.PrintPages);
    }

    [Fact]
    public void A_chapter_with_only_a_picture_keeps_its_page()
    {
        var layout = Cut(new SyntheticEpub().Chapter("cover.xhtml", """<img src="c.jpg"/>""").Chapter("one.xhtml", "<p>Text.</p>"));
        Assert.Equal(["", "Text."], layout.Pages.Select(p => p.Text));
    }

    [Fact]
    public void Cutting_the_same_file_twice_gives_the_same_pages()
    {
        var epub = new SyntheticEpub().Chapter("one.xhtml", string.Concat(Enumerable.Range(0, 30).Select(i => $"<p>{SyntheticEpub.Words(377 + i * 13, $"w{i}")}</p>")));
        Assert.Equal(Cut(epub).Pages, Cut(epub).Pages);
    }
}

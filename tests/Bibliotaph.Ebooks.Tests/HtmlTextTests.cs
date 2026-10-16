namespace Bibliotaph.Ebooks.Tests;

public sealed class HtmlTextTests
{
    [Fact]
    public void Blocks_are_paragraphs_headings_items_and_rows_with_scripts_and_styles_left_out()
    {
        var chapter = HtmlText.Read("""
            <html><head><title>Not text</title><style>p { color: red }</style></head>
            <body>
              <h1>The   Reef</h1>
              <script>document.write("never")</script>
              <p>Waves<br/>and   <em>foam</em>.</p>
              <ul><li>Rope</li><li>Lamp</li></ul>
              <table><tr><th>d6</th><th>Find</th></tr><tr><td>1</td><td>A shell</td></tr></table>
              <div>Loose <span>text</span> <div>nested</div> after</div>
            </body></html>
            """);
        Assert.Equal(["The Reef", "Waves\nand foam.", "Rope", "Lamp", "d6 Find", "1 A shell", "Loose text", "nested", "after"],
            chapter.Blocks.Select(b => b.Text));
        Assert.True(chapter.Blocks[0].Heading);
        Assert.False(chapter.Blocks[1].Heading);
    }

    [Fact]
    public void A_self_closed_anchor_does_not_swallow_the_text_after_it_and_ids_name_their_blocks()
    {
        var chapter = HtmlText.Read("""<p>First.</p><a id="mark"/><p>Second has <span id="inner">an id</span>.</p><h2 id="h">Head</h2>""");
        Assert.Equal(["First.", "Second has an id.", "Head"], chapter.Blocks.Select(b => b.Text));
        Assert.Equal(1, chapter.Ids["mark"]);
        Assert.Equal(1, chapter.Ids["inner"]);
        Assert.Equal(2, chapter.Ids["h"]);
    }

    [Fact]
    public void Markup_that_isnt_well_formed_XML_still_reads()
    {
        var chapter = HtmlText.Read("<p>Unclosed &nbsp;entity<p>Second<br>line &amp; more");
        Assert.Equal(["Unclosed entity", "Second\nline & more"], chapter.Blocks.Select(b => b.Text));
    }

    [Fact]
    public void Preformatted_text_keeps_its_lines()
    {
        var chapter = HtmlText.Read("<pre>STR 10\nDEX  12</pre>");
        Assert.Equal("STR 10\nDEX 12", chapter.Blocks.Single().Text);
    }

    [Fact]
    public void A_cover_page_names_its_image_as_an_img_or_an_SVG_image()
    {
        Assert.Equal("../img/c.jpg", HtmlText.FirstImage("""<div><img src="../img/c.jpg"/></div>"""));
        Assert.Equal("cover.jpeg", HtmlText.FirstImage("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href="cover.jpeg"/></svg>
            """));
        Assert.Null(HtmlText.FirstImage("<p>No picture.</p>"));
    }

    [Fact]
    public void Very_deep_nesting_is_read_without_overflowing()
    {
        var html = string.Concat(Enumerable.Repeat("<span>", 50_000)) + "deep" + string.Concat(Enumerable.Repeat("</span>", 50_000));
        // On a thread with a small stack, as Windows gives, so a recursion that a large default stack hides still fails.
        string? text = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                text = HtmlText.Read("<p>" + html + "</p>").Blocks.Single().Text;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal("deep", text);
    }
}

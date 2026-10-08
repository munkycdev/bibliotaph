using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

public class PageTextLayerTests
{
    /// <summary>"red dragon" on one line and "lair" below it, 10 points a character, as PDFium reports a page.</summary>
    static PageTextLayer Page()
    {
        const string Text = "red dragon\r\nlair";
        var boxes = new List<PageRect>();
        var x = 0;
        var line = 0;
        foreach (var c in Text)
        {
            if (c is '\r' or '\n')
            {
                boxes.Add(default);
                if (c == '\n') (x, line) = (0, line + 1);
                continue;
            }
            var top = 700 - (line * 20);
            boxes.Add(c == ' ' ? default : new PageRect(x, top, x + 10, top - 12));
            x += 10;
        }
        return new PageTextLayer(Text, boxes);
    }

    [Fact]
    public void A_point_finds_its_character_or_the_nearest_on_its_line()
    {
        var page = Page();

        Assert.Equal(4, page.HitTest(45, 695));   // inside the "d" of dragon
        Assert.Equal(9, page.HitTest(500, 695));  // right of the line: its last character
        Assert.Equal(12, page.HitTest(-20, 675)); // left of "lair"
    }

    [Fact]
    public void Dragging_either_way_selects_the_same_text()
    {
        var page = Page();

        Assert.Equal("dragon", page.TextOf(PageTextLayer.Between(9, 4)));
        Assert.Equal("dragon", page.TextOf(PageTextLayer.Between(4, 9)));
    }

    [Fact]
    public void Double_and_triple_click_select_a_word_and_a_line()
    {
        var page = Page();

        Assert.Equal("dragon", page.TextOf(page.WordAt(6)));
        Assert.Equal("red dragon", page.TextOf(page.LineAt(1)));
        Assert.Equal("lair", page.TextOf(page.LineAt(13)));
    }

    [Fact]
    public void Copied_text_keeps_windows_line_breaks()
    {
        Assert.Equal("dragon\r\nlair", Page().TextOf(new TextRange(4, 12)));
    }

    [Fact]
    public void A_selection_is_drawn_as_one_rectangle_per_line()
    {
        var rects = Page().RectsOf(new TextRange(4, 12));

        Assert.Equal([new PageRect(40, 700, 100, 688), new PageRect(0, 680, 40, 668)], rects);
    }

    [Fact]
    public void Ocr_words_share_their_box_and_break_lines_where_the_page_does()
    {
        var page = PageTextLayer.FromWords([
            ("Secret", new PageRect(0, 700, 60, 688)),
            ("door", new PageRect(70, 700, 110, 688)),
            ("below", new PageRect(0, 680, 50, 668)),
        ]);

        Assert.Equal("Secret door\nbelow", page.Text);
        Assert.Equal(new PageRect(10, 700, 20, 688), page.Boxes[1]);
        Assert.Equal("door", page.TextOf(page.WordAt(page.HitTest(90, 695)!.Value)));
        Assert.Equal("Secret door\r\nbelow", page.TextOf(page.All));
    }

    [Fact]
    public void A_page_without_text_has_nothing_to_hit() => Assert.Null(PageTextLayer.Empty.HitTest(10, 10));
}

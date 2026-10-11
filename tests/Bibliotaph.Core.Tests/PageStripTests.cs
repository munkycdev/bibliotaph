using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

public class PageStripTests
{
    [Fact]
    public void Counts_each_note_on_every_page_it_covers()
    {
        var counts = PageStrip.NoteCounts(6, [(1, 1), (1, 3), (5, 5)]);

        Assert.Equal([0, 2, 1, 1, 0, 1], counts);
    }

    [Fact]
    public void A_note_past_the_end_of_the_book_counts_only_on_its_pages_here()
    {
        // A note made in a longer copy, or with its pages typed backwards.
        var counts = PageStrip.NoteCounts(4, [(2, 9), (8, 9), (-3, 0), (3, 1)]);

        Assert.Equal([1, 1, 2, 2], counts);
    }

    [Fact]
    public void A_book_with_no_pages_has_no_counts() => Assert.Empty(PageStrip.NoteCounts(0, [(0, 2)]));

    [Theory]
    [InlineData(4, 6, 10, 4, 6)]
    [InlineData(6, 4, 10, 4, 6)]   // backwards
    [InlineData(8, 14, 10, 8, 9)]  // runs past the end
    [InlineData(-2, 1, 10, 0, 1)]
    public void An_items_pages_are_held_to_the_book(int first, int last, int pageCount, int expectedFirst, int expectedLast) =>
        Assert.Equal((expectedFirst, expectedLast), PageStrip.ItemPages(first, last, pageCount));

    [Fact]
    public void A_whole_book_or_pages_outside_it_mark_nothing()
    {
        Assert.Null(PageStrip.ItemPages(null, null, 10));
        Assert.Null(PageStrip.ItemPages(12, 14, 10));
        Assert.Null(PageStrip.ItemPages(0, 0, 0));
    }

    [Fact]
    public void Draws_the_thumbnails_in_view_then_a_few_either_side_nearest_first()
    {
        Assert.Equal([10, 11, 12, 13, 9, 14, 8], PageStrip.PagesToDraw(10, 12, 600, around: 2));
    }

    [Fact]
    public void Drawing_stops_at_the_ends_of_the_book()
    {
        Assert.Equal([0, 1, 2, 3], PageStrip.PagesToDraw(0, 1, 4, around: 2));
        Assert.Equal([598, 599, 597, 596], PageStrip.PagesToDraw(598, 599, 600, around: 2));
        Assert.Empty(PageStrip.PagesToDraw(0, 0, 0));
    }

    [Fact]
    public void A_long_book_draws_only_what_is_near_the_view()
    {
        var pages = PageStrip.PagesToDraw(300, 309, 600);

        Assert.Equal(10 + (2 * PageStrip.DrawAround), pages.Count);
        Assert.All(pages, p => Assert.InRange(p, 300 - PageStrip.DrawAround, 309 + PageStrip.DrawAround));
    }

    [Fact]
    public void Describes_a_thumbnail_for_screen_readers()
    {
        Assert.Equal("Page 12", PageStrip.Describe("12", 0, inItem: false, current: false));
        Assert.Equal("Page xii, 1 note, the page in view", PageStrip.Describe("xii", 1, inItem: false, current: true));
        Assert.Equal("Page 4, 3 notes, in this item", PageStrip.Describe("4", 3, inItem: true, current: false));
    }

    static readonly ReadAheadItem InBookOne = new(1, CanOpen: true, IsImage: false);
    static readonly ReadAheadItem InBookTwo = new(2, CanOpen: true, IsImage: false);

    [Fact]
    public void Reads_ahead_when_the_next_item_is_in_another_book()
    {
        Assert.Equal(1, ReadAhead.Next([InBookOne, InBookTwo], 0, openDocument: 1));
    }

    [Fact]
    public void Leaves_the_next_item_in_the_same_book_to_the_books_own_prefetch()
    {
        Assert.Null(ReadAhead.Next([InBookOne, InBookOne], 0, openDocument: 1));
    }

    [Fact]
    public void Reads_ahead_when_nothing_is_open_yet()
    {
        // The item in hand couldn't be reached, so the reader shows why and nothing is open.
        Assert.Equal(1, ReadAhead.Next([InBookOne, InBookTwo], 0, openDocument: null));
    }

    [Fact]
    public void Does_not_read_ahead_past_the_end_or_to_items_that_cannot_open()
    {
        Assert.Null(ReadAhead.Next([InBookOne, InBookTwo], 1, openDocument: 2));
        Assert.Null(ReadAhead.Next([InBookOne, InBookTwo with { CanOpen = false }], 0, openDocument: 1));
        Assert.Null(ReadAhead.Next([InBookOne, InBookTwo with { DocumentId = null }], 0, openDocument: 1));
        Assert.Null(ReadAhead.Next([InBookOne, InBookTwo with { IsImage = true }], 0, openDocument: 1));
        Assert.Null(ReadAhead.Next([InBookOne], -1, openDocument: null));
        Assert.Null(ReadAhead.Next([], 0, openDocument: null));
    }
}

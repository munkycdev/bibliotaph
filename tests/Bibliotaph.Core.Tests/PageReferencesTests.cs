using System.Globalization;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

/// <summary>Made-up index and contents pages, and pages that only look a little like them.</summary>
public class PageReferencesTests
{
    /// <summary>A 120-page book: a cover and three front pages (i-iii), then printed page 1 on PDF page 4.</summary>
    static readonly PrintedPages Book = PrintedPages.FromLabels(
        [.. new string?[] { "Cover", "i", "ii", "iii" }.Concat(Enumerable.Range(1, 116).Select(n => n.ToString(CultureInfo.InvariantCulture)))], 120);

    const int Front = 3;

    /// <summary>What each reference covers in the text, its number and its PDF page.</summary>
    static List<(string Text, int Printed, int PageIndex)> Find(string text, bool listed = false, PrintedPages? printed = null) =>
        [.. PageReferences.Find(text, printed ?? Book, listed).Select(r => (text.Substring(r.Range.Start, r.Range.Length), r.Printed, r.PageIndex))];

    static List<int> Numbers(string text, bool listed = false) => [.. Find(text, listed).Select(r => r.Printed)];

    const string IndexPage = """
        Index
        Amber golems, 12
        Bandit camps, 34–36
        Bells of the drowned chapel, 7, 19, 102
        Cart roads, 55
        Copper mines, 8-9; 60
        Dire badgers, 41
        Ember drakes 77
        Fen lights, 23,
          88
        Glass towers, 90—91
        112
        """;

    [Fact]
    public void An_index_pages_numbers_go_to_their_printed_pages()
    {
        var found = Find(IndexPage);

        Assert.Equal([12, 34, 7, 19, 102, 55, 8, 60, 41, 77, 23, 88, 90], found.Select(r => r.Printed));
        Assert.All(found, r => Assert.Equal(r.Printed + Front, r.PageIndex));
    }

    [Fact]
    public void A_range_is_one_link_to_its_first_page()
    {
        var found = Find(IndexPage);

        Assert.Contains(("34–36", 34, 37), found);
        Assert.Contains(("8-9", 8, 11), found);
        Assert.Contains(("90—91", 90, 93), found);
        Assert.Contains(("102", 102, 105), found);
    }

    [Fact]
    public void Numbers_alone_on_a_line_carry_on_only_after_a_comma()
    {
        var found = Numbers(IndexPage);

        Assert.Contains(88, found);          // after "Fen lights, 23,"
        Assert.DoesNotContain(112, found);   // the page's own number at its foot
    }

    [Fact]
    public void Windows_line_breaks_and_indents_keep_the_positions_right()
    {
        var text = IndexPage.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("Cart roads", "   Cart roads", StringComparison.Ordinal);

        var found = Find(text);

        Assert.Contains(("55", 55, 58), found);
        Assert.Equal(13, found.Count);
    }

    [Fact]
    public void A_contents_page_with_dot_leaders_counts_without_a_heading()
    {
        var contents = """
            Introduction .......... 3
            Chapter 1: The Valley ...... 9
            Chapter 2 . . . . . . . . 21
            The Old Road…………… 40
            Appendix ........ 101
            Credits and thanks
            """;

        Assert.Equal([3, 9, 21, 40, 101], Numbers(contents));
        Assert.Contains(("21", 21, 24), Find(contents));
    }

    const string ShortIndex = """
        Lanterns, 14
        Moon wells, 52
        Night markets, 67
        The listing above covers the second edition only.
        Earlier printings put these on other pages.
        Ask your game master which printing you have.
        """;

    [Fact]
    public void A_short_list_counts_only_under_an_index_bookmark_or_heading()
    {
        Assert.Empty(Find(ShortIndex));
        Assert.Equal([14, 52, 67], Numbers(ShortIndex, listed: true));
        Assert.Equal([14, 52, 67], Numbers("Index\n" + ShortIndex));
        Assert.Equal([14, 52, 67], Numbers("Monster Index (continued)\n" + ShortIndex));
        Assert.Equal([14, 52, 67], Numbers("Table of Contents\n" + ShortIndex));
    }

    [Fact]
    public void Prose_with_a_few_numbers_at_line_ends_is_left_alone()
    {
        var prose = """
            The caravan left the city with 12
            mules and a cart of salt. It reached the pass
            after three days, where the guards counted 40
            bandits on the ridge. The captain ordered the
            wagons into a ring and waited for the dawn.
            Nobody slept, and by morning only 7
            of the bandits were still watching the road.
            The rest had gone north toward the mines.
            """;

        Assert.Empty(Find(prose));
        Assert.Empty(Find(prose, listed: true));
    }

    [Fact]
    public void A_sentence_ending_in_a_number_is_not_an_entry()
    {
        var page = """
            Index
            Owls, 15
            Ravens, 16
            The goblins fled into the woods. They counted 20
            Wolves, 30
            """;

        Assert.Equal([15, 16, 30], Numbers(page));
    }

    [Fact]
    public void Dice_and_stat_lines_never_count()
    {
        var page = """
            Index
            Ash wraiths, 18
            Bog hounds, 25
            Cinder imps, 31
            Dusk moths, 39
            Frost newts, 46
            Grave larks, 53
            Damage 2d6
            Attack +5 to hit, 1d8 + 3
            Armor Class 15
            Hit Points 27
            STR 18
            Speed 30
            """;

        Assert.Equal([18, 25, 31, 39, 46, 53], Numbers(page, listed: true));
    }

    [Fact]
    public void A_dice_table_counting_up_from_one_is_not_a_list_of_pages()
    {
        var table = """
            Wandering monsters
            Goblin scouts 1–2
            Lost pilgrim 3
            Hungry wolves 4–5
            Storm crows 6
            """;

        Assert.Empty(Find(table, listed: true));
        Assert.Empty(Find("Index\n" + table));
    }

    [Fact]
    public void Numbers_with_a_leading_zero_are_table_rows()
    {
        var table = """
            Index
            Goblin scouts 01–05
            Lost pilgrims 06–10
            Hungry wolves 11–20
            Storm crows 21–40
            """;

        Assert.Empty(Find(table));
    }

    [Fact]
    public void Years_and_numbers_past_the_books_pages_are_left_alone()
    {
        var page = """
            Index
            Founding of the city, 1204
            Old maps, 12, 450
            River gods, 44
            Salt roads, 61
            © 2024 Invented Press
            """;

        Assert.Equal([12, 44, 61], Numbers(page));
    }

    [Fact]
    public void A_page_full_of_entries_counts_without_any_hint()
    {
        var page = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"Invented entry {(char)('A' + i)}, {(i * 9) + 5}"));

        Assert.Equal(10, Find(page).Count);
        // Fewer than eight entries is not plain enough without a hint.
        Assert.Empty(Find(string.Join('\n', page.Split('\n').Take(7))));
    }

    [Fact]
    public void A_tiny_books_index_counts_under_its_heading()
    {
        // As PDFium reads the smoke test's index page: a title page labelled i, then printed pages 1 to 3.
        var printed = PrintedPages.FromLabels(["i", "1", "2", "3"], 4);
        var page = "Index\r\nBridges, 1\r\nDragons, 2\r\nMill wheel, 1-2\r\nRiver crossings, 1\r\nWyrms, 2\r\n";

        var found = Find(page, printed: printed);

        Assert.Equal([("1", 1, 1), ("2", 2, 2), ("1-2", 1, 1), ("1", 1, 1), ("2", 2, 2)], found);
    }

    [Fact]
    public void Nothing_is_guessed_when_the_book_doesnt_say_how_it_numbers_its_pages()
    {
        Assert.Empty(Find(IndexPage, printed: PrintedPages.None));
        Assert.Empty(Find(""));
    }

    [Fact]
    public void Folios_map_the_numbers_of_a_book_without_labels()
    {
        // Printed page n on PDF page n + 2, read from the number at the foot of each page.
        var pages = Enumerable.Range(0, 120).Select(page => (page, page < 2 ? "Cover" : $"Some invented text.\n{page - 1}")).ToList();
        var printed = PrintedPages.FromFolios(pages, 120);

        var found = Find(IndexPage, printed: printed);

        Assert.Contains(("34–36", 34, 35), found);
    }

    [Fact]
    public void Index_and_contents_bookmarks_mark_their_pages()
    {
        (string, int, int)[] outline =
        [
            ("Credits", 0, 0),
            ("Table of Contents", 2, 0),
            ("Chapter 1", 4, 0),
            ("Indexing the vault", 30, 1),
            ("Index", 110, 0),
            ("A", 110, 1),
            ("B", 111, 1),
            ("Advertisements", 113, 0),
        ];

        Assert.Equal([2, 3, 110, 111, 112], PageReferences.ListedPages(outline, 120).Order());
    }

    [Fact]
    public void A_last_index_bookmark_runs_to_the_end_but_not_too_far()
    {
        Assert.Equal([116, 117, 118, 119], PageReferences.ListedPages([("Chapter 9", 80, 0), ("Appendix C: Index", 116, 0)], 120).Order());
        Assert.Equal(40, PageReferences.ListedPages([("Monster Index", 10, 0)], 120).Count);
        Assert.Empty(PageReferences.ListedPages([("Index", 130, 0)], 120));
    }
}

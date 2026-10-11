namespace Bibliotaph.Core.Tests;

/// <summary>Slice 5b: the rule that proposes a compilation's adventures from its bookmarks and their blurbs.</summary>
public class PartRulesTests
{
    /// <summary>A made-up compilation: credits, three adventures with a page of blurb each, and an appendix.</summary>
    static readonly string[] Pages =
    [
        "Credits\nWritten by Ana Ruiz",
        "Introduction\nHow to use this book",
        "The Salt Mine\nAn adventure for four characters of 1st level",
        "The mine's shafts run deep under the marsh.",
        "Lantern Night\nA short adventure for characters of 3rd to 5th level",
        "The festival begins at dusk.",
        "Rooftop chase continues.",
        "The Glass Tower\nFor levels 7-9",
        "The tower hums at midnight.",
        "Appendix: New Monsters\nSalt wraiths and glass golems.",
    ];

    [Fact]
    public void Top_level_bookmarks_with_a_blurb_are_proposed_and_front_and_back_matter_is_left_out()
    {
        (string, int, int)[] outline =
            [("Credits", 0, 0), ("Introduction", 1, 0), ("The Salt Mine", 2, 0), ("Shafts", 3, 1), ("Lantern Night", 4, 0), ("The Glass Tower", 7, 0), ("Appendix: New Monsters", 9, 0)];

        var proposed = PartRules.Propose(outline, Pages);

        Assert.Equal(
            [("The Salt Mine", new PartPages(2, 3), "1"), ("Lantern Night", new PartPages(4, 6), "3-5"), ("The Glass Tower", new PartPages(7, 8), "7-9")],
            proposed.Select(p => (p.Title, p.Pages, p.Levels)));
        Assert.Equal("for four characters of 1st level", proposed[0].Quote);
    }

    [Fact]
    public void Under_a_single_top_level_bookmark_its_children_are_the_candidates()
    {
        (string, int, int)[] outline =
            [("A Made-up Compilation", 0, 0), ("Credits", 0, 1), ("The Salt Mine", 2, 1), ("Lantern Night", 4, 1), ("The Glass Tower", 7, 1), ("Appendix", 9, 1)];
        Assert.Equal(["The Salt Mine", "Lantern Night", "The Glass Tower"], PartRules.Propose(outline, Pages).Select(p => p.Title));
    }

    [Fact]
    public void A_blurb_on_the_page_after_a_bookmark_counts_but_a_chapter_without_one_does_not()
    {
        string[] pages = ["Title page", "Part one\nArt", "An adventure for 4-6 players of 2nd level", "Part two", "levels 4-6", "more", "Part three", "no blurb here"];
        (string, int, int)[] outline = [("Part one", 1, 0), ("Part two", 3, 0), ("Part three", 6, 0)];

        var proposed = PartRules.Propose(outline, pages);

        Assert.Equal([("Part one", new PartPages(1, 2), "2"), ("Part two", new PartPages(3, 5), "4-6")], proposed.Select(p => (p.Title, p.Pages, p.Levels)));
    }

    [Fact]
    public void One_adventure_is_not_a_compilation()
    {
        (string, int, int)[] outline = [("Credits", 0, 0), ("The Salt Mine", 2, 0), ("Appendix", 9, 0)];
        Assert.Empty(PartRules.Propose(outline, Pages));
        Assert.Empty(PartRules.Propose([], []));
    }

    [Fact]
    public void Without_bookmarks_the_blurb_alone_decides_and_the_first_line_is_the_title()
    {
        var proposed = PartRules.Propose([], Pages);

        Assert.Equal(
            [("The Salt Mine", new PartPages(2, 3)), ("Lantern Night", new PartPages(4, 6)), ("The Glass Tower", new PartPages(7, 9))],
            proposed.Select(p => (p.Title, p.Pages)));
    }

    [Theory]
    [InlineData("Credits")]
    [InlineData("INTRODUCTION")]
    [InlineData("Appendix B: Magic Items")]
    [InlineData("Index")]
    [InlineData("OGL")]
    [InlineData("Open Game License Version 1.0a")]
    [InlineData("Table of Contents")]
    public void Front_and_back_matter_is_never_an_adventure(string title) => Assert.True(PartRules.IsMatter(title));

    [Theory]
    [InlineData("The Salt Mine")]
    [InlineData("Indexing the Archive")]
    [InlineData("Legacy of the Glass Tower")]
    public void An_adventure_title_is_not_matter(string title) => Assert.False(PartRules.IsMatter(title));
}

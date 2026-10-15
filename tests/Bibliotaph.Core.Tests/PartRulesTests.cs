namespace Bibliotaph.Core.Tests;

/// <summary>The rule that proposes a compilation's adventures from its bookmarks and their blurbs.</summary>
public class PartRulesTests
{
    /// <summary>
    /// A made-up compilation: credits, an introduction that says it is a collection, three adventures with a page of
    /// blurb each, and an appendix.
    /// </summary>
    static readonly string[] Pages =
    [
        "Credits\nWritten by Ana Ruiz",
        "Introduction\nThis book is a collection of three short adventures. Each of them can be run on its own,\nin an evening.",
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

        var found = PartRules.Propose(outline, Pages)!;

        var proposed = found.Parts;
        Assert.Equal(
            [("The Salt Mine", new PartPages(2, 3), "1"), ("Lantern Night", new PartPages(4, 6), "3-5"), ("The Glass Tower", new PartPages(7, 8), "7-9")],
            proposed.Select(p => (p.Title, p.Pages, p.Levels)));
        Assert.Equal("for four characters of 1st level", proposed[0].Quote);
        Assert.Equal(new CollectionEvidence(1, "Introduction This book is a collection of three short adventures."), found.Evidence);
    }

    [Fact]
    public void Under_a_single_top_level_bookmark_its_children_are_the_candidates()
    {
        (string, int, int)[] outline =
            [("A Made-up Compilation", 0, 0), ("Credits", 0, 1), ("The Salt Mine", 2, 1), ("Lantern Night", 4, 1), ("The Glass Tower", 7, 1), ("Appendix", 9, 1)];
        Assert.Equal(["The Salt Mine", "Lantern Night", "The Glass Tower"], PartRules.Propose(outline, Pages)!.Parts.Select(p => p.Title));
    }

    [Fact]
    public void A_blurb_on_the_page_after_a_bookmark_counts_but_a_chapter_without_one_does_not()
    {
        string[] pages = ["Title page\nTwo stand-alone adventures for the Marsh Guild", "Part one\nArt", "An adventure for 4-6 players of 2nd level", "Part two", "levels 4-6", "more", "Part three", "no blurb here"];
        (string, int, int)[] outline = [("Part one", 1, 0), ("Part two", 3, 0), ("Part three", 6, 0)];

        var proposed = PartRules.Propose(outline, pages)!.Parts;

        Assert.Equal([("Part one", new PartPages(1, 2), "2"), ("Part two", new PartPages(3, 5), "4-6")], proposed.Select(p => (p.Title, p.Pages, p.Levels)));
    }

    [Fact]
    public void The_candidates_a_model_is_asked_about_are_the_bookmarks_without_front_and_back_matter_whatever_their_pages_say()
    {
        (string, int, int)[] outline =
            [("Credits", 0, 0), ("The Salt Mine", 2, 0), ("Shafts", 3, 1), ("A Chapter", 4, 0), ("Appendix: New Monsters", 9, 0), ("Off the end", 12, 0)];

        Assert.Equal([("The Salt Mine", new PartPages(2, 3)), ("A Chapter", new PartPages(4, 8))],
            PartRules.Candidates(outline, Pages.Length).Select(c => (c.Title, c.Pages)));
        Assert.Empty(PartRules.Candidates([], 10));
    }

    [Fact]
    public void A_party_of_some_level_is_a_blurb_but_a_contents_page_listing_levels_is_not()
    {
        string[] pages =
        [
            "A Dozen Adventures for the Marsh",
            "Contents The Salt Mine (Levels 1-2) 2 Lantern Night (Levels 3-4) 4 The Glass Tower (Levels 5-6) 6 Fog Market (Levels 7-8) 8",
            "FOUR 1ST- TO 2ND-LEVEL PCS The Salt Mine", "more",
            "FIVE 3RD-LEVEL CHARACTERS Lantern Night", "more",
        ];
        (string, int, int)[] outline = [("A Dozen Adventures for the Marsh", 0, 0), ("The Salt Mine", 2, 0), ("Lantern Night", 4, 0)];

        var found = PartRules.Propose(outline, pages)!;

        Assert.Equal([("The Salt Mine", new PartPages(2, 3), "1-2"), ("Lantern Night", new PartPages(4, 5), "3")], found.Parts.Select(p => (p.Title, p.Pages, p.Levels)));
        Assert.Equal(new CollectionEvidence(0, "A Dozen Adventures for the Marsh"), found.Evidence);
    }

    [Fact]
    public void One_adventure_is_not_a_compilation()
    {
        (string, int, int)[] outline = [("Credits", 0, 0), ("The Salt Mine", 2, 0), ("Appendix", 9, 0)];
        Assert.Null(PartRules.Propose(outline, Pages));
        Assert.Null(PartRules.Propose([], []));
    }

    [Fact]
    public void Without_bookmarks_the_blurb_alone_decides_and_the_first_line_is_the_title()
    {
        var proposed = PartRules.Propose([], Pages)!.Parts;

        Assert.Equal(
            [("The Salt Mine", new PartPages(2, 3)), ("Lantern Night", new PartPages(4, 6)), ("The Glass Tower", new PartPages(7, 9))],
            proposed.Select(p => (p.Title, p.Pages)));
    }

    /// <summary>Chapters of one campaign, each with a blurb, as campaign books print them.</summary>
    static readonly (string, int, int)[] CampaignOutline =
        [("Credits", 0, 0), ("Introduction", 1, 0), ("The Salt Mine", 2, 0), ("Lantern Night", 4, 0), ("The Glass Tower", 7, 0), ("Appendix: New Monsters", 9, 0)];

    [Theory]
    [InlineData("Introduction\nHow to use this book")]
    [InlineData("Introduction\nThese three adventures form one campaign, played in order from the mine to the tower.")]
    [InlineData("Introduction\nThe first two adventures take place in the marsh.")]
    public void Blurbs_alone_dont_make_a_compilation_the_book_must_say_its_adventures_are_separate(string introduction)
    {
        string[] pages = [.. Pages];
        pages[1] = introduction;
        Assert.Null(PartRules.Propose(CampaignOutline, pages));
        Assert.Null(PartRules.Propose([], pages));
    }

    [Fact]
    public void The_back_cover_can_say_so_but_a_page_after_the_first_adventure_starts_cannot()
    {
        string[] pages = [.. Pages];
        pages[1] = "Introduction\nHow to use this book";
        pages[5] = "This book holds three stand-alone adventures.";
        Assert.Null(PartRules.Propose(CampaignOutline, pages));

        pages[9] = "Appendix: New Monsters\nSalt wraiths and glass golems. Three stand-alone adventures for the marsh.";
        Assert.Equal(new CollectionEvidence(9, "Three stand-alone adventures for the marsh."), PartRules.Propose(CampaignOutline, pages)!.Evidence);
    }

    [Theory]
    [InlineData("This anthology gathers six one-shots for horror nights.", "This anthology gathers six one-shots for horror nights.")]
    [InlineData("Inside are five self-contained scenarios for new players.", "Inside are five self-contained scenarios for new players.")]
    [InlineData("A compilation of nine short adventures!", "A compilation of nine short adventures!")]
    [InlineData("Run them as a campaign, or as one-shots.", "Run them as a campaign, or as one-shots.")]
    [InlineData("Welcome. These adventures can each be played on their own or in any order.", "These adventures can each be played on their own or in any order.")]
    [InlineData("The book holds seven adventures you can drop into any game.", "The book holds seven adventures you can drop into any game.")]
    [InlineData("An adventure collection for the marsh.", "An adventure collection for the marsh.")]
    [InlineData("A DOZEN ADVENTURES FOR ANY TABLE", "A DOZEN ADVENTURES FOR ANY TABLE")]
    [InlineData("12 MINI-ADVENTURES FOR LEVELS 1 TO 15", "12 MINI-ADVENTURES FOR LEVELS 1 TO 15")]
    public void A_book_saying_it_holds_separate_adventures_is_found(string text, string sentence) => Assert.Equal(sentence, PartRules.CollectionSentence(text));

    [Theory]
    [InlineData("An adventure for four characters of 3rd level.")]
    [InlineData("This adventure can be run as a one-shot.")]
    [InlineData("The book is divided into seven chapters.")]
    [InlineData("Four linked adventures take the heroes from the mine to the tower.")]
    [InlineData("These four adventures form a single campaign.")]
    [InlineData("The next two adventures take place underground.")]
    [InlineData("A collection of maps and handouts.")]
    [InlineData("Then comes a summary of the three adventures.")]
    [InlineData("Contents Introduction 4 Journeys 29 Mini adventures 109 Random events 112 Index 120")]
    public void A_single_adventure_or_a_campaign_is_not_a_collection(string text) => Assert.Null(PartRules.CollectionSentence(text));

    [Theory]
    [InlineData("Herein lie five tales of terror, short adventures for a single night each.", true)]
    [InlineData("Four linked adventures, each also playable on its own.", true)]
    [InlineData("Four linked adventures take the heroes from the mine to the tower.", false)]
    [InlineData("An adventure for four characters of 3rd level", false)]
    [InlineData("This adventure can be played as a one-shot", false)]
    public void A_models_quote_must_be_about_several_separate_adventures(string quote, bool describes) =>
        Assert.Equal(describes, PartRules.DescribesCollection(quote));

    [Fact]
    public void A_book_describes_itself_before_its_first_adventure_or_on_its_back_cover()
    {
        Assert.Equal([0, 1, 2, 8, 9], PartRules.DescriptionPages(10, 2));
        Assert.Equal([.. Enumerable.Range(0, PartRules.FrontMatterPages), 98, 99], PartRules.DescriptionPages(100, 60));
        Assert.Equal([0], PartRules.DescriptionPages(1, 0));
    }

    [Theory]
    [InlineData("for four characters of 1st level", "4")]
    [InlineData("four to six 5th-level characters", "4-6")]
    [InlineData("for four to six characters of 3rd level", "4-6")]
    [InlineData("FOUR 1ST- TO 2ND-LEVEL PCS", "4")]
    [InlineData("An adventure for 3-5 players of 2nd level", "3-5")]
    [InlineData("for a party of 5", "5")]
    [InlineData("for characters of levels 2-4", null)]
    [InlineData("for 4th-level characters", null)]
    [InlineData(null, null)]
    public void A_blurb_gives_the_party_size_when_it_counts_the_characters(string? quote, string? players) =>
        Assert.Equal(players, PartRules.Party(quote)?.ToString());

    [Fact]
    public void A_blurbs_quote_takes_in_the_party_size_written_around_it_and_not_another_groups()
    {
        string[] pages =
        [
            "Six heroes vanished last spring.\nThe Fog Market\nAn adventure for four to six 5th-level characters",
            "For four players, levels 2-3",
            "The Glass Tower\nAn adventure for characters of 7th level",
        ];

        var fog = PartRules.ReadBlurb(pages, new PartPages(0, 0))!;
        Assert.Equal((0, "four to six 5th-level characters", "5", "4-6"), (fog.Page, fog.Quote, fog.Levels?.ToString(), fog.Players?.ToString()));
        var meet = PartRules.ReadBlurb(pages, new PartPages(1, 1))!;
        Assert.Equal(("four players, levels 2-3", "2-3", "4"), (meet.Quote, meet.Levels?.ToString(), meet.Players?.ToString()));
        var tower = PartRules.ReadBlurb(pages, new PartPages(2, 2))!;
        Assert.Equal(("for characters of 7th level", "7", (string?)null), (tower.Quote, tower.Levels?.ToString(), tower.Players?.ToString()));
        Assert.Null(PartRules.ReadBlurb(["The mine's shafts run deep."], new PartPages(0, 0)));
    }

    [Theory]
    [InlineData("Credits")]
    [InlineData("INTRODUCTION")]
    [InlineData("Appendix B: Magic Items")]
    [InlineData("Index")]
    [InlineData("OGL")]
    [InlineData("Open Game License Version 1.0a")]
    [InlineData("Table of Contents")]
    [InlineData("Back Cover")]
    [InlineData("(AD) Another Book")]
    [InlineData("1. Introduction")]
    [InlineData("Chapter 2: Credits")]
    public void Front_and_back_matter_is_never_an_adventure(string title) => Assert.True(PartRules.IsMatter(title));

    [Theory]
    [InlineData("The Salt Mine")]
    [InlineData("Indexing the Archive")]
    [InlineData("Legacy of the Glass Tower")]
    [InlineData("2. The Salt Mine")]
    [InlineData("Covered Bridge")]
    public void An_adventure_title_is_not_matter(string title) => Assert.False(PartRules.IsMatter(title));
}

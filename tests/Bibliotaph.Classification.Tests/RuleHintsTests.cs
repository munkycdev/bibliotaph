using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Classification.Tests;

public class RuleHintsTests
{
    static readonly Vocabulary Vocabulary = new(
        [
            new Term("system", "dnd", "Dungeons & Dragons", "D&D"),
            new Term("system", "pathfinder", "Pathfinder"),
            new Term("edition", "dnd-5e", "5th edition", "5e", "dnd"),
            new Term("edition", "pathfinder-2e", "Pathfinder 2nd edition", "PF2e", "pathfinder"),
            new Term("type", "adventure", "Adventure"),
            new Term("type", "map-pack", "Map pack"),
            new Term("theme", "horror", "Horror"),
            new Term("publisher", "kobold-press", "Kobold Press"),
        ],
        [
            ("edition", "dnd-5e", "D&D 5e"), ("type", "adventure", "adventures"), ("type", "adventure", "one shots"),
            ("type", "map-pack", "maps"), ("edition", "pathfinder-2e", "PF2"),
        ]);

    static string Sep(params string[] parts) => Path.Combine(parts);

    static (string Field, string Value, AssertionOrigin Origin)[] Propose(HintSource source,
        IReadOnlySet<(string, string, string)>? ignored = null) =>
        [.. RuleHints.Propose(source, Vocabulary, ignored).Select(p => (p.Field.Key, p.Value, p.Origin)).OrderBy(p => p.Key).ThenBy(p => p.Value)];

    [Fact]
    public void Folder_names_suggest_vocabulary_terms_and_an_edition_its_system()
    {
        var proposals = RuleHints.Propose(new HintSource(Sep("D&D 5e", "Adventures", "One Shots", "Tomb of Annihilation.pdf")), Vocabulary);

        Assert.Contains(proposals, p => p.Field == MetadataFields.Edition && p.Value == "dnd-5e" && p.Origin == AssertionOrigin.Folder && p.Quote == "D&D 5e");
        Assert.Contains(proposals, p => p.Field == MetadataFields.System && p.Value == "dnd" && p.Origin == AssertionOrigin.Folder);
        Assert.Single(proposals, p => p.Field == MetadataFields.Types);
        Assert.Contains(proposals, p => p.Field == MetadataFields.Title && p.Value == "Tomb of Annihilation" && p.Origin == AssertionOrigin.Filename);
    }

    [Fact]
    public void A_deeper_folder_wins_a_single_value_field()
    {
        var proposals = RuleHints.Propose(new HintSource(Sep("D&D 5e", "Pathfinder 2e conversions", "PF2", "Book.pdf")), Vocabulary);

        Assert.Equal("pathfinder-2e", Assert.Single(proposals, p => p.Field == MetadataFields.Edition).Value);
        Assert.Equal("pathfinder", Assert.Single(proposals, p => p.Field == MetadataFields.System).Value);
    }

    [Fact]
    public void A_publisher_folder_suggests_the_publishers_name()
    {
        var proposals = RuleHints.Propose(new HintSource(Sep("Kobold Press", "Tome.pdf")), Vocabulary);

        Assert.Equal("Kobold Press", Assert.Single(proposals, p => p.Field == MetadataFields.Publisher).Value);
    }

    [Fact]
    public void A_folder_label_the_user_switched_off_is_skipped()
    {
        var proposals = Propose(new HintSource(Sep("Maps", "Cave.png")), new HashSet<(string, string, string)> { ("maps", "type", "map-pack") });

        Assert.DoesNotContain(proposals, p => p.Field == "type");
    }

    [Fact]
    public void Folder_words_that_arent_terms_suggest_nothing()
    {
        Assert.Equal([("title", "Cave", AssertionOrigin.Filename)], Propose(new HintSource(Sep("Stuff", "Downloads 2021", "Cave.png"))));
    }

    [Fact]
    public void The_pdfs_own_title_authors_and_keywords_are_used()
    {
        var proposals = RuleHints.Propose(
            new HintSource("tomb_v3_final.pdf", "Tomb of the Serpent King", "Jane Doe; John Roe and Administrator", EmbeddedKeywords: "horror, D&D 5e, dungeon"),
            Vocabulary);

        Assert.Contains(proposals, p => p.Field == MetadataFields.Title && p.Value == "Tomb of the Serpent King" && p.Origin == AssertionOrigin.Embedded);
        Assert.Equal(["Jane Doe", "John Roe"], proposals.Where(p => p.Field == MetadataFields.Authors).Select(p => p.Value));
        Assert.Contains(proposals, p => p.Field == MetadataFields.Themes && p.Value == "horror" && p.Origin == AssertionOrigin.Embedded);
        Assert.Contains(proposals, p => p.Field == MetadataFields.Edition && p.Value == "dnd-5e" && p.Origin == AssertionOrigin.Embedded);
    }

    [Theory]
    [InlineData("Microsoft Word - Tomb draft.docx")]
    [InlineData("Untitled")]
    [InlineData("Document1")]
    [InlineData("ToA_Cover_v3")]
    [InlineData("tomb-of-horrors-final")]
    [InlineData("layout.indd")]
    [InlineData("12345")]
    [InlineData("ab")]
    public void Titles_left_by_authoring_software_are_ignored(string title) => Assert.True(RuleHints.IsJunkTitle(title));

    [Theory]
    [InlineData("Tomb of Horrors")]
    [InlineData("Curse of Strahd: Revamped")]
    [InlineData("1001 Dungeon Rooms")]
    [InlineData("Half-Elf Heritage")]
    public void Real_titles_are_kept(string title) => Assert.False(RuleHints.IsJunkTitle(title));

    [Theory]
    [InlineData("Jane Doe, John Roe", new[] { "Jane Doe", "John Roe" })]
    [InlineData("Administrator", new string[0])]
    [InlineData("someone@example.com", new string[0])]
    [InlineData("Jane Doe & Jane Doe", new[] { "Jane Doe" })]
    public void Authors_are_split_and_placeholders_dropped(string author, string[] expected) =>
        Assert.Equal(expected, RuleHints.Authors(author));

    [Theory]
    [InlineData("Sunless Citadel (Levels 1-3)", "1-3")]
    [InlineData("Adventure_Level_5", "5")]
    [InlineData("lvl 3 - 5 crawl", "3-5")]
    [InlineData("For 1st to 4th level characters", "1-4")]
    [InlineData("A 3rd-level adventure", "3")]
    [InlineData("Levels 1 to 20", "1-20")]
    public void Level_ranges_written_out_are_found(string text, string expected) =>
        Assert.Equal(expected, RuleHints.FindLevels(text)?.Range.ToString());

    [Theory]
    [InlineData("Level Up Advanced")]
    [InlineData("Dungeon of 1000 doors")]
    [InlineData("Level 99")]
    public void Text_without_a_level_range_gives_none(string text) => Assert.Null(RuleHints.FindLevels(text));

    [Fact]
    public void Levels_come_from_a_pdfs_name_but_not_from_folders_or_images()
    {
        Assert.Contains(("levels", "1-3", AssertionOrigin.Rule), Propose(new HintSource("Sunless Citadel (Levels 1-3).pdf")));
        Assert.DoesNotContain(Propose(new HintSource(Sep("Level 2", "Crypt.pdf"))), p => p.Field == "levels");
        Assert.DoesNotContain(Propose(new HintSource("Dungeon Level 2.png")), p => p.Field == "levels");
    }
}

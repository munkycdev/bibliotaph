using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class BookRecordsTests
{
    static BookRecord Record(string title, string? published = "March 2019") =>
        new("Open Library", "OL1M", "9781234567897", title, ["A. Example", "B. Example"], ["Example Press", "Sample Games"], published);

    [Theory]
    [InlineData("The Owlbear Mill", true)]
    [InlineData("The Owlbear Mill: A Tale of Eggs", true)] // the subtitle needn't be printed
    [InlineData("THE OWLBEAR MILL", true)]
    [InlineData("The Owlbear's Mill", false)]
    [InlineData("The Goblin Warrens", false)]
    public void A_record_is_used_only_when_its_title_is_printed_in_the_book(string title, bool used)
    {
        string?[] places = [null, "", "Writing: A. Example\nTHE   OWLBEAR — MILL\nAn adventure for four players", "owlbear_mill_v2.pdf"];

        Assert.Equal(used, BookRecords.IsNamedIn(Record(title), places));
    }

    [Fact]
    public void Accents_and_punctuation_dont_stop_a_title_matching() =>
        Assert.True(BookRecords.IsNamedIn(Record("Café Tales, Volume 2"), ["CAFE TALES VOLUME 2"]));

    [Fact]
    public void Broken_characters_in_a_books_text_are_passed_over() =>
        Assert.True(BookRecords.IsNamedIn(Record("The Owlbear Mill"), ["\uD83D The Owlbear Mill \uDC00\uFFFE"]));

    [Fact]
    public void A_record_suggests_its_title_authors_first_publisher_and_year()
    {
        var proposals = BookRecords.Proposals([Record("The Owlbear Mill")]);

        Assert.Equal(
            [("title", "The Owlbear Mill"), ("authors", "A. Example"), ("authors", "B. Example"), ("publisher", "Example Press"), ("year", "2019")],
            proposals.Select(p => (p.Field.Key, p.Value)));
        Assert.All(proposals, p => Assert.Equal((AssertionOrigin.Lookup, "Open Library's record of The Owlbear Mill"), (p.Origin, p.Quote)));
    }

    [Fact]
    public void A_field_shows_RPGGeeks_data_when_a_value_or_another_suggestion_is_quoted_from_its_record()
    {
        static EffectiveValue Value(string quote) => new("The Owlbear Mill", "the owlbear mill", false,
            [new MetadataClaim(1, "title", "The Owlbear Mill", "the owlbear mill", AssertionOrigin.Lookup, AssertionState.Provisional, DateTime.UtcNow, quote)]);

        Assert.True(BookRecords.Shows(new EffectiveField(MetadataFields.Title, [], [Value("RPGGeek's record of The Owlbear Mill")]), "RPGGeek"));
        Assert.False(BookRecords.Shows(new EffectiveField(MetadataFields.Title, [Value("Open Library's record of The Owlbear Mill")], []), "RPGGeek"));
    }

    [Fact]
    public void A_game_catalogues_record_suggests_its_artists_and_mechanics()
    {
        var record = Record("The Owlbear Mill") with
        {
            Source = "RPGGeek",
            Values = [new CatalogueValue("artists", "C. Painter"), new CatalogueValue("mechanics", "Dice (d20)"), new CatalogueValue("mechanics", "Class-based")],
        };

        var proposals = BookRecords.Proposals([record]);

        Assert.Equal(["C. Painter"], proposals.Where(p => p.Field == MetadataFields.Artists).Select(p => p.Value));
        Assert.Equal(["Dice (d20)", "Class-based"], proposals.Where(p => p.Field == MetadataFields.Mechanics).Select(p => p.Value));
    }

    [Fact]
    public void A_record_found_by_title_alone_is_the_books_when_its_authors_publisher_or_page_count_agree()
    {
        var record = new BookRecord("AdventureLookup", "902", "", "The Owlbear Mill", ["Ada Quill"], ["Gull Rock Games"], "2019", Pages: 32);
        string?[] front = ["THE OWLBEAR MILL", "Written by Ada Quill. Cover by C. Painter."];
        string?[] other = ["THE OWLBEAR MILL", "A Sample Press adventure"];

        Assert.True(BookRecords.ByTitle(record));
        Assert.Equal("title", BookRecords.FoundBy(record));
        Assert.False(BookRecords.ByTitle(Record("The Owlbear Mill")));
        Assert.True(BookRecords.Agrees(record, front, pages: 120));
        Assert.True(BookRecords.Agrees(record with { Authors = [] }, ["The Owlbear Mill", "© Gull Rock Games, 2019"], pages: 120));
        Assert.True(BookRecords.Agrees(record, other, pages: 34));
        Assert.False(BookRecords.Agrees(record, other, pages: 35));
        Assert.False(BookRecords.Agrees(record with { Pages = null }, other, pages: 32));
        // A name only a few letters long is printed by chance too often to count.
        Assert.False(BookRecords.Agrees(record with { Authors = ["Al"], Publishers = [], Pages = null }, ["Al and the owlbear mill"], pages: null));
    }

    [Fact]
    public void An_adventure_catalogues_record_suggests_its_levels_and_files_it_under_its_environments()
    {
        var record = Record("The Owlbear Mill") with
        {
            Source = "AdventureLookup",
            Values = [new CatalogueValue("levels", "1-3"), new CatalogueValue("environment", "Underground"), new CatalogueValue("environment", "Forest")],
        };
        var vocabulary = new Vocabulary([new Term("environment", "underground", "Underground"), new Term("environment", "forest", "Forest")], []);

        var levels = BookRecords.Proposals([record]).Single(p => p.Field == MetadataFields.Levels);
        var terms = BookRecords.Terms([record], vocabulary);

        Assert.Equal("1-3", levels.Value);
        Assert.Equal(["underground", "forest"], terms.Where(t => t.Field == MetadataFields.Environments).Select(t => t.Term?.Key));
    }

    [Fact]
    public void An_adventure_catalogues_record_suggests_its_monsters_and_items_and_what_it_includes_from_the_fixed_list()
    {
        var record = Record("The Owlbear Mill") with
        {
            Source = "AdventureLookup",
            Values =
            [
                new CatalogueValue("monsters", "Owlbear"), new CatalogueValue("monsters", "Goblin"), new CatalogueValue("items", "Wand of Example"),
                new CatalogueValue("includes", "Handouts"), new CatalogueValue("includes", "Puzzles"),
            ],
        };
        var vocabulary = new Vocabulary([new Term("includes", "handouts", "Handouts")], []);

        var proposals = BookRecords.Proposals([record]);
        var terms = BookRecords.Terms([record], vocabulary);

        Assert.Equal(["Owlbear", "Goblin"], proposals.Where(p => p.Field == MetadataFields.Monsters).Select(p => p.Value));
        Assert.Equal(["Wand of Example"], proposals.Where(p => p.Field == MetadataFields.Items).Select(p => p.Value));
        // A fixed list, as the types are: a name it doesn't have isn't proposed as a new one.
        var included = Assert.Single(terms, t => t.Field == MetadataFields.Includes);
        Assert.Equal("handouts", included.Term?.Key);
    }

    [Fact]
    public void Each_field_comes_from_the_first_record_that_gives_it()
    {
        var rpggeek = new BookRecord("RPGGeek", "1", "9781234567897", "The Owlbear Mill", ["C. Designer"], [], null);
        var openLibrary = Record("Owlbear Mill") with { Authors = [] };

        var proposals = BookRecords.Proposals([rpggeek, openLibrary]);

        Assert.Equal(
            [("title", "The Owlbear Mill", "RPGGeek"), ("authors", "C. Designer", "RPGGeek"), ("publisher", "Example Press", "Open Library"), ("year", "2019", "Open Library")],
            proposals.Select(p => (p.Field.Key, p.Value, p.Quote![..p.Quote!.IndexOf('\'', StringComparison.Ordinal)])));
    }

    [Theory]
    [InlineData("1985", "1985")]
    [InlineData("c1985", "1985")]
    [InlineData("March 3, 2019", "2019")]
    [InlineData("n.d.", null)]
    [InlineData(null, null)]
    public void The_year_is_read_from_the_catalogues_date(string? published, string? year) =>
        Assert.Equal(year, BookRecords.Year(published));

    [Fact]
    public void A_series_comes_from_the_first_record_that_files_the_book_in_one_and_a_catch_all_isnt_one()
    {
        var rpggeek = new BookRecord("RPGGeek", "1", "9781234567897", "The Owlbear Mill", [], [], null,
            [new CatalogueValue("series", "Misc."), new CatalogueValue("series", "Example Line"), new CatalogueValue("theme", "Horror")]);

        var series = Assert.Single(BookRecords.Proposals([Record("The Owlbear Mill"), rpggeek]), p => p.Field == MetadataFields.Series);

        Assert.Equal(("Example Line", "RPGGeek's record of The Owlbear Mill"), (series.Value, series.Quote));
    }

    [Fact]
    public void Terms_come_from_the_first_record_that_files_the_book_under_that_field()
    {
        var vocabulary = new Vocabulary([new Term("theme", "horror", "Horror"), new Term("system", "example", "Example System", "ES"),
            new Term("edition", "example-2e", "2nd edition", "2e", "example")], [("edition", "example-2e", "ES 2nd Edition")]);
        var first = new BookRecord("RPGGeek", "1", "9781234567897", "The Owlbear Mill", [], [], null,
            [new CatalogueValue("theme", "Horror (Supernatural)"), new CatalogueValue("theme", "Fantasy (High Fantasy)"), new CatalogueValue("system", "Example System (2nd Edition)")]);
        var second = first with { Source = "Other", Values = [new CatalogueValue("theme", "Mystery"), new CatalogueValue("setting", "Example World")] };

        var terms = BookRecords.Terms([first, second], vocabulary);

        Assert.Equal(["system=example", "edition=example-2e", "theme=horror", "setting:Example World"],
            terms.Select(t => t.Term is { } term ? $"{t.Field.Key}={term.Key}" : $"{t.Field.Key}:{t.Label}"));
        Assert.Equal(["RPGGeek", "RPGGeek", "RPGGeek", "Other"], terms.Select(t => t.Quote.Split('\'')[0]));
    }
}

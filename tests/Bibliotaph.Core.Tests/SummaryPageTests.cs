namespace Bibliotaph.Core.Tests;

/// <summary>The Summary page as both clients draw it: what each figure says and where it goes.</summary>
public sealed class SummaryPageTests
{
    static SummaryPage Page(LibrarySummary summary) => SummaryPage.Of(summary, f => (f.Path.ToUpperInvariant(), $"at {f.Path}"));

    static SummaryValue GameSystem(int i, long count, long confirmed = 0) => new("system", $"s{i}", $"System {i}", count, confirmed);

    [Fact]
    public void A_long_list_shows_the_most_common_then_the_rest_as_one_line()
    {
        var page = Page(new LibrarySummary { Books = 100, Systems = [.. Enumerable.Range(1, 9).Select(i => GameSystem(i, 20 - i, 10 - i))], SystemUnknown = 4 });

        var systems = page.Systems!;
        Assert.Equal("9 in all", systems.Note);
        Assert.Equal(["System 1", "System 2", "System 3", "System 4", "System 5", "System 6", "3 others", "Not cataloged yet"],
            systems.Bars.Select(b => b.Label));
        // Bars are fractions of the longest, which here is the three folded together.
        Assert.Equal((1.0, 36L), (systems.Bars[6].Total, long.Parse(systems.Bars[6].Count, global::System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(new SummaryFilter("system", "s1"), systems.Bars[0].Link!.Filter);
        Assert.Null(systems.Bars[6].Link);
        Assert.Equal("system:unknown", systems.Bars[^1].Link!.Query);
        Assert.Equal("System 1: 19 books, 9 confirmed and 10 suggested", systems.Bars[0].ToolTip);
    }

    [Fact]
    public void Folders_are_named_the_clients_way_and_open_the_library_at_their_books()
    {
        var page = Page(new LibrarySummary { Books = 30, Folders = [new SummaryFolder(4, "rpg", 20, 2_000_000_000, false), new SummaryFolder(5, "old", 10, 0, true)] });

        Assert.Equal(["RPG", "OLD (offline)"], page.Folders!.Bars.Select(b => b.Label));
        Assert.Equal("20 · 2 GB", page.Folders.Bars[0].Count);
        Assert.Equal("at rpg: 20 books, 2 GB", page.Folders.Bars[0].ToolTip);
        Assert.Equal((4L, 0.5), (page.Folders.Bars[0].Link!.Filter!.Folder!.Value, page.Folders.Bars[1].Total));
        Assert.True(page.Folders.Bars[1].Offline);
    }

    [Fact]
    public void Grouped_folders_share_one_row_that_opens_their_collections()
    {
        var page = Page(new LibrarySummary
        {
            Books = 60,
            Folders =
            [
                new SummaryFolder(4, "rpg", 30, 3_000_000_000, false),
                new SummaryFolder(7, "1f0c", 20, 1_000_000_000, false, "Nornis"),
                new SummaryFolder(8, "9a2e", 15, 1_000_000_000, true, "Nornis"),
            ],
        });

        Assert.Equal(["Nornis", "RPG"], page.Folders!.Bars.Select(b => b.Label));
        Assert.Equal("35 · 2 GB", page.Folders.Bars[0].Count);
        Assert.Equal("Nornis, 2 folders: 35 books, 2 GB", page.Folders.Bars[0].ToolTip);
        Assert.Equal(SummaryGoal.Collections, page.Folders.Bars[0].Link!.Goal);
        Assert.False(page.Folders.Bars[0].Offline);
    }

    [Fact]
    public void A_group_of_one_opens_the_library_at_its_folder()
    {
        var page = Page(new LibrarySummary { Books = 5, Folders = [new SummaryFolder(7, "1f0c", 5, 0, true, "Nornis")] });

        Assert.Equal("Nornis (offline)", page.Folders!.Bars.Single().Label);
        Assert.Equal(7L, page.Folders.Bars[0].Link!.Filter!.Folder);
    }

    [Fact]
    public void Tiles_open_the_books_they_count()
    {
        var page = Page(new LibrarySummary
        {
            Books = 10, Ebooks = 2, Packs = 1, Duplicates = 3, Elsewhere = 4, Adventures = 5,
            Types = [new("type", "adventure", "Adventure", 6, 6)], Collections = 1, Binders = 2, BinderItems = 9, ReadByAi = 7,
        });

        Assert.Equal(["Adventures", "E-books", "Packs", "Duplicates found", "Owned with no file"], page.Hoard.Select(t => t.Label));
        Assert.Equal(("6", "and 5 inside other books"), (page.Hoard[0].Count, page.Hoard[0].Detail));
        Assert.Equal(new SummaryFilter("type", "adventure", AdventureCards: true), page.Hoard[0].Link!.Filter);
        Assert.Equal("format:ebook", page.Hoard[1].Link!.Query);
        Assert.Equal("Packs", page.Hoard[2].Link!.Filter!.Kind);
        Assert.Equal(SummaryGoal.Collections, page.Work.Single(t => t.Label == "Collection").Link!.Goal);
        Assert.Equal("9 items ready", page.Work.Single(t => t.Label == "Binders").Detail);
        Assert.True(page.Work.Single(t => t.Label == "Read by AI").Link!.Filter!.ReadByAi);
        Assert.Equal("opened:no", SummaryPage.NeverOpened.Query);
    }

    [Fact]
    public void Strips_and_growth_are_fractions_of_their_largest()
    {
        long[] levels = [.. Enumerable.Range(1, SummaryText.TopLevel).Select(l => l == 3 ? 8L : 2L)];
        var page = Page(new LibrarySummary
        {
            Books = 8, Levels = levels,
            Decades = [new(1980, 1), new(1990, 4)],
            Growth = [new(new DateOnly(2026, 8, 1), 2), new(new DateOnly(2026, 9, 1), 4), new(new DateOnly(2026, 10, 1), 8)],
        });

        Assert.Equal((1.0, 0.25), (page.Levels[2].Share, page.Levels[0].Share));
        Assert.Equal(["1", "", "", "", "5"], page.Levels.Take(5).Select(c => c.Tick));
        Assert.Equal(3, page.Levels[2].Link!.Filter!.Level);
        Assert.Equal(["1980s", "1990s"], page.Decades.Select(d => d.Tick));
        Assert.Equal([0.25, 0.5, 1.0], page.Growth);
        Assert.Equal(("Aug 2026", "Sep", "Oct 2026"), (page.GrowthStart, page.GrowthMiddle, page.GrowthEnd));
        Assert.Equal("October 2026: 8 books", page.GrowthLabels[^1]);
    }

    [Fact]
    public void Values_from_online_lookups_open_the_filter_or_search_that_finds_them()
    {
        var page = Page(new LibrarySummary
        {
            Books = 40,
            Publishers = [new("publisher", "Kobold Press", "Kobold Press", 12, 12)],
            FoundOnline = [new("found", "RPGGeek", "RPGGeek", 20, 20), new("found", "Open Library", "Open Library", 8, 8)],
            NotFoundOnline = 15,
            Themes = [new("theme", "horror", "Horror", 6, 2)],
            Environments = [new("environment", "urban", "Urban", 5, 5)],
            Includes = [new("includes", "pregens", "Pre-generated characters", 4, 1)],
        });

        Assert.Equal(new SummaryFilter("publisher", "Kobold Press", Picked: true), page.Publishers!.Bars[0].Link!.Filter);
        // Publishers and catalogues are counted, not confirmed or suggested.
        Assert.Equal("Kobold Press: 12 books", page.Publishers.Bars[0].ToolTip);
        Assert.Equal(["RPGGeek", "Open Library", "Not found online"], page.FoundOnline!.Bars.Select(b => b.Label));
        Assert.Equal(new SummaryFilter("found", "RPGGeek"), page.FoundOnline.Bars[0].Link!.Filter);
        Assert.Equal(new SummaryFilter("found", "unknown"), page.FoundOnline.Bars[^1].Link!.Filter);
        Assert.Equal("RPGGeek: 20 books", page.FoundOnline.Bars[0].ToolTip);
        Assert.Equal(new SummaryFilter("theme", "horror", Picked: true), page.Themes!.Bars[0].Link!.Filter);
        Assert.Equal(new SummaryFilter("environment", "urban", Picked: true), page.Environments!.Bars[0].Link!.Filter);
        Assert.Equal(("What's included", "includes:pregens"), (page.Includes!.Heading, page.Includes.Bars[0].Link!.Query));
        Assert.Equal("Pre-generated characters: 4 books, 1 confirmed and 3 suggested", page.Includes.Bars[0].ToolTip);
    }

    [Fact]
    public void Nothing_found_online_yet_says_nothing_about_it()
    {
        var page = Page(new LibrarySummary { Books = 10, NotFoundOnline = 10 });

        Assert.Null(page.FoundOnline);
        Assert.Null(page.Publishers);
    }

    [Fact]
    public void An_empty_library_has_no_lists()
    {
        var page = Page(new LibrarySummary());

        Assert.False(page.HasBooks);
        Assert.Null(page.Systems);
        Assert.Null(page.Folders);
        Assert.Empty(page.Levels);
        Assert.False(page.HasGrowth);
    }
}

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

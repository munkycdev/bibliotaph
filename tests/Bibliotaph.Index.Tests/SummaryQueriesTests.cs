using System.Globalization;
using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index.Tests;

/// <summary>The Summary page's figures over a small made-up library, counted as the Library counts its cards.</summary>
public sealed class SummaryQueriesTests : IndexFixture
{
    const long Rulebook = 1, Novel = 2, Map = 3, TokenA = 4, TokenB = 5;
    static readonly EntryId Pack = new(50), Printed = new(70), Ordered = new(71), Chapter = new(80);

    SummaryQueries _summary = null!;
    IndexStore _store = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DateOnly Today => new(2026, 12, 15);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _summary = new SummaryQueries(Database);
        _store = new IndexStore(Writer, Clock);

        await AddAsync(Rulebook, "pdf", 300);
        Clock.Advance(TimeSpan.FromDays(40));
        await AddAsync(Novel, "epub", 120);
        await AddAsync(Map, "png", 1);
        await AddAsync(TokenA, "png", 1);
        await AddAsync(TokenB, "png", 1);
        // Two images kept as one pack, and the rulebook found twice.
        await _store.RemoveEntriesAsync([EntryOf(TokenA), EntryOf(TokenB)], Ct);
        await _store.SetEntriesAsync(
        [
            new EntryDocRow(Pack, TokenA, EntryKind.Pack) { Name = "Tokens", Members = [new(TokenA, EntryOf(TokenA), "a.png"), new(TokenB, EntryOf(TokenB), "b.png")] },
            new EntryDocRow(EntryOf(Rulebook), Rulebook, EntryKind.Whole, Copies: 2),
            new EntryDocRow(Printed, null, EntryKind.Elsewhere, Copies: 0) { AddedUtc = Clock.GetUtcNow().UtcDateTime },
            new EntryDocRow(Ordered, null, EntryKind.Elsewhere, Copies: 0) { AddedUtc = Clock.GetUtcNow().UtcDateTime },
            new EntryDocRow(Chapter, Rulebook, EntryKind.Part) { ParentEntryId = EntryOf(Rulebook), Pages = new PartPages(10, 20) },
        ], Ct);

        await _store.SetMetadataAsync(
        [
            new EntryMetaRow
            {
                EntryId = EntryOf(Rulebook), Year = 1985, LevelMin = 1, LevelMax = 3, Levels = LevelState.Known,
                Facets = [new("system", "fantasy", "Fantasy RPG", true), new("type", "rulebook", "Rulebook", true), new("store", "shop", "A shop", true)],
            },
            new EntryMetaRow
            {
                EntryId = EntryOf(Novel), Year = 2021,
                Facets = [new("system", "fantasy", "Fantasy RPG", false), new("setting", "isles", "The Isles", false)],
            },
            new EntryMetaRow { EntryId = Printed, Year = 1450, Facets = [new("own", "print", "Print", true), new("type", "adventure", "Adventure", true)] },
            new EntryMetaRow { EntryId = Ordered, Facets = [new("store", "shop", "A shop", true), new("system", "space", "Space RPG", true)] },
            new EntryMetaRow
            {
                EntryId = Chapter, LevelMin = 2, LevelMax = 25, Levels = LevelState.Known,
                Facets = [new("type", "adventure", "Adventure", true)],
            },
        ], Ct);

        await _store.SetOpenedAsync(null, new Dictionary<EntryId, DateTime> { [EntryOf(Novel)] = Clock.GetUtcNow().UtcDateTime }, Ct);
        await _store.SetScopesAsync(null, [(EntryOf(Map), ScopeKeys.Favorites)], Ct);
        await _store.SetAiReadAsync(null, new Dictionary<EntryId, string> { [EntryOf(Rulebook)] = "model" }, Ct);
        await _store.SetOcrPageAsync(Rulebook, 1, "Recognised text.", 0.9, [], Ct);
        Connection.Execute(
            "INSERT INTO stage_status (document_id, stage, status, stage_version, updated_utc) VALUES (@id, 'Text', 'Complete', 1, '2026-10-08T12:00:00Z')",
            new { id = Rulebook });
    }

    async Task AddAsync(long id, string format, int pages)
    {
        await _store.UpsertDocumentAsync(
            new DocRow { DocumentId = id, ContentHash = $"hash{id}", Format = format, DisplayTitle = $"Book {id}", PageCount = pages, FolderHint = "Shelf" },
            [.. Enumerable.Range(0, pages).Select(i => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], [], Ct);
        await AddEntriesAsync(_store, id);
    }

    Task<LibrarySummary> SummaryAsync(params EntryId[] without)
    {
        EntryId[] all = [EntryOf(Rulebook), EntryOf(Novel), EntryOf(Map), Pack, Printed, Ordered, Chapter];
        return _summary.GetAsync([.. all.Except(without)], Today, Ct);
    }

    [Fact]
    public async Task Cards_are_counted_as_the_library_shows_them()
    {
        var summary = await SummaryAsync();

        Assert.Equal((6L, 3L, 1L, 1L, 2L, 1L, 1L), (summary.Books, summary.Files, summary.Packs, summary.Adventures, summary.Elsewhere, summary.Ebooks, summary.Duplicates));
        // The rulebook counts once whatever its copies, and the pack through its images.
        Assert.Equal(300 + 120 + 1 + 2L, summary.Pages);
        Assert.Equal((1L, 1L, 1L, 1L, 1L), (summary.Opened, summary.Favorites, summary.ReadByAi, summary.TextRead, summary.OcrPages));
    }

    [Fact]
    public async Task An_empty_scope_counts_nothing()
    {
        var summary = await _summary.GetAsync([], Today, Ct);

        Assert.Equal((0L, 0L), (summary.Books, summary.Pages));
        Assert.Empty(summary.Systems);
        Assert.Empty(summary.Growth);
        Assert.Empty(summary.Decades);
        Assert.Null(summary.BiggestDay);
    }

    [Fact]
    public async Task Only_the_cards_in_scope_count()
    {
        var summary = await SummaryAsync(EntryOf(Novel), Printed);

        Assert.Equal((4L, 0L, 0L, 1L), (summary.Books, summary.Ebooks, summary.Opened, summary.Elsewhere));
        Assert.Equal(3L, await _summary.CountBooksAsync([EntryOf(Rulebook), EntryOf(Map), Pack, Chapter], Ct));
        Assert.Equal(0L, await _summary.CountBooksAsync([], Ct));
    }

    [Fact]
    public async Task Values_come_most_first_with_how_many_are_confirmed()
    {
        var summary = await SummaryAsync();

        Assert.Equal([("fantasy", 2L, 1L), ("space", 1L, 1L)], summary.Systems.Select(v => (v.Value, v.Count, v.Confirmed)));
        // The adventure inside the rulebook is a card of its own only when searched for, so it isn't a type here.
        Assert.Equal([("adventure", 1L), ("rulebook", 1L)], summary.Types.Select(v => (v.Value, v.Count)));
        Assert.Equal("The Isles", Assert.Single(summary.Settings).Label);
        Assert.Equal([("store", "shop", 2L), ("own", "print", 1L)], summary.Sources.Select(v => (v.Field, v.Value, v.Count)));
        // The map and the book in print have no system.
        Assert.Equal(2L, summary.SystemUnknown);
        Assert.Equal((1L, 1L), (summary.StoreDownloaded, summary.StoreWaiting));
    }

    [Fact]
    public async Task Levels_count_each_level_a_book_or_adventure_covers_up_to_the_top()
    {
        var summary = await SummaryAsync();

        Assert.Equal(SummaryText.TopLevel, summary.Levels.Count);
        Assert.Equal([1L, 2L, 2L, 1L], summary.Levels.Take(4));
        Assert.Equal(1L, summary.Levels[^1]);
        // The adventure in print, whose levels nobody knows yet.
        Assert.Equal(1L, summary.LevelsUnknown);
        Assert.Equal("Level 2 is your strongest shelf: 2 books.", SummaryText.Levels(summary));
    }

    [Fact]
    public async Task Years_fill_every_decade_between_and_leave_out_unlikely_ones()
    {
        var summary = await SummaryAsync();

        Assert.Equal((1985, 2021), (summary.OldestYear, summary.NewestYear));
        Assert.Equal([(1980, 1L), (1990, 0L), (2000, 0L), (2010, 0L), (2020, 1L)], summary.Decades.Select(d => (d.Decade, d.Count)));
        // The map, the book with no year and the one whose year is a slip.
        Assert.Equal(3L, summary.YearUnknown);
    }

    [Fact]
    public async Task Growth_runs_from_the_first_month_to_this_one_with_the_biggest_day()
    {
        var summary = await SummaryAsync();

        // The rulebook in October, the rest 40 days later, and nothing since.
        Assert.Equal([(new DateOnly(2026, 10, 1), 1L), (new DateOnly(2026, 11, 1), 6L), (new DateOnly(2026, 12, 1), 6L)],
            summary.Growth.Select(m => (m.Month, m.Total)));
        Assert.Equal(new SummaryDay(new DateOnly(2026, 11, 17), 5), summary.BiggestDay);
    }
}

using Bibliotaph.Core.Search;

namespace Bibliotaph.Index.Tests;

/// <summary>Projected metadata in the library: titles, card labels, field search, filters, facet counts and levels.</summary>
public sealed class MetadataSearchTests : IndexFixture
{
    const long Tomb = 1, Citadel = 2, Bestiary = 3, Map = 4, Unknown = 5;

    LibraryQueries _library = null!;
    IndexStore _store = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _library = new LibraryQueries(Database);
        _store = new IndexStore(Writer, Clock);
        foreach (var (id, title, format) in new[] { (Tomb, "tomb_final", "pdf"), (Citadel, "Sunless Citadel", "pdf"), (Bestiary, "Creature Codex", "pdf"),
                     (Map, "Cave Map", "png"), (Unknown, "Mystery Book", "pdf") })
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await _store.UpsertDocumentAsync(new DocRow { DocumentId = id, ContentHash = $"h{id}", Format = format, DisplayTitle = title }, [], [], Ct);
            await AddEntriesAsync(_store, id);
        }
        await _store.SetTermAliasesAsync(
            [
                new("system", "d&d", "dnd"), new("system", "dnd", "dnd"), new("edition", "5e", "dnd-5e"), new("edition", "d&d 5e", "dnd-5e"),
                new("edition", "pf2e", "pathfinder-2e"), new("system", "pathfinder", "pathfinder"), new("type", "module", "adventure"),
            ], Ct);
        await _store.SetMetadataAsync(
            [
                new EntryMetaRow
                {
                    EntryId = EntryOf(Tomb), Title = "Tomb of Horrors", Publisher = "TSR", Authors = "Gary Gygax", SystemLabel = "D&D 5e", KindLabel = "Adventure",
                    LevelMin = 10, LevelMax = 14, Levels = LevelState.Known, ConfirmedText = "5th edition · 5e",
                    Facets = [new("system", "dnd", "Dungeons & Dragons", true), new("edition", "dnd-5e", "5th edition", true), new("type", "adventure", "Adventure", false), new("theme", "horror", "Horror", false)],
                },
                new EntryMetaRow
                {
                    EntryId = EntryOf(Citadel), Publisher = "Wizards of the Coast", SystemLabel = "D&D 5e", KindLabel = "Adventure", LevelMin = 1, LevelMax = 3, Levels = LevelState.Known,
                    Facets = [new("system", "dnd", "Dungeons & Dragons", false), new("edition", "dnd-5e", "5th edition", false), new("type", "adventure", "Adventure", false)],
                },
                new EntryMetaRow
                {
                    EntryId = EntryOf(Bestiary), Publisher = "Kobold Press", SystemLabel = "PF2e", KindLabel = "Bestiary", Levels = LevelState.NotApplicable, Reviews = 1,
                    Facets = [new("system", "pathfinder", "Pathfinder", false), new("edition", "pathfinder-2e", "Pathfinder 2nd edition", false), new("type", "bestiary", "Bestiary", false)],
                },
                new EntryMetaRow { EntryId = EntryOf(Map), Facets = [new("type", "map-pack", "Map pack", true)] },
            ], Ct);
    }

    static SearchPlan Plan(string query) => SearchPlan.From(SearchQuery.Parse(query));

    async Task<IEnumerable<long>> FindAsync(string query, LibraryFilter? filter = null) =>
        (await _library.SearchDocumentsAsync(Plan(query), filter ?? new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).Select(e => e.DocumentId).Order();

    [Fact]
    public async Task Entries_carry_the_effective_title_and_card_labels()
    {
        var entries = (await _library.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.DocumentId);

        Assert.Equal("Tomb of Horrors", entries[Tomb].Title);
        Assert.Equal("Sunless Citadel", entries[Citadel].Title); // no effective title: the file name's
        Assert.Equal(("D&D 5e", "Adventure", "TSR", "Levels 10–14"), (entries[Tomb].System, entries[Tomb].Kind, entries[Tomb].Publisher, entries[Tomb].Levels));
        Assert.Equal("No levels", entries[Bestiary].Levels);
        Assert.True(entries[Bestiary].NeedsReview);
        Assert.Null(entries[Unknown].System);
        Assert.Null(entries[Unknown].Levels);
    }

    [Fact]
    public async Task Words_find_documents_by_effective_title_metadata_and_the_file_names_title()
    {
        Assert.Equal([Tomb], await FindAsync("horrors"));
        Assert.Equal([Tomb], await FindAsync("tomb_final"));
        Assert.Equal([Tomb], await FindAsync("gygax"));
        Assert.Equal([Tomb], await FindAsync("5e"));
    }

    [Fact]
    public async Task Text_fields_search_their_own_column()
    {
        Assert.Equal([Bestiary], await FindAsync("publisher:kobold"));
        Assert.Equal([Tomb], await FindAsync("author:gygax"));
        Assert.Equal([Tomb], await FindAsync("title:horrors"));
        Assert.Equal([Citadel, Bestiary, Map, Unknown], await FindAsync("-publisher:tsr"));
        Assert.Empty(await FindAsync("publisher:gygax"));
    }

    [Theory]
    [InlineData("system:dnd", new[] { Tomb, Citadel })]
    [InlineData("system:\"D&D\"", new[] { Tomb, Citadel })]
    [InlineData("system:5e", new[] { Tomb, Citadel })]
    [InlineData("system:\"D&D 5e\"", new[] { Tomb, Citadel })]
    [InlineData("system:pathfinder", new[] { Bestiary })]
    [InlineData("system:pf2e", new[] { Bestiary })]
    [InlineData("system:dungeons", new[] { Tomb, Citadel })]
    [InlineData("system:path*", new[] { Bestiary })]
    [InlineData("edition:5e", new[] { Tomb, Citadel })]
    [InlineData("type:adventure", new[] { Tomb, Citadel })]
    [InlineData("type:module", new[] { Tomb, Citadel })]
    [InlineData("type:map", new[] { Map })]
    [InlineData("-type:adventure", new[] { Bestiary, Map, Unknown })]
    [InlineData("system:unknown", new[] { Map, Unknown })]
    [InlineData("-system:unknown", new[] { Tomb, Citadel, Bestiary })]
    [InlineData("theme:horror", new[] { Tomb })]
    [InlineData("type:adventure system:dnd -theme:horror", new[] { Citadel })]
    public async Task Vocabulary_fields_match_any_name_of_a_term(string query, long[] expected) =>
        Assert.Equal(expected, await FindAsync(query));

    [Theory]
    [InlineData("level:3", new[] { Citadel })]
    [InlineData("level:2-11", new[] { Tomb, Citadel })]
    [InlineData("level:none", new[] { Bestiary })]
    [InlineData("level:unknown", new[] { Map, Unknown })]
    [InlineData("-level:unknown", new[] { Tomb, Citadel, Bestiary })]
    [InlineData("-level:12", new[] { Citadel, Bestiary, Map, Unknown })]
    public async Task Levels_match_by_overlap_and_unknown_is_its_own_value(string query, long[] expected) =>
        Assert.Equal(expected, await FindAsync(query));

    [Fact]
    public async Task A12_a_level_filter_leaves_out_unknown_levels_unless_asked()
    {
        var strict = await _library.ListAsync(new LibraryFilter(Level: 2), ct: Ct);
        var broad = await _library.ListAsync(new LibraryFilter(Level: 2, IncludeUnknownLevel: true), ct: Ct);

        Assert.Equal([Citadel], strict.Select(e => e.DocumentId));
        Assert.Equal([Citadel, Map, Unknown], broad.Select(e => e.DocumentId).Order());
    }

    [Fact]
    public async Task Filters_choose_systems_and_types_with_unknown_as_a_choice()
    {
        Assert.Equal([Tomb, Citadel], (await _library.ListAsync(new LibraryFilter(Systems: ["dnd"]), ct: Ct)).Select(e => e.DocumentId).Order());
        Assert.Equal([Bestiary, Map, Unknown],
            (await _library.ListAsync(new LibraryFilter(Systems: ["pathfinder", SearchQuery.Unknown]), ct: Ct)).Select(e => e.DocumentId).Order());
        Assert.Equal([Citadel], (await _library.ListAsync(new LibraryFilter(Systems: ["dnd"], Types: ["adventure"], Level: 3), ct: Ct)).Select(e => e.DocumentId));
    }

    [Fact]
    public async Task Facet_counts_follow_the_rest_of_the_filter_and_count_unknowns()
    {
        var systems = await _library.GetFacetCountsAsync("system", new LibraryFilter(), ct: Ct);
        Assert.Equal([("dnd", 2L), ("pathfinder", 1L), (SearchQuery.Unknown, 2L)], systems.Select(c => (c.Value, c.Count)));
        Assert.Equal("Dungeons & Dragons", systems[0].Label);

        var adventureSystems = await _library.GetFacetCountsAsync("system", new LibraryFilter(Types: ["adventure"]), ct: Ct);
        Assert.Equal([("dnd", 2L)], adventureSystems.Select(c => (c.Value, c.Count)));

        var horror = await _library.GetFacetCountsAsync("type", new LibraryFilter(), Plan("horrors"), Ct);
        Assert.Equal([("adventure", 1L)], horror.Select(c => (c.Value, c.Count)));
    }

    [Fact]
    public async Task Format_counts_are_most_common_first_within_the_scope()
    {
        var formats = await _library.GetFormatCountsAsync(new LibraryFilter(), Ct);
        Assert.Equal([("pdf", "PDF", 4L), ("png", "PNG", 1L)], formats.Select(c => (c.Value, c.Label, c.Count)));

        var scoped = await _library.GetFormatCountsAsync(new LibraryFilter(Scope: [EntryOf(Map), EntryOf(Tomb)]), Ct);
        Assert.Equal([("pdf", 1L), ("png", 1L)], scoped.Select(c => (c.Value, c.Count)));
    }

    [Fact]
    public async Task Sorting_by_publisher_puts_documents_without_one_last()
    {
        var entries = await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.Publisher), ct: Ct);

        Assert.Equal([Bestiary, Tomb, Citadel, Map, Unknown], entries.Select(e => e.DocumentId).ToArray()[..3].Concat(entries.Skip(3).Select(e => e.DocumentId).Order()));
    }

    [Fact]
    public async Task Clearing_metadata_drops_it_from_search_and_the_list()
    {
        await _store.ClearMetadataAsync([EntryOf(Tomb)], Ct);

        Assert.Empty(await FindAsync("horrors"));
        Assert.Equal([Tomb], await FindAsync("tomb_final"));
        Assert.Equal("tomb_final", (await _library.ListAsync(new LibraryFilter(), ct: Ct)).Single(e => e.DocumentId == Tomb).Title);
    }

    [Fact]
    public async Task Probing_again_keeps_the_projected_metadata_in_search()
    {
        await _store.UpsertDocumentAsync(new DocRow { DocumentId = Tomb, ContentHash = "h1", Format = "pdf", DisplayTitle = "tomb_final" }, [], [], Ct);

        Assert.Equal([Tomb], await FindAsync("horrors"));
    }
}

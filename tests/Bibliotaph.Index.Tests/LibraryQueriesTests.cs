using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Dapper;

namespace Bibliotaph.Index.Tests;

/// <summary>The library list and both search tabs over a small made-up library.</summary>
public sealed class LibraryQueriesTests : IndexFixture
{
    const long Gazetteer = 1, Lairs = 2, TavernMap = 3, HauntedInn = 4;

    LibraryQueries _library = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _library = new LibraryQueries(Database);
        var store = new IndexStore(Writer, Clock);

        await AddAsync(store, Gazetteer, "Gazetteer of the Marches", "pdf", "Setting / Maps",
            "Contents", "The ancient red dragon sleeps beneath the mill.", "Goblins raid the tavern at midnight.", "The dragon wakes.");
        await AddAsync(store, Lairs, "Dragon Lairs Compendium", "pdf", "Monsters",
            "A lich keeps a tavern ledger.", "Red dragon lair maps.");
        await AddAsync(store, TavernMap, "Tavern Map", "png", "Handouts");
        await AddAsync(store, HauntedInn, "Haunted Inn", "pdf", "Adventures / One Shots",
            "The inn is haunted; a secret door hides behind the bar.", "");
        await store.SetOcrPageAsync(HauntedInn, 1, "Secret passages run under the inn.", 0.9, [], Ct);
        Connection.Execute(
            "INSERT INTO stage_status (document_id, stage, status, stage_version, updated_utc) VALUES (@id, 'Text', 'Complete', 1, '2026-10-08T12:00:00Z')",
            new { id = Gazetteer });
    }

    async Task AddAsync(IndexStore store, long id, string title, string format, string folder, params string[] pages)
    {
        Clock.Advance(TimeSpan.FromHours(1));
        await store.UpsertDocumentAsync(
            new DocRow { DocumentId = id, ContentHash = $"hash{id}", Format = format, DisplayTitle = title, PageCount = pages.Length, FolderHint = folder },
            [.. pages.Select((_, i) => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], [], Ct);
        await store.SetPageTextAsync(id, [.. pages.Select((text, i) => new PageTextRow(i, text, "pdf", 1, false))], Ct);
    }

    static SearchPlan Plan(string query) => SearchPlan.From(SearchQuery.Parse(query));

    async Task<long[]> DocumentsAsync(string query, LibraryFilter? filter = null) =>
        [.. (await _library.SearchDocumentsAsync(Plan(query), filter ?? new LibraryFilter(), ct: Ct)).Select(e => e.DocumentId)];

    async Task<(long Doc, int Page)[]> PagesAsync(string query, LibraryFilter? filter = null) =>
        [.. (await _library.SearchPagesAsync(Plan(query), filter ?? new LibraryFilter(), ct: Ct))
            .Documents.SelectMany(d => d.Pages.Select(p => (d.Document.DocumentId, p.PdfPage)))];

    [Fact]
    public async Task The_library_lists_newest_first_or_by_title()
    {
        Assert.Equal([HauntedInn, TavernMap, Lairs, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded), ct: Ct)).Select(e => e.DocumentId));
        Assert.Equal(["Dragon Lairs Compendium", "Gazetteer of the Marches", "Haunted Inn", "Tavern Map"],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).Select(e => e.Title));
    }

    [Fact]
    public async Task The_library_filters_by_format_and_scope()
    {
        Assert.Equal([TavernMap], (await _library.ListAsync(new LibraryFilter(Format: FormatFilter.Images), ct: Ct)).Select(e => e.DocumentId));
        Assert.DoesNotContain(TavernMap, (await _library.ListAsync(new LibraryFilter(Format: FormatFilter.Pdf), ct: Ct)).Select(e => e.DocumentId));
        Assert.Equal([Lairs, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Scope: [Gazetteer, Lairs, 99]), ct: Ct)).Select(e => e.DocumentId));
    }

    [Fact]
    public async Task Entries_say_whether_their_text_is_searchable_yet()
    {
        var entries = (await _library.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.DocumentId);

        Assert.True(entries[Gazetteer].Searchable);
        Assert.False(entries[Lairs].Searchable);
        Assert.Equal(new DateTime(2026, 10, 8, 13, 0, 0, DateTimeKind.Utc), entries[Gazetteer].AddedUtc);
    }

    [Theory]
    [InlineData("dragon", new[] { Lairs })]
    [InlineData("tavern", new[] { TavernMap })]
    [InlineData("gazet*", new[] { Gazetteer })]
    [InlineData("\"dragon lairs\"", new[] { Lairs })]
    [InlineData("haunted OR gazetteer", new[] { HauntedInn, Gazetteer })]
    [InlineData("title:map*", new[] { TavernMap })]
    [InlineData("maps", new[] { Gazetteer })] // the folder name is searchable metadata
    public async Task The_documents_tab_matches_titles_and_metadata(string query, long[] expected) =>
        Assert.Equal(expected.Order(), (await DocumentsAsync(query)).Order());

    [Fact]
    public async Task The_documents_tab_puts_title_matches_first()
    {
        // "maps" is in Gazetteer's folder (provisional metadata) and in Tavern Map's title (map* prefix).
        var found = await DocumentsAsync("map*");

        Assert.Equal([TavernMap, Gazetteer], found);
    }

    [Theory]
    [InlineData("format:png", new[] { TavernMap })]
    [InlineData("format:image", new[] { TavernMap })]
    [InlineData("-format:pdf", new[] { TavernMap })]
    [InlineData("folder:maps", new[] { Gazetteer })]
    [InlineData("folder:\"one shots\"", new[] { HauntedInn })]
    [InlineData("-folder:maps -folder:monsters", new[] { TavernMap, HauntedInn })]
    [InlineData("-dragon", new[] { Gazetteer, TavernMap, HauntedInn })]
    [InlineData("-title:dragon -title:tavern", new[] { Gazetteer, HauntedInn })]
    [InlineData("format:pdf -haunted", new[] { Gazetteer, Lairs })]
    [InlineData("folder:100%", new long[0])]
    public async Task Fields_and_exclusions_filter_without_words(string query, long[] expected) =>
        Assert.Equal(expected.Order(), (await DocumentsAsync(query)).Order());

    [Fact]
    public async Task Pages_are_grouped_by_book_with_counts_and_snippets()
    {
        var results = await _library.SearchPagesAsync(Plan("dragon"), new LibraryFilter(), pagesPerDocument: 1, ct: Ct);

        Assert.Equal(2, results.MatchingDocuments);
        Assert.Equal(3, results.MatchingPages);
        var gazetteer = results.Documents.Single(d => d.Document.DocumentId == Gazetteer);
        Assert.Equal(2, gazetteer.MatchingPages);
        var hit = Assert.Single(gazetteer.Pages);
        Assert.Contains($"{LibraryQueries.HitStart}dragon{LibraryQueries.HitEnd}", hit.Snippet, StringComparison.Ordinal);
        Assert.Equal(hit.PdfPage + 1, int.Parse(hit.Label!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("dragon", new[] { "1:1", "1:3", "2:1" })]
    [InlineData("\"red dragon\"", new[] { "1:1", "2:1" })]
    [InlineData("\"red dragon\" -lair", new[] { "1:1" })]
    [InlineData("lich OR goblins", new[] { "1:2", "2:0" })]
    [InlineData("gob*", new[] { "1:2" })]
    [InlineData("title:lairs dragon", new[] { "2:1" })]
    [InlineData("dragon -title:lairs", new[] { "1:1", "1:3" })]
    [InlineData("tavern folder:monsters", new[] { "2:0" })]
    [InlineData("passages", new[] { "4:1" })]
    [InlineData("(-ghost -spirit) OR goblins", new[] { "1:2" })]
    public async Task The_inside_documents_tab_finds_pages(string query, string[] expected) =>
        Assert.Equal(expected.Order(), (await PagesAsync(query)).Select(p => $"{p.Doc}:{p.Page}").Order());

    [Theory]
    [InlineData("-dragon")]
    [InlineData("format:pdf")]
    [InlineData("title:dragon")]
    [InlineData("")]
    public async Task The_inside_documents_tab_needs_words_to_look_for(string query) =>
        Assert.Same(PageResults.None, await _library.SearchPagesAsync(Plan(query), new LibraryFilter(), ct: Ct));

    [Fact]
    public async Task Pages_from_ocr_say_so()
    {
        var hit = (await _library.SearchPagesAsync(Plan("inn"), new LibraryFilter(), ct: Ct)).Documents.Single().Pages;

        Assert.Equal([false, true], hit.OrderBy(p => p.PdfPage).Select(p => p.FromOcr));
    }

    [Fact]
    public async Task Scope_and_format_apply_to_page_search_too()
    {
        var scoped = await PagesAsync("dragon", new LibraryFilter(Scope: [Lairs]));

        Assert.Equal([(Lairs, 1)], scoped);
        Assert.Empty(await PagesAsync("dragon", new LibraryFilter(Format: FormatFilter.Images)));
    }

    [Theory]
    [InlineData("NEAR(dragon lich)")]
    [InlineData("text:dragon")]
    [InlineData("{title}:dragon")]
    [InlineData("dragon AND")]
    [InlineData("dragon NOT")]
    [InlineData("\"dragon")]
    [InlineData("dragon\"\"lich")]
    [InlineData("^dragon")]
    [InlineData("'; DROP TABLE doc; --")]
    [InlineData("folder:'%_\\")]
    [InlineData("*dragon")]
    public async Task Typed_syntax_never_reaches_sqlite_as_syntax(string query)
    {
        await _library.SearchDocumentsAsync(Plan(query), new LibraryFilter(), ct: Ct);
        await _library.SearchPagesAsync(Plan(query), new LibraryFilter(), ct: Ct);

        Assert.Equal(4, Connection.ExecuteScalar<long>("SELECT count(*) FROM doc"));
    }

    [Fact]
    public async Task Details_cover_ocr_and_stages()
    {
        var details = await _library.GetDetailsAsync(HauntedInn, Ct);

        Assert.NotNull(details);
        Assert.Equal("Haunted Inn", details.Entry.Title);
        Assert.Equal(1, details.OcrPages);
        Assert.Empty(details.Stages);
        Assert.Equal([new StageState(Stage.Text, StageStatus.Complete, null)], (await _library.GetDetailsAsync(Gazetteer, Ct))!.Stages);
        Assert.Null(await _library.GetDetailsAsync(42, Ct));
    }

    [Fact]
    public async Task Ocr_words_come_back_in_reading_order_for_scanned_pages_only()
    {
        await new IndexStore(Writer, Clock).SetOcrPageAsync(HauntedInn, 1, "Secret passages",
            0.9, [new OcrWordRow("Secret", 72, 700, 120, 688), new OcrWordRow("passages", 124, 700, 190, 688)], Ct);

        var words = await _library.GetOcrWordsAsync(HauntedInn, 1, Ct);

        Assert.Equal(["Secret", "passages"], words.Select(w => w.Text));
        Assert.Equal(new OcrWordRow("Secret", 72, 700, 120, 688), words[0]);
        Assert.Empty(await _library.GetOcrWordsAsync(HauntedInn, 0, Ct));
    }
}

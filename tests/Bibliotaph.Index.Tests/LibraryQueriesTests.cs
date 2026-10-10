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
        await AddEntriesAsync(store, id);
    }

    static SearchPlan Plan(string query) => SearchPlan.From(SearchQuery.Parse(query));

    async Task<long[]> DocumentsAsync(string query, LibraryFilter? filter = null) =>
        [.. (await _library.SearchDocumentsAsync(Plan(query), filter ?? new LibraryFilter(), ct: Ct)).Select(e => e.DocumentId)];

    async Task<(long Doc, int Page)[]> PagesAsync(string query, LibraryFilter? filter = null) =>
        [.. (await _library.SearchPagesAsync(Plan(query), filter ?? new LibraryFilter(), ct: Ct))
            .Entries.SelectMany(d => d.Pages.Select(p => (d.Entry.DocumentId, p.PdfPage)))];

    [Fact]
    public async Task The_library_lists_newest_first_or_by_title()
    {
        Assert.Equal([HauntedInn, TavernMap, Lairs, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded), ct: Ct)).Select(e => e.DocumentId));
        Assert.Equal(["Dragon Lairs Compendium", "Gazetteer of the Marches", "Haunted Inn", "Tavern Map"],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).Select(e => e.Title));
    }

    [Fact]
    public async Task The_library_filters_by_kind_and_scope()
    {
        Assert.Equal([TavernMap], (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Images), ct: Ct)).Select(e => e.DocumentId));
        Assert.DoesNotContain(TavernMap, (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Books), ct: Ct)).Select(e => e.DocumentId));
        Assert.Equal([Lairs, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Scope: [EntryOf(Gazetteer), EntryOf(Lairs), EntryOf(99)]), ct: Ct)).Select(e => e.DocumentId));
    }

    [Fact]
    public async Task A_book_owned_elsewhere_is_listed_found_by_title_and_where_it_is_owned_but_never_inside_documents()
    {
        var store = new IndexStore(Writer, Clock);
        var printed = new EntryId(70);
        Clock.Advance(TimeSpan.FromHours(1));
        await store.SetEntriesAsync([new EntryDocRow(printed, null, EntryKind.Elsewhere, Copies: 0) { AddedUtc = Clock.GetUtcNow().UtcDateTime }], Ct);
        await store.SetMetadataAsync([new EntryMetaRow
        {
            EntryId = printed,
            Title = "Red Dragon Atlas",
            Publisher = "Kobold Works",
            Facets = [new("own", "print", "Print", true), new("own", "foundry", "Foundry VTT", true)],
        }], Ct);

        var all = await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded), ct: Ct);
        var card = all[0];
        Assert.Equal((printed, 0L, "Red Dragon Atlas", "", true), (card.EntryId, card.DocumentId, card.Title, card.Format, card.IsElsewhere));
        Assert.Equal("Foundry VTT · Print", card.AlsoOwn);
        Assert.Equal(5, all.Count);
        Assert.Equal([printed], (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Select(e => e.EntryId));
        Assert.DoesNotContain(printed, (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Books), ct: Ct)).Select(e => e.EntryId));

        // The Documents tab finds it by its title and by where it is owned; Inside documents never does.
        Assert.Contains(printed, (await _library.SearchDocumentsAsync(Plan("dragon"), new LibraryFilter(), ct: Ct)).Select(e => e.EntryId));
        Assert.DoesNotContain(printed, (await _library.SearchPagesAsync(Plan("dragon"), new LibraryFilter(), ct: Ct)).Entries.Select(e => e.Entry.EntryId));
        Assert.Equal([printed], (await _library.ListAsync(new LibraryFilter(), Plan("own:foundry"), ct: Ct)).Select(e => e.EntryId));
        Assert.Equal([printed], (await _library.ListAsync(new LibraryFilter(Owns: ["print"]), ct: Ct)).Select(e => e.EntryId));
        Assert.Contains(printed, (await _library.ListAsync(new LibraryFilter(), Plan("-format:png"), ct: Ct)).Select(e => e.EntryId));
        Assert.DoesNotContain(printed, (await _library.ListAsync(new LibraryFilter(), Plan("format:pdf"), ct: Ct)).Select(e => e.EntryId));
        Assert.Equal([("foundry", 1L), ("print", 1L), (SearchQuery.Unknown, 4L)],
            (await _library.GetFacetCountsAsync("own", new LibraryFilter(), ct: Ct)).Select(c => (c.Value, c.Count)).Order());
        Assert.DoesNotContain(await _library.GetFormatCountsAsync(new LibraryFilter(), Ct), c => c.Value.Length == 0);

        var details = await _library.GetDetailsAsync(printed, Ct);
        Assert.NotNull(details);
        Assert.Empty(details.Stages);
    }

    [Fact]
    public async Task A_pack_shows_its_name_its_image_count_and_a_mosaic_and_has_a_kind_of_its_own()
    {
        var store = new IndexStore(Writer, Clock);
        List<long> images = [10, 11, 12, 13, 14];
        foreach (var id in images)
        {
            await AddAsync(store, id, $"Zombie {id}", "png", "Tokens / Undead");
            if (id != 11) await store.SetCoverAsync(id, $"cover{id}.webp", Ct);
        }
        await store.RemoveEntriesAsync([.. images.Select(EntryOf)], Ct);
        var pack = new EntryId(50);
        await store.SetEntriesAsync([new EntryDocRow(pack, 10, EntryKind.Pack)
        {
            Name = "Undead",
            Members = [.. images.Select(id => new EntryMemberRow(id, EntryOf(id), $"Zombie {id}.png"))],
        }], Ct);

        var card = Assert.Single(await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Packs), ct: Ct));
        Assert.Equal((pack, "Undead", true, 5), (card.EntryId, card.Title, card.IsPack, card.Members));
        // The first four images with a cover, in order.
        Assert.Equal(["cover10.webp", "cover12.webp", "cover13.webp", "cover14.webp"], card.MosaicCovers);
        Assert.Equal([TavernMap], (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Images), ct: Ct)).Select(e => e.DocumentId));
        Assert.DoesNotContain(pack, (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Books), ct: Ct)).Select(e => e.EntryId));
        // Found by its name, not by its first image's.
        Assert.Equal(10L, Assert.Single(await DocumentsAsync("undead")));
        Assert.Empty(await DocumentsAsync("title:zombie"));
        Assert.Equal(["Zombie 10.png", "Zombie 11.png", "Zombie 12.png", "Zombie 13.png", "Zombie 14.png"],
            (await _library.GetPackImagesAsync(pack, Ct)).Select(i => i.Name));
        var second = (await _library.GetPackImagesAsync(pack, Ct))[1];
        Assert.Equal((11L, EntryOf(11), (string?)null), (second.DocumentId, second.MemberEntryId, second.Cover));

        await store.RemoveEntriesAsync([pack], Ct);
        Assert.Empty(await _library.GetPackImagesAsync(pack, Ct));
    }

    [Fact]
    public async Task A_pack_is_found_by_its_images_names_and_says_which_one_matched()
    {
        var store = new IndexStore(Writer, Clock);
        (long Id, string Name)[] images = [(10, "Kraken"), (11, "Sea Serpent"), (12, "Krakenling Swarm")];
        foreach (var (id, name) in images) await AddAsync(store, id, name, "png", "Tokens / Sea");
        await store.RemoveEntriesAsync([.. images.Select(i => EntryOf(i.Id))], Ct);
        var pack = new EntryId(50);
        await store.SetEntriesAsync([new EntryDocRow(pack, 10, EntryKind.Pack)
        {
            Name = "Sea",
            Members = [.. images.Select(i => new EntryMemberRow(i.Id, EntryOf(i.Id), $"{i.Name}.png"))],
        }], Ct);

        // The pack is the result, with the image that matched best; opening it opens that image.
        var hit = Assert.Single(await _library.SearchDocumentsAsync(Plan("serpent"), new LibraryFilter(), ct: Ct));
        Assert.Equal((pack, "Sea Serpent.png", 11L), (hit.EntryId, hit.MatchedMember, hit.MatchedDocumentId));
        Assert.Equal("Kraken.png", Assert.Single(await _library.SearchDocumentsAsync(Plan("\"kraken\""), new LibraryFilter(), ct: Ct)).MatchedMember);
        // Found by its own name, it shows no image in particular; title: is about the card, not its images.
        Assert.Null(Assert.Single(await _library.SearchDocumentsAsync(Plan("sea -serpent"), new LibraryFilter(), ct: Ct)).MatchedMember);
        Assert.Empty(await DocumentsAsync("title:kraken"));
        // The extension isn't a word to find, and the filter panel counts the pack among the results.
        Assert.Empty(await DocumentsAsync("png"));
        Assert.Contains(new FacetCount(SearchQuery.Unknown, "Unknown", 1), await _library.GetFacetCountsAsync("type", new LibraryFilter(), Plan("serpent"), Ct));

        // Split: the images are their own cards again, found by their own titles.
        await store.RemoveEntriesAsync([pack], Ct);
        await AddEntriesAsync(store, 11);
        hit = Assert.Single(await _library.SearchDocumentsAsync(Plan("serpent"), new LibraryFilter(), ct: Ct));
        Assert.Equal((EntryOf(11), null), (hit.EntryId, hit.MatchedMember));
    }

    [Fact]
    public async Task WebP_images_are_images()
    {
        await AddAsync(new IndexStore(Writer, Clock), 20, "Harbor", "webp", "Maps");

        Assert.Equal([20, TavernMap], (await _library.ListAsync(new LibraryFilter(Kind: KindFilter.Images), ct: Ct)).Select(e => e.DocumentId));
        var images = await DocumentsAsync("format:image");
        Assert.Equal([20, TavernMap], images);
        Assert.Equal(20, Assert.Single(await DocumentsAsync("format:webp")));
    }

    [Fact]
    public async Task The_library_marks_and_filters_books_a_model_has_read()
    {
        var store = new IndexStore(Writer, Clock);
        await store.SetAiReadAsync(null, new Dictionary<EntryId, string> { [EntryOf(Gazetteer)] = "model-a", [EntryOf(Lairs)] = "model-b" }, Ct);
        // Reprojecting two entries: the Lairs mark goes, a new one for the inn comes, the Gazetteer's is untouched.
        await store.SetAiReadAsync([EntryOf(Lairs), EntryOf(HauntedInn)], new Dictionary<EntryId, string> { [EntryOf(HauntedInn)] = "model-b", [EntryOf(TavernMap)] = "ignored" }, Ct);

        var entries = (await _library.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.DocumentId, e => e.AiModel);
        Assert.Equal("model-a", entries[Gazetteer]);
        Assert.Equal("model-b", entries[HauntedInn]);
        Assert.Null(entries[Lairs]);
        Assert.Null(entries[TavernMap]);
        Assert.Equal(2, await _library.CountAiReadAsync(Ct));
        Assert.Equal([HauntedInn, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded, Ai: AiFilter.Read), ct: Ct)).Select(e => e.DocumentId));
        Assert.Equal([TavernMap, Lairs],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded, Ai: AiFilter.NotRead), ct: Ct)).Select(e => e.DocumentId));
        Assert.Empty(await DocumentsAsync("dragon", new LibraryFilter(Ai: AiFilter.Read)));
        var notRead = await DocumentsAsync("dragon", new LibraryFilter(Ai: AiFilter.NotRead));
        Assert.Equal([Lairs], notRead);
        Assert.All(await PagesAsync("dragon", new LibraryFilter(Ai: AiFilter.Read)), hit => Assert.Equal(Gazetteer, hit.Doc));
    }

    [Fact]
    public async Task Favorites_are_a_group_the_library_can_show_on_its_own_and_search_by()
    {
        var store = new IndexStore(Writer, Clock);
        await store.SetScopesAsync(null, [(EntryOf(Gazetteer), ScopeKeys.Favorites), (EntryOf(Lairs), ScopeKeys.Favorites)], Ct);
        // Reprojecting two entries: the Lairs heart goes, the inn's comes, the Gazetteer's is untouched.
        await store.SetScopesAsync([EntryOf(Lairs), EntryOf(HauntedInn)], [(EntryOf(HauntedInn), ScopeKeys.Favorites), (EntryOf(TavernMap), ScopeKeys.Favorites)], Ct);

        var favorites = (await _library.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.DocumentId, e => e.Favorite);
        Assert.Equal((true, true, false, false), (favorites[Gazetteer], favorites[HauntedInn], favorites[Lairs], favorites[TavernMap]));
        Assert.Equal([HauntedInn, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyAdded, Group: ScopeKeys.Favorites), ct: Ct)).Select(e => e.DocumentId));
        var inFavorites = await DocumentsAsync("marches", new LibraryFilter(Group: ScopeKeys.Favorites));
        Assert.Equal([Gazetteer], inFavorites);
        Assert.Empty(await DocumentsAsync("dragon", new LibraryFilter(Group: ScopeKeys.Favorites)));
        var pages = await PagesAsync("dragon", new LibraryFilter(Group: ScopeKeys.Favorites));
        Assert.NotEmpty(pages);
        Assert.All(pages, hit => Assert.Equal(Gazetteer, hit.Doc));

        // favorite:yes and favorite:no, alone or with words.
        Assert.Equal([Gazetteer, HauntedInn], (await DocumentsAsync("favorite:yes")).Order());
        Assert.Equal([Lairs, TavernMap], (await DocumentsAsync("fav:no")).Order());
        var dragons = await DocumentsAsync("dragon -favorite:yes");
        Assert.Equal([Lairs], dragons);

        await store.RemoveEntriesAsync([EntryOf(Gazetteer)], Ct);
        Assert.Equal(1, Connection.ExecuteScalar<long>("SELECT count(*) FROM entry_scope"));
    }

    [Fact]
    public async Task Recently_opened_puts_the_last_opened_first_and_can_keep_only_opened_books()
    {
        var store = new IndexStore(Writer, Clock);
        var monday = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        await store.SetOpenedAsync(null, new Dictionary<EntryId, DateTime> { [EntryOf(Gazetteer)] = monday, [EntryOf(TavernMap)] = monday.AddDays(1) }, Ct);

        var all = await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyOpened), ct: Ct);
        // Then those never opened, newest first.
        Assert.Equal([TavernMap, Gazetteer, HauntedInn, Lairs], all.Select(e => e.DocumentId));
        Assert.Equal(monday.AddDays(1), all[0].OpenedUtc);
        Assert.Equal(DateTimeKind.Utc, all[0].OpenedUtc!.Value.Kind);
        Assert.Null(all[2].OpenedUtc);
        Assert.Equal([TavernMap],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), limit: 1, ct: Ct)).Select(e => e.DocumentId));

        await store.SetOpenedAsync([EntryOf(Lairs)], new Dictionary<EntryId, DateTime> { [EntryOf(Lairs)] = monday.AddDays(2) }, Ct);
        Assert.Equal([Lairs, TavernMap, Gazetteer],
            (await _library.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), ct: Ct)).Select(e => e.DocumentId));
    }

    [Fact]
    public async Task Cards_count_their_copies_and_the_copies_filter_keeps_books_with_more_than_one()
    {
        await new IndexStore(Writer, Clock).SetEntriesAsync([new EntryDocRow(EntryOf(Lairs), Lairs, EntryKind.Whole, Copies: 2)], Ct);

        var entries = (await _library.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.DocumentId, e => e.Copies);
        Assert.Equal((2, 1), (entries[Lairs], entries[Gazetteer]));
        Assert.Equal([Lairs], (await _library.ListAsync(new LibraryFilter(OnlyWithCopies: true), ct: Ct)).Select(e => e.DocumentId));
        var dragons = await DocumentsAsync("dragon", new LibraryFilter(OnlyWithCopies: true));
        Assert.Equal([Lairs], dragons);
        Assert.Equal(1, await _library.CountWithCopiesAsync(Ct));
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
    [InlineData("drag", new[] { Lairs })] // a word finds words that start with it
    [InlineData("title:haun", new[] { HauntedInn })]
    [InlineData("\"dragon lai\"", new long[0])] // phrases stay exact
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

        Assert.Equal(2, results.MatchingEntries);
        Assert.Equal(3, results.MatchingPages);
        var gazetteer = results.Entries.Single(d => d.Entry.DocumentId == Gazetteer);
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
    [InlineData("gob", new[] { "1:2" })]
    [InlineData("re", new string[0])] // too short to stand for red, ruins or the rest
    [InlineData("pass -drag", new[] { "4:1" })]
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
        var hit = (await _library.SearchPagesAsync(Plan("inn"), new LibraryFilter(), ct: Ct)).Entries.Single().Pages;

        Assert.Equal([false, true], hit.OrderBy(p => p.PdfPage).Select(p => p.FromOcr));
    }

    [Fact]
    public async Task Scope_and_format_apply_to_page_search_too()
    {
        var scoped = await PagesAsync("dragon", new LibraryFilter(Scope: [EntryOf(Lairs)]));

        Assert.Equal([(Lairs, 1)], scoped);
        Assert.Empty(await PagesAsync("dragon", new LibraryFilter(Kind: KindFilter.Images)));
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
        var details = await _library.GetDetailsAsync(EntryOf(HauntedInn), Ct);

        Assert.NotNull(details);
        Assert.Equal("Haunted Inn", details.Entry.Title);
        Assert.Equal(1, details.OcrPages);
        Assert.Empty(details.Stages);
        Assert.Equal([new StageState(Stage.Text, StageStatus.Complete, null)], (await _library.GetDetailsAsync(EntryOf(Gazetteer), Ct))!.Stages);
        Assert.Null(await _library.GetDetailsAsync(EntryOf(42), Ct));
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

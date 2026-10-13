using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Dapper;

namespace Bibliotaph.Index.Tests;

/// <summary>
/// What a question to the whole library reads from index.db: pages in scope by words and by vector, counts, titles, the
/// next book to read for meaning, and the scopes filters make.
/// </summary>
public sealed class LibraryAskQueriesTests : IndexFixture
{
    IndexStore _store = null!;
    LibraryAskQueries _ask = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _store = new IndexStore(Writer, Clock);
        _ask = new LibraryAskQueries(Database);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task AddBookAsync(long id, params string[] texts)
    {
        await _store.UpsertDocumentAsync(new DocRow { DocumentId = id, ContentHash = $"hash{id}", Format = "pdf", DisplayTitle = $"Invented Book {id}", PageCount = texts.Length },
            [.. texts.Select((_, i) => new PageRow(i, null, 612, 792))], [], Ct);
        await _store.SetPageTextAsync(id, [.. texts.Select((t, i) => new PageTextRow(i, t, "pdf", 1, t.Length == 0))], Ct);
    }

    long PageId(long documentId, int pdfPage) =>
        Connection.ExecuteScalar<long>("SELECT id FROM page WHERE document_id = @documentId AND pdf_page = @pdfPage", new { documentId, pdfPage });

    [Fact]
    public void A_scope_is_the_pages_every_filter_allows_a_whole_book_taking_in_its_ranges()
    {
        var books = PageScope.Of([new PageSpan(1), new PageSpan(2), new PageSpan(3, 10, 20)]);
        var chapters = PageScope.Of([new PageSpan(1, 4, 6), new PageSpan(3, 15, 30), new PageSpan(4)]);

        var both = books.Intersect(chapters);

        Assert.True(both.Contains(1, 5));
        Assert.False(both.Contains(1, 7));
        Assert.False(both.Contains(2, 0));
        Assert.True(both.Contains(3, 20));
        Assert.False(both.Contains(3, 21));
        Assert.False(both.Contains(4, 0));
        Assert.True(PageScope.Everything.Intersect(books).Contains(2, 99));
        Assert.True(PageScope.Of([new PageSpan(5, 1, 2), new PageSpan(5)]).Contains(5, 40));
        Assert.False(books.Without([2]).Contains(2, 0));
        Assert.False(books.Without([2]).Intersect(PageScope.Everything).Contains(2, 0));
    }

    [Fact]
    public async Task Words_find_pages_across_the_books_cards_show_within_the_scope()
    {
        await AddBookAsync(1, "The lich waits.", "Rules for spells.");
        await AddBookAsync(2, "Another lich, in another book.", "A lich and its lich lair.");
        await AddBookAsync(3, "A lich in a copy nobody's card shows.");
        await AddEntriesAsync(_store, 1, 2);

        var everywhere = await _ask.FindWordsAsync("\"lich\"*", PageScope.Everything, 10, Ct);
        Assert.Equal([PageId(1, 0), PageId(2, 0), PageId(2, 1)], everywhere.Select(h => h.PageId).Order());
        Assert.Equal(PageId(2, 1), everywhere[0].PageId);
        Assert.DoesNotContain(everywhere, h => h.DocumentId == 3);

        var second = await _ask.FindWordsAsync("\"lich\"*", PageScope.Of([new PageSpan(2, 1, 1)]), 10, Ct);
        Assert.Equal([(2L, 1)], second.Select(h => (h.DocumentId, h.PdfPage)));
        Assert.Equal([3, 1], await _ask.CountPagesAsync(["\"lich\"*", "\"spell\"*"], PageScope.Everything, Ct));
        Assert.Equal([2, 0], await _ask.CountPagesAsync(["\"lich\"*", "\"spell\"*"], PageScope.Everything.Without([1]), Ct));
    }

    [Fact]
    public async Task Vectors_are_read_in_scope_and_coverage_and_the_next_book_to_read_follow_them()
    {
        await AddBookAsync(1, "One.", "Two.", "");
        await AddBookAsync(2, "Three.");
        await AddBookAsync(3, "Four.");
        await AddEntriesAsync(_store, 1, 2, 3);
        await _store.SaveVectorsAsync("embed", 1, [(PageId(1, 0), [1f, 0f]), (PageId(2, 0), [0f, 1f])], Ct);
        await _store.SaveVectorsAsync("other", 1, [(PageId(3, 0), [1f, 1f])], Ct);

        var vectors = new List<LibraryPageVector>();
        await foreach (var vector in _ask.ReadVectorsAsync("embed", 1, PageScope.Of([new PageSpan(1)]), Ct)) vectors.Add(vector);
        Assert.Equal([(1L, 0)], vectors.Select(v => (v.DocumentId, v.PdfPage)));
        Assert.Equal([1f, 0f], vectors[0].Vector);

        Assert.Equal(new LibraryCoverage(3, 4, 2), await _ask.GetCoverageAsync("embed", 1, PageScope.Everything, Ct));
        Assert.Equal(1L, await _ask.NextWithoutVectorsAsync("embed", 1, 0, [], Ct));
        Assert.Equal(3L, await _ask.NextWithoutVectorsAsync("embed", 1, 1, [], Ct));
        Assert.Null(await _ask.NextWithoutVectorsAsync("embed", 1, 1, [3], Ct));
        Assert.Equal((4, 2), await _ask.GetVectorProgressAsync("embed", 1, [], Ct));
        Assert.Equal((3, 2), await _ask.GetVectorProgressAsync("embed", 1, [3], Ct));
    }

    [Fact]
    public async Task A_book_is_cited_by_its_cards_title_and_the_library_filters_give_its_pages()
    {
        await AddBookAsync(1, "One.", "Two.", "Three.");
        await AddBookAsync(2, "Four.");
        await _store.SetEntriesAsync(
        [
            new EntryDocRow(EntryOf(1), 1, EntryKind.Whole),
            new EntryDocRow(new EntryId(500), 1, EntryKind.Part) { ParentEntryId = EntryOf(1), Pages = new PartPages(1, 2) },
            new EntryDocRow(EntryOf(2), 2, EntryKind.Whole),
        ], Ct);
        await _store.SetMetadataAsync(
        [
            new EntryMetaRow { EntryId = EntryOf(1), Title = "The Invented Compilation", Facets = [new FacetRow("system", "dnd5e", "D&D 5e", true)] },
            new EntryMetaRow { EntryId = new EntryId(500), Title = "The Invented Adventure", Facets = [new FacetRow("system", "dnd5e", "D&D 5e", true)] },
            new EntryMetaRow { EntryId = EntryOf(2), Title = "Another Invented Book", Facets = [new FacetRow("system", "pf2e", "Pathfinder 2e", true)] },
        ], Ct);
        var library = new LibraryQueries(Database);

        Assert.Equal("The Invented Compilation", (await _ask.GetTitlesAsync([1, 2], Ct))[1]);

        var dnd = PageScope.Of(await library.GetPageSpansAsync(new LibraryFilter(Systems: ["dnd5e"]), new SearchPlan(), Ct));
        Assert.True(dnd.Contains(1, 0));
        Assert.False(dnd.Contains(2, 0));

        // Adventures alone: only the adventure's pages of the file.
        var parts = PageScope.Of(await library.GetPageSpansAsync(new LibraryFilter(), SearchPlan.From(SearchQuery.Parse("title:adventure")), Ct));
        Assert.True(parts.Contains(1, 2));
        Assert.False(parts.Contains(1, 0));
        Assert.False(parts.Contains(2, 0));
    }
}

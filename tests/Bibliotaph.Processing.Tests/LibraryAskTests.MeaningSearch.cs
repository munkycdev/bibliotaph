using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// The search box's about:, which ranks the library's pages as a question to the library does, without an answer: by
/// its words and, once pages have vectors, by meaning; within the fields and filters; after the plain words.
/// </summary>
public sealed partial class LibraryAskTests
{
    Task<MeaningResults> SearchAsync(string text, LibraryFilter? filter = null)
    {
        var query = SearchQuery.Parse(text);
        return _search.SearchAsync(query, SearchPlan.From(query), filter ?? new LibraryFilter(), Ct);
    }

    async Task ReadLibraryForMeaningAsync()
    {
        _clock.Advance(LibraryVectors.Quiet);
        for (var i = 0; i < 10 && await _vectors.FillNextAsync(Ct) != LibraryVectorsStep.Done; i++) { }
    }

    static IEnumerable<(long Document, int Page)> Pages(MeaningResults results) =>
        results.Pages.Entries.SelectMany(e => e.Pages.Select(p => (e.Entry.DocumentId, p.PdfPage)));

    [Fact]
    public async Task About_finds_pages_by_meaning_once_they_have_vectors_and_by_its_words_until_then()
    {
        await SetUpAsync("embed-a");

        var before = await SearchAsync("about:\"phylactery pearl\"");
        Assert.Equal(AskMeaning.Partial, before.Meaning);
        Assert.Contains("none has been read for meaning yet", before.Note, StringComparison.Ordinal);
        Assert.Contains((_city, 0), Pages(before));
        Assert.Empty(_server.Embedded);

        await ReadLibraryForMeaningAsync();
        var after = await SearchAsync("about:\"phylactery pearl\"");

        // The book in a folder kept from AI has no vectors, so only its words find it; the search still shows it.
        Assert.Equal(AskMeaning.Partial, after.Meaning);
        Assert.Equal((_city, 0), Pages(after).First());
        Assert.Contains(after.Pages.Entries, e => e.Entry.DocumentId == _secret);
        Assert.Equal(after.Pages.Entries.Select(e => e.Entry.CardId), after.Documents.Select(d => d.CardId));
        Assert.Contains($"{LibraryQueries.HitStart}pearl{LibraryQueries.HitEnd}", after.Pages.Entries[0].Pages[0].Snippet, StringComparison.Ordinal);
        Assert.Equal("phylactery pearl", Assert.Single(_server.Embedded.Last()));

        // Changing tab or order asks the same again, and the model isn't asked twice.
        var embedded = _server.Embedded.Count;
        await SearchAsync("about:\"phylactery pearl\"", new LibraryFilter(Sort: LibrarySort.Title));
        Assert.Equal(embedded, _server.Embedded.Count);
    }

    [Fact]
    public async Task Plain_words_still_have_to_be_on_the_page_and_fields_narrow_the_books()
    {
        await SetUpAsync("embed-a");
        await ReadLibraryForMeaningAsync();

        Assert.Equal([(_city, 1)], Pages(await SearchAsync("harbour about:lich")));
        Assert.All(Pages(await SearchAsync("system:pf2e about:\"lich crypt\"")), p => Assert.Equal(_city, p.Document));
        Assert.Empty(Pages(await SearchAsync("dragonborn about:lich")));
    }

    [Fact]
    public async Task Without_an_embedding_model_about_finds_pages_with_any_of_its_words_and_says_so()
    {
        await SetUpAsync(null);

        var results = await SearchAsync("about:\"sunken pearl\"");

        Assert.Equal(AskMeaning.Off, results.Meaning);
        Assert.Equal([(_city, 0)], Pages(results));
        Assert.Contains("embedding model", results.Note, StringComparison.Ordinal);
        Assert.Empty(_server.Embedded);
    }
}

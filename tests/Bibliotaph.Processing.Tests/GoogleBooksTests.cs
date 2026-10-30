using System.Net;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Asking Google Books about a book's ISBNs, with a stand-in for its server, and asking it after Open Library.</summary>
public sealed class GoogleBooksTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Server(HttpStatusCode status, Func<string, string> body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body(request.RequestUri!.Query)) });
        }
    }

    // Made up: no real book's record. The second volume is a loose match that doesn't list the ISBN asked about.
    const string Answer = """
        {
          "items": [
            { "id": "zzLoose", "volumeInfo": { "title": "Some Other Book", "industryIdentifiers": [{ "type": "ISBN_13", "identifier": "9780306406157" }] } },
            { "id": "abc123", "volumeInfo": {
                "title": "The Owlbear Mill", "authors": ["A. Example", "B. Example"], "publisher": "Example Press", "publishedDate": "2019-03",
                "industryIdentifiers": [{ "type": "ISBN_10", "identifier": "1234567897" }, { "type": "ISBN_13", "identifier": "9781234567897" }] } }
          ]
        }
        """;

    static GoogleBooksCatalogue Catalogue(Server server) => new(GoogleBooksCatalogue.CreateClient(server), "test-key");

    [Fact]
    public async Task Each_isbn_is_asked_about_in_turn_with_the_key_in_a_header_and_only_a_volume_listing_it_is_taken()
    {
        var server = new Server(HttpStatusCode.OK, query => query.Contains("9781234567897", StringComparison.Ordinal) ? Answer : "{}");

        var record = await Catalogue(server).FindAsync(["9780555555552", "9781234567897"], Ct);

        Assert.Equal(2, server.Asked.Count);
        Assert.All(server.Asked, a => Assert.DoesNotContain("test-key", a.RequestUri!.AbsoluteUri, StringComparison.Ordinal));
        Assert.All(server.Asked, a => Assert.Equal("test-key", Assert.Single(a.Headers.GetValues("X-Goog-Api-Key"))));
        Assert.StartsWith("https://www.googleapis.com/books/v1/volumes?q=isbn:9780555555552", server.Asked[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(("Google Books", "abc123", "9781234567897", "The Owlbear Mill", "2019-03"),
            (record!.Source, record.Key, record.Isbn, record.Title, record.Published));
        Assert.Equal(["A. Example", "B. Example"], record.Authors);
        Assert.Equal(["Example Press"], record.Publishers);
        Assert.Equal("2019", BookRecords.Year(record.Published));
    }

    [Fact]
    public async Task An_isbn_no_volume_lists_has_no_record() =>
        Assert.Null(await Catalogue(new Server(HttpStatusCode.OK, _ => Answer)).FindAsync(["9780555555552"], Ct));

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "", "busy")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"dailyLimitExceeded"}]}}""", "busy")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"accessNotConfigured"}]}}""", "API key")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"errors":[{"reason":"keyInvalid"}]}}""", "API key")]
    public async Task A_used_up_quota_or_a_bad_key_is_unavailable_not_an_answer(HttpStatusCode status, string body, string said)
    {
        var ex = await Assert.ThrowsAsync<CatalogueUnavailableException>(() => Catalogue(new Server(status, _ => body)).FindAsync(["9781234567897"], Ct));
        Assert.Contains(said, ex.Message, StringComparison.Ordinal);
    }

    sealed class Fixed(string name, BookRecord? record, bool unavailable = false) : IBookCatalogue
    {
        public int Asked { get; private set; }

        public string Name => name;

        public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
        {
            Asked++;
            return unavailable ? throw new CatalogueUnavailableException($"{name} is busy.") : Task.FromResult(record);
        }
    }

    static BookRecord Record(string source) => new(source, "k", "9781234567897", "The Owlbear Mill", [], [], null);

    [Fact]
    public async Task The_preferred_catalogues_record_comes_first_and_the_next_is_asked_too()
    {
        var first = new Fixed("Open Library", Record("Open Library"));
        var second = new Fixed("Google Books", Record("Google Books"));
        var catalogues = new BookCatalogues([first, second]);

        Assert.Equal("Open Library or Google Books", catalogues.Name);
        Assert.Equal("Open Library", (await catalogues.FindAsync(["9781234567897"], Ct))!.Source);
        // Asked anyway, for the fields the first's record lacks.
        Assert.Equal(1, second.Asked);

        Assert.Equal("Google Books", (await new BookCatalogues([new Fixed("Open Library", null), second]).FindAsync(["9781234567897"], Ct))!.Source);
    }

    [Theory]
    [InlineData("No record of its ISBN in Open Library.", "Open Library or Google Books", true)]
    [InlineData("No record of its ISBN in Open Library or Google Books.", "Open Library or Google Books", false)]
    // Found, a book is asked again too once a catalogue preferred to the one that found it can be asked.
    [InlineData("Looked up in Open Library or Google Books.", "RPGGeek or Open Library or Google Books", true)]
    [InlineData("Looked up in RPGGeek or Open Library or Google Books.", "RPGGeek or Open Library or Google Books", false)]
    [InlineData("Open Library's record for its ISBN is of “X”, which isn't printed in it, so it wasn't used. Looked up in Open Library.", "RPGGeek or Open Library", true)]
    // A PC that can't reach its web library asks Open Library alone, and doesn't ask the books its web library didn't know.
    [InlineData("No record of its ISBN in Open Library or Google Books.", "Open Library", false)]
    [InlineData("No record of its product code in Open Library.", "RPGGeek or Open Library", true)]
    [InlineData("It prints no ISBN to look up.", "Open Library or Google Books", false)]
    [InlineData(null, "Open Library or Google Books", false)]
    public void A_book_no_catalogue_knew_is_asked_again_only_once_there_is_one_more_to_ask(string? reason, string catalogues, bool again)
    {
        Assert.Equal(again, BookLookups.AskedOfFewer(reason, catalogues));
        Assert.Equal("No record of its ISBN in Open Library.", BookLookups.NoRecordIn("Open Library"));
    }

    [Fact]
    public void A_book_passed_over_for_printing_no_isbn_is_looked_at_again_once_for_its_product_codes()
    {
        Assert.True(BookLookups.HadNoIsbn("It prints no ISBN to look up."));
        Assert.False(BookLookups.HadNoIsbn(BookLookups.NothingToLookUp));
    }

    [Fact]
    public async Task Every_catalogue_is_asked_and_one_that_couldnt_answer_is_left_out_of_where_it_asked()
    {
        var rpggeek = new Fixed("RPGGeek", Record("RPGGeek"));
        var openLibrary = new Fixed("Open Library", null, unavailable: true);
        var google = new Fixed("Google Books", Record("Google Books"));

        var answer = await new BookCatalogues([rpggeek, openLibrary, google]).AskAsync(["9781234567897"], [], [], Ct);
        var none = await new BookCatalogues([new Fixed("Open Library", null), new Fixed("Google Books", null)]).AskAsync(["9781234567897"], [], [], Ct);

        Assert.Equal(["RPGGeek", "Google Books"], answer.Records.Select(r => r.Source));
        Assert.Equal("RPGGeek or Google Books", answer.AskedOf);
        Assert.Equal((0, "Open Library or Google Books"), (none.Records.Count, none.AskedOf));
        // None could answer: the book waits.
        await Assert.ThrowsAsync<CatalogueUnavailableException>(() =>
            new BookCatalogues([new Fixed("Open Library", null, unavailable: true), new Fixed("Google Books", null, unavailable: true)]).AskAsync(["9781234567897"], [], [], Ct));
    }
}

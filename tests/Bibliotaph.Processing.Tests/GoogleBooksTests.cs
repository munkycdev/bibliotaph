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
    public async Task The_first_catalogue_with_a_record_wins_and_the_next_is_asked_only_without_one()
    {
        var first = new Fixed("Open Library", Record("Open Library"));
        var second = new Fixed("Google Books", Record("Google Books"));
        var catalogues = new BookCatalogues([first, second]);

        Assert.Equal("Open Library or Google Books", catalogues.Name);
        Assert.Equal("Open Library", (await catalogues.FindAsync(["9781234567897"], Ct))!.Source);
        Assert.Equal(0, second.Asked);

        Assert.Equal("Google Books", (await new BookCatalogues([new Fixed("Open Library", null), second]).FindAsync(["9781234567897"], Ct))!.Source);
    }

    [Fact]
    public async Task A_book_none_knows_waits_while_one_couldnt_answer()
    {
        Assert.Null(await new BookCatalogues([new Fixed("Open Library", null), new Fixed("Google Books", null)]).FindAsync(["9781234567897"], Ct));
        Assert.Equal("Google Books", (await new BookCatalogues([new Fixed("Open Library", null, unavailable: true), new Fixed("Google Books", Record("Google Books"))])
            .FindAsync(["9781234567897"], Ct))!.Source);
        await Assert.ThrowsAsync<CatalogueUnavailableException>(() =>
            new BookCatalogues([new Fixed("Open Library", null, unavailable: true), new Fixed("Google Books", null)]).FindAsync(["9781234567897"], Ct));
    }
}

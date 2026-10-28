using System.Net;

namespace Bibliotaph.Processing.Tests;

/// <summary>Asking Open Library about a book's ISBNs, with a stand-in for its server.</summary>
public sealed class OpenLibraryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Server(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host.");
    }

    // Made up: no real book's record.
    const string Answer = """
        {
          "ISBN:9781234567897": {
            "key": "/books/OL1M",
            "title": "The Owlbear Mill",
            "authors": [{ "url": "https://openlibrary.org/authors/OL1A", "name": "A. Example" }, { "name": "B. Example" }],
            "publishers": [{ "name": "Example Press" }],
            "publish_date": "March 2019",
            "subjects": [{ "name": "Fantasy games" }]
          }
        }
        """;

    [Fact]
    public async Task All_of_a_books_isbns_are_asked_about_at_once_and_only_they_are_sent()
    {
        var server = new Server(HttpStatusCode.OK, Answer);
        var catalogue = new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(server));

        var record = await catalogue.FindAsync(["9780555555552", "9781234567897"], Ct);

        var asked = Assert.Single(server.Asked);
        Assert.Equal("https://openlibrary.org/api/books?bibkeys=ISBN%3A9780555555552%2CISBN%3A9781234567897&format=json&jscmd=data", asked.RequestUri!.AbsoluteUri);
        Assert.Contains("bibliotaph.online", asked.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Equal(("Open Library", "OL1M", "9781234567897", "The Owlbear Mill", "March 2019"),
            (record!.Source, record.Key, record.Isbn, record.Title, record.Published));
        Assert.Equal(["A. Example", "B. Example"], record.Authors);
        Assert.Equal(["Example Press"], record.Publishers);
    }

    [Fact]
    public async Task An_isbn_it_doesnt_know_has_no_record()
    {
        var catalogue = new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(new Server(HttpStatusCode.OK, "{}")));

        Assert.Null(await catalogue.FindAsync(["9781234567897"], Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_busy_or_refusing_catalogue_is_unavailable_not_an_answer(HttpStatusCode status)
    {
        var catalogue = new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(new Server(status, "")));

        await Assert.ThrowsAsync<CatalogueUnavailableException>(() => catalogue.FindAsync(["9781234567897"], Ct));
    }

    [Fact]
    public async Task A_catalogue_that_cant_be_reached_is_unavailable()
    {
        var catalogue = new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(new Unreachable()));

        await Assert.ThrowsAsync<CatalogueUnavailableException>(() => catalogue.FindAsync(["9781234567897"], Ct));
    }
}

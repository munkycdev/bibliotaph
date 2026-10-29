using System.Net;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Asking RPGGeek about a book, with a stand-in for BoardGameGeek's XML API: searched by name, taken by ISBN.</summary>
public sealed class RpgGeekTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Server(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // Made up, in the shape BoardGameGeek answers in: no real product's record.
    const string Search = """
        <?xml version="1.0" encoding="utf-8"?><items total="4">
          <item type="rpgitem" id="301"><name type="primary" value="Maps for The Owlbear Mill"/><yearpublished value="2023" /></item>
          <item type="rpgitem" id="302"><name type="primary" value="The Owlbear Mill"/><yearpublished value="2019" /></item>
          <item type="rpgitem" id="303"><name type="primary" value="Goblin Warrens"/></item>
          <item type="rpgitem" id="304"><name type="alternate" value="The Owlbear Mill: Deluxe"/></item>
        </items>
        """;

    const string Things = """
        <?xml version="1.0" encoding="utf-8"?><items>
          <item type="rpgitem" id="302">
            <name type="primary" sortindex="1" value="The Owlbear Mill" />
            <link type="rpg" id="1" value="Example System" />
            <yearpublished value="2019" />
            <link type="rpgpublisher" id="2" value="Example Press" />
            <link type="rpgdesigner" id="3" value="A. Example" />
            <link type="rpgdesigner" id="4" value="(Uncredited)" />
            <versions>
              <item type="rpgitemversion" id="9001"><name type="primary" value="PDF" /><yearpublished value="0" />
                <productcode value="" /><isbn10 value="" /><isbn13 value="" /></item>
              <item type="rpgitemversion" id="9002"><name type="primary" value="Print" /><yearpublished value="2020" />
                <link type="rpgpublisher" id="5" value="Sample Games" />
                <productcode value="EX1001" /><isbn10 value="1-234-56789-X" /><isbn13 value="" /></item>
            </versions>
          </item>
          <item type="rpgitem" id="301">
            <name type="primary" value="Maps for The Owlbear Mill" />
            <versions><item type="rpgitemversion" id="9003"><isbn13 value="9780306406157" /></item></versions>
          </item>
        </items>
        """;

    static RpgGeekCatalogue Catalogue(Server server) => new(RpgGeekCatalogue.CreateClient(server), "test-token", pace: TimeSpan.Zero);

    static Server Answering() => new(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) ? Search : Things));

    [Fact]
    public async Task A_book_is_searched_for_by_title_and_taken_only_from_a_version_listing_its_isbn()
    {
        var server = Answering();
        var catalogue = Catalogue(server);

        var answer = await catalogue.AskAsync(["9781234567897"], ["The Owlbear Mill"], Ct);
        var again = await catalogue.AskAsync(["9781234567897"], ["The Owlbear Mill"], Ct);
        var other = await catalogue.AskAsync(["9780555555552"], ["The Owlbear Mill"], Ct);

        var record = Assert.Single(answer.Records);
        Assert.Equal(("RPGGeek", "302", "9781234567897", "The Owlbear Mill", "2020"), (record.Source, record.Key, record.Isbn, record.Title, record.Published));
        Assert.Equal(["A. Example"], record.Authors);
        Assert.Equal(["Sample Games"], record.Publishers);
        Assert.Equal("RPGGeek", answer.AskedOf);
        // The exact name first, then those starting with it, then those holding it; one that doesn't is left out.
        Assert.Equal("https://boardgamegeek.com/xmlapi2/search?query=The+Owlbear+Mill&type=rpgitem", server.Asked[0].RequestUri!.AbsoluteUri);
        Assert.Equal("https://boardgamegeek.com/xmlapi2/thing?id=302,304,301&type=rpgitem&versions=1", server.Asked[1].RequestUri!.AbsoluteUri);
        Assert.All(server.Asked, a => Assert.Equal(("Bearer", "test-token"), (a.Headers.Authorization!.Scheme, a.Headers.Authorization.Parameter)));
        // Kept: asking again sends nothing, and a book whose ISBN no version lists has no record.
        Assert.Equal((record.Key, record.Title), (Assert.Single(again.Records).Key, again.Records[0].Title));
        Assert.Empty(other.Records);
        Assert.Equal(2, server.Asked.Count);
    }

    [Fact]
    public async Task Without_a_title_there_is_nothing_to_search_for()
    {
        var server = Answering();

        Assert.Empty((await Catalogue(server).AskAsync(["9781234567897"], [], Ct)).Records);
        Assert.Null(await Catalogue(server).FindAsync(["9781234567897"], Ct));
        Assert.Empty(server.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "busy")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    [InlineData(HttpStatusCode.Unauthorized, "token")]
    public async Task Throttled_or_refused_is_unavailable_not_an_answer(HttpStatusCode status, string said)
    {
        var ex = await Assert.ThrowsAsync<CatalogueUnavailableException>(() =>
            Catalogue(new Server(_ => (status, ""))).AskAsync(["9781234567897"], ["The Owlbear Mill"], Ct));
        Assert.Contains(said, ex.Message, StringComparison.Ordinal);
    }

    sealed class Fixed(BookRecord record) : IBookCatalogue
    {
        public string Name => record.Source;

        public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default) => Task.FromResult<BookRecord?>(record);
    }

    [Fact]
    public async Task Asked_after_the_others_it_searches_their_records_title_first_and_its_record_still_comes_first()
    {
        var server = Answering();
        var openLibrary = new Fixed(new BookRecord("Open Library", "OL1M", "9781234567897", "The Owlbear Mill", [], ["Example Press"], "2019"));

        var answer = await new BookCatalogues([Catalogue(server), openLibrary]).AskAsync(["9781234567897"], ["owlbear_mill_v2"], Ct);

        Assert.Equal(["RPGGeek", "Open Library"], answer.Records.Select(r => r.Source));
        Assert.Equal("RPGGeek or Open Library", answer.AskedOf);
        Assert.Contains("query=The+Owlbear+Mill", server.Asked[0].RequestUri!.Query, StringComparison.Ordinal);
    }
}

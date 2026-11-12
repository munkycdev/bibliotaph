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
            <link type="rpgcategory" id="6" value="Scenario / Adventure / Module" />
            <link type="rpggenre" id="7" value="Horror (Supernatural)" />
            <link type="rpgsetting" id="8" value="Example World" />
            <link type="rpgseries" id="9" value="Example Line" />
            <link type="rpgmechanic" id="10" value="Dice (Various)" />
            <link type="rpgartist" id="11" value="C. Painter" />
            <link type="rpgproducer" id="12" value="D. Producer" />
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

        var answer = await catalogue.AskAsync(["9781234567897"], ["The Owlbear Mill"], [], ct: Ct);
        var again = await catalogue.AskAsync(["9781234567897"], ["The Owlbear Mill"], [], ct: Ct);
        var other = await catalogue.AskAsync(["9780555555552"], ["The Owlbear Mill"], [], ct: Ct);

        var record = Assert.Single(answer.Records);
        Assert.Equal(("RPGGeek", "302", "9781234567897", "The Owlbear Mill", "2020"), (record.Source, record.Key, record.Isbn, record.Title, record.Published));
        Assert.Equal(["A. Example"], record.Authors);
        Assert.Equal(["Sample Games"], record.Publishers);
        // What it's filed under, who drew it and its mechanics, in RPGGeek's words; producers aren't used.
        Assert.Equal(
            [("system", "Example System"), ("type", "Scenario / Adventure / Module"), ("theme", "Horror (Supernatural)"), ("setting", "Example World"),
                ("series", "Example Line"), ("artists", "C. Painter"), ("mechanics", "Dice (Various)")],
            record.Values!.Select(v => (v.Field, v.Value)));
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
    public async Task A_version_listing_a_product_code_the_book_prints_names_it_when_no_version_lists_its_isbn()
    {
        var catalogue = Catalogue(Answering());

        var byCode = Assert.Single((await catalogue.AskAsync([], ["The Owlbear Mill"], ["EX1001"], ct: Ct)).Records);
        var byIsbn = Assert.Single((await catalogue.AskAsync(["9780306406157"], ["The Owlbear Mill"], ["EX1001"], ct: Ct)).Records);
        // RPGGeek writes an empty code for a version without one, which matches nothing.
        var neither = await catalogue.AskAsync([], ["The Owlbear Mill"], ["EX9999"], ct: Ct);

        Assert.Equal(("302", "EX1001", "product code"), (byCode.Key, byCode.Isbn, BookRecords.FoundBy(byCode)));
        // An ISBN is the surer match, whichever item lists it.
        Assert.Equal(("301", "ISBN"), (byIsbn.Key, BookRecords.FoundBy(byIsbn)));
        Assert.Empty(neither.Records);
    }

    [Fact]
    public async Task Without_a_title_there_is_nothing_to_search_for()
    {
        var server = Answering();

        Assert.Empty((await Catalogue(server).AskAsync(["9781234567897"], [], [], ct: Ct)).Records);
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
            Catalogue(new Server(_ => (status, ""))).AskAsync(["9781234567897"], ["The Owlbear Mill"], [], ct: Ct));
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

        var answer = await new BookCatalogues([Catalogue(server), openLibrary]).AskAsync(["9781234567897"], ["owlbear_mill_v2"], [], ct: Ct);

        Assert.Equal(["RPGGeek", "Open Library"], answer.Records.Select(r => r.Source));
        Assert.Equal("RPGGeek or Open Library", answer.AskedOf);
        Assert.Contains("query=The+Owlbear+Mill", server.Asked[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>A search finding <paramref name="count"/> books of a made-up line, from id 401.</summary>
    static string LineSearch(int count) =>
        $"""<?xml version="1.0" encoding="utf-8"?><items total="{count + 1}">"""
        + string.Concat(Enumerable.Range(401, count).Select(id => $"""<item type="rpgitem" id="{id}"><name type="primary" value="Gull Rock Saga: Book {id - 400}"/></item>"""))
        + """<item type="rpgitem" id="399"><name type="primary" value="Goblin Warrens"/></item></items>""";

    /// <summary>The items asked for, each listing the ISBN <paramref name="isbns"/> gives its id, if any.</summary>
    static string LineThings(string ids, IReadOnlyDictionary<string, string> isbns) =>
        """<?xml version="1.0" encoding="utf-8"?><items>"""
        + string.Concat(ids.Split(',').Select(id => $"""<item type="rpgitem" id="{id}"><name type="primary" value="Gull Rock Saga: Book {int.Parse(id, System.Globalization.CultureInfo.InvariantCulture) - 400}"/>"""
            + $"""<versions><item type="rpgitemversion" id="9{id}"><isbn13 value="{isbns.GetValueOrDefault(id, "")}" /></item></versions></item>"""))
        + "</items>";

    static Server LineServer(int count, IReadOnlyDictionary<string, string> isbns) => new(r =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
        if (r.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
            return (HttpStatusCode.OK, query["query"] == "Gull Rock Saga" ? LineSearch(count) : """<?xml version="1.0" encoding="utf-8"?><items total="0"></items>""");
        return (HttpStatusCode.OK, LineThings(query["id"]!, isbns));
    });

    [Fact]
    public async Task A_book_whose_title_finds_nothing_is_found_through_the_line_it_prints_and_the_line_is_asked_about_once()
    {
        var server = LineServer(25, new Dictionary<string, string> { ["423"] = "9781234567897", ["405"] = "9780306406157" });
        var catalogue = Catalogue(server);

        // A file's name runs the words together and leaves the line out, so RPGGeek's search finds nothing by it.
        var tome = await catalogue.AskAsync(["9781234567897"], ["Monster Tome Vol1"], [], ["Gull Rock Saga"], Ct);
        var asked = server.Asked.Count;
        var atlas = await catalogue.AskAsync(["9780306406157"], ["World Atlas Vol1"], [], ["Gull Rock Saga"], Ct);

        Assert.Equal(("423", "Gull Rock Saga: Book 23"), (Assert.Single(tome.Records).Key, tome.Records[0].Title));
        Assert.Equal("405", Assert.Single(atlas.Records).Key);
        // Its title, then its line, whose items are read twenty at a time; the one not of the line isn't read.
        Assert.Equal(["search:Monster Tome Vol1", "search:Gull Rock Saga", "thing:20", "thing:5"], server.Asked.Take(asked).Select(Shown));
        // The next book in the line asks only about its own title: the line's search and items are kept.
        Assert.Equal(["search:World Atlas Vol1"], server.Asked.Skip(asked).Select(Shown));
        Assert.DoesNotContain(server.Asked, a => a.RequestUri!.Query.Contains("399", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_line_that_names_too_many_books_is_read_only_so_far()
    {
        var server = LineServer(100, new Dictionary<string, string> { ["490"] = "9781234567897" });

        var answer = await Catalogue(server).AskAsync(["9781234567897"], [], [], ["Gull Rock Saga"], Ct);

        Assert.Empty(answer.Records);
        Assert.Equal(["search:Gull Rock Saga", "thing:20", "thing:20", "thing:20"], server.Asked.Select(Shown));
    }

    [Fact]
    public async Task An_item_holding_every_word_of_the_title_in_another_order_may_be_the_book()
    {
        var server = new Server(r => (HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? """
              <items total="3">
                <item type="rpgitem" id="501"><name type="primary" value="Bestiary: Creatures"/></item>
                <item type="rpgitem" id="502"><name type="primary" value="Creatures Bestiary"/></item>
                <item type="rpgitem" id="503"><name type="primary" value="Creatures of the Deep"/></item>
              </items>
              """
            : """
              <items><item type="rpgitem" id="501"><name type="primary" value="Bestiary: Creatures"/>
                <versions><item type="rpgitemversion" id="9501"><isbn13 value="9781234567897" /></item></versions></item></items>
              """));

        var answer = await Catalogue(server).AskAsync(["9781234567897"], ["Creatures Bestiary"], [], ct: Ct);

        Assert.Equal("501", Assert.Single(answer.Records).Key);
        Assert.Equal("502,501", System.Web.HttpUtility.ParseQueryString(server.Asked[1].RequestUri!.Query)["id"]);
    }

    [Fact]
    public async Task A_title_given_twice_however_it_is_written_is_searched_for_once()
    {
        var server = Answering();

        await Catalogue(server).AskAsync(["9780555555552"], ["The Owlbear Mill", "the owlbear mill."], [], ["The Owlbear Mill"], Ct);

        Assert.Single(server.Asked, a => a.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal));
    }

    static string Shown(HttpRequestMessage request)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        return request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? $"search:{query["query"]}"
            : $"thing:{query["id"]!.Split(',').Length}";
    }
}

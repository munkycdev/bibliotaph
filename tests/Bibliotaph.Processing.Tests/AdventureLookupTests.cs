using System.Net;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Asking AdventureLookup about a book, with a stand-in for its API: searched by title, named by title.</summary>
public sealed class AdventureLookupTests
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

    // Made up, in the shape AdventureLookup answers in: no real adventure's record.
    const string Search = """
        {"total_count": 3, "seed": "2026-44", "adventures": [
          {"id": 901, "title": "Maps of the Owlbear Mill", "authors": [], "edition": "5th Edition", "environments": []},
          {"id": 902, "title": "The Owlbear Mill", "slug": "the-owlbear-mill", "description": "A blurb.",
           "authors": ["Ada Quill", "B. Example"], "edition": "5th Edition",
           "environments": ["Dungeon", "Forest", "Feywild", "Other", "Town/Village"], "items": ["Wand of Example"],
           "publisher": "Gull Rock Games", "setting": "Forgotten Realms", "common_monsters": ["Goblin"], "boss_monsters": ["Owlbear"],
           "min_starting_level": 1, "max_starting_level": 3, "starting_level_range": "Tier 1 5E 1-4", "num_pages": 32,
           "found_in": null, "part_of": "Example Saga", "official_url": "https://www.example.com/product/1?src=list",
           "thumbnail_url": "https://example.com/cover.jpg", "soloable": false, "has_pregenerated_characters": true,
           "has_tactical_maps": true, "has_handouts": null, "publication_year": 2019},
          {"id": 903, "title": "Goblin Warrens", "authors": ["C. Example"], "edition": "OSR", "setting": "Setting Neutral",
           "publisher": "DMs Guild", "starting_level_range": "Paragon 4E 11-20"}
        ]}
        """;

    static AdventureLookupCatalogue Catalogue(Server server) => new(AdventureLookupCatalogue.CreateClient(server), pace: TimeSpan.Zero);

    static Server Answering(string body = Search) => new(_ => (HttpStatusCode.OK, body));

    [Fact]
    public async Task A_book_is_searched_for_by_title_and_named_by_the_adventure_of_that_title()
    {
        var server = Answering();
        var catalogue = Catalogue(server);

        var answer = await catalogue.AskAsync([], ["the owlbear mill"], [], Ct);
        var again = await catalogue.AskAsync(["9781234567897"], ["the owlbear mill"], [], Ct);

        var record = Assert.Single(answer.Records);
        Assert.Equal(("AdventureLookup", "902", "", "The Owlbear Mill", "2019", 32), (record.Source, record.Key, record.Isbn, record.Title, record.Published, record.Pages));
        Assert.True(BookRecords.ByTitle(record));
        Assert.Equal("title", BookRecords.FoundBy(record));
        Assert.Equal(["Ada Quill", "B. Example"], record.Authors);
        Assert.Equal(["Gull Rock Games"], record.Publishers);
        Assert.Equal(
            ["system:D&D 5e", "environment:Underground", "environment:Forest", "environment:Planar", "theme:Fey", "environment:Urban",
             "setting:Forgotten Realms", "levels:1-3", "series:Example Saga", "monsters:Owlbear", "monsters:Goblin", "items:Wand of Example",
             "includes:Pre-generated characters", "includes:Battle maps"],
            record.Values!.Select(v => $"{v.Field}:{v.Value}"));
        Assert.Equal(record.Key, Assert.Single(again.Records).Key);
        // Asked once: the answer is kept, and only the title is sent.
        var asked = Assert.Single(server.Asked);
        Assert.Equal("https://adventurelookup.com/api/adventures?q=the%20owlbear%20mill", asked.RequestUri!.AbsoluteUri);
        Assert.Contains("Bibliotaph", asked.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_title_with_more_words_finds_the_adventure_it_names_and_store_names_and_vague_values_say_nothing()
    {
        var catalogue = Catalogue(Answering());

        var longer = await catalogue.AskAsync([], ["Goblin Warrens (Revised PDF)"], [], Ct);
        var unknown = await catalogue.AskAsync([], ["Lighthouse at the End"], [], Ct);

        var record = Assert.Single(longer.Records);
        Assert.Equal(("903", "Goblin Warrens"), (record.Key, record.Title));
        Assert.Empty(record.Publishers);
        Assert.Null(record.Pages);
        Assert.Equal(["levels:11-20"], record.Values!.Select(v => $"{v.Field}:{v.Value}"));
        Assert.Empty(unknown.Records);
    }

    [Fact]
    public async Task Without_a_title_there_is_nothing_to_search_for()
    {
        var server = Answering();

        var answer = await Catalogue(server).AskAsync(["9781234567897"], [" "], [], Ct);

        Assert.Empty(answer.Records);
        Assert.Empty(server.Asked);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "AdventureLookup is busy. Books will be looked up when it answers again.")]
    [InlineData(HttpStatusCode.BadGateway, "AdventureLookup is busy. Books will be looked up when it answers again.")]
    [InlineData(HttpStatusCode.Forbidden, "AdventureLookup turned the request down (403).")]
    public async Task Throttled_or_refused_is_unavailable_not_an_answer(HttpStatusCode status, string said)
    {
        var catalogue = Catalogue(new Server(_ => (status, "")));

        var ex = await Assert.ThrowsAsync<CatalogueUnavailableException>(() => catalogue.AskAsync([], ["The Owlbear Mill"], [], Ct));

        Assert.Equal(said, ex.Message);
    }

    [Fact]
    public async Task An_answer_that_isnt_json_is_unavailable()
    {
        var catalogue = Catalogue(Answering("<html>maintenance</html>"));

        var ex = await Assert.ThrowsAsync<CatalogueUnavailableException>(() => catalogue.AskAsync([], ["The Owlbear Mill"], [], Ct));

        Assert.Equal("AdventureLookup's answer couldn't be read.", ex.Message);
    }

    [Fact]
    public async Task Asked_beside_catalogues_that_need_an_isbn_only_it_is_asked_about_a_book_printing_none()
    {
        var server = Answering();
        var isbnOnly = new IsbnOnly();
        var catalogues = new BookCatalogues([isbnOnly, Catalogue(server)]);

        var answer = await catalogues.AskAsync([], ["The Owlbear Mill"], [], Ct);

        Assert.True(catalogues.FindsByTitleAlone);
        Assert.Equal(0, isbnOnly.Asked);
        Assert.Equal("902", Assert.Single(answer.Records).Key);
        // The one that needs an ISBN counts as asked, so the book isn't asked again for it.
        Assert.Equal("Open Library or AdventureLookup", answer.AskedOf);
    }

    sealed class IsbnOnly : IBookCatalogue
    {
        public int Asked { get; private set; }

        public string Name => "Open Library";

        public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<BookRecord?>(null);
        }
    }
}

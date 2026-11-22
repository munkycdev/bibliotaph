namespace Bibliotaph.Processing.Tests;

/// <summary>The catalogues a computer asks on its own, with the user's own RPGGeek token and Google Books key.</summary>
public sealed class OwnCataloguesTests
{
    sealed class Keys : IApiKeyStore
    {
        public Dictionary<string, string> Kept { get; } = [];

        public bool Refuses { get; set; }

        public string? Find(string endpoint) => Kept.GetValueOrDefault(endpoint);

        public bool Remember(string endpoint, string key)
        {
            if (Refuses) return false;
            Kept[endpoint] = key;
            return true;
        }

        public void Forget(string endpoint) => Kept.Remove(endpoint);
    }

    // Nothing is asked in these tests; a handler that answers nothing keeps them off the network.
    sealed class Nowhere : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing should be asked.");
    }

    [Fact]
    public void Without_keys_it_asks_the_catalogues_that_need_none()
    {
        var catalogues = new OwnCatalogues(new Keys(), handler: new Nowhere());

        Assert.Equal("AdventureLookup or Open Library", catalogues.Name);
        Assert.False(catalogues.HasRpgGeekToken);
        Assert.False(catalogues.HasGoogleBooksKey);
        // AdventureLookup is searched by title, so a book printing no ISBN is asked about too.
        Assert.True(catalogues.FindsByTitleAlone);
    }

    [Fact]
    public void A_saved_token_and_key_add_RPGGeek_first_and_Google_Books_last_and_say_so()
    {
        var keys = new Keys();
        var catalogues = new OwnCatalogues(keys, handler: new Nowhere());
        var changes = 0;
        catalogues.NameChanged += (_, _) => changes++;

        Assert.True(catalogues.SaveRpgGeekToken("  bgg-token  "));
        Assert.True(catalogues.SaveGoogleBooksKey("google-key"));

        Assert.Equal("RPGGeek or AdventureLookup or Open Library or Google Books", catalogues.Name);
        Assert.Equal("bgg-token", keys.Kept[OwnCatalogues.RpgGeekKey]);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Forgetting_a_token_stops_asking_RPGGeek()
    {
        var keys = new Keys();
        keys.Kept[OwnCatalogues.RpgGeekKey] = "bgg-token";
        var catalogues = new OwnCatalogues(keys, handler: new Nowhere());
        Assert.StartsWith("RPGGeek", catalogues.Name, StringComparison.Ordinal);

        catalogues.ForgetRpgGeekToken();

        Assert.Equal("AdventureLookup or Open Library", catalogues.Name);
        Assert.Empty(keys.Kept);
    }

    [Fact]
    public void A_token_that_couldnt_be_kept_or_is_blank_changes_nothing()
    {
        var keys = new Keys { Refuses = true };
        var catalogues = new OwnCatalogues(keys, handler: new Nowhere());
        var changes = 0;
        catalogues.NameChanged += (_, _) => changes++;

        Assert.False(catalogues.SaveRpgGeekToken("bgg-token"));
        Assert.False(catalogues.SaveGoogleBooksKey("   "));

        Assert.Equal("AdventureLookup or Open Library", catalogues.Name);
        Assert.Equal(0, changes);
    }
}

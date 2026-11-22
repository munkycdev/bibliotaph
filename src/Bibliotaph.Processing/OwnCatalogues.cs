using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// The catalogues a computer asks on its own, while it isn't connected to its web library, in the order the server
/// asks them: RPGGeek with the user's own token, AdventureLookup, Open Library, then Google Books with the user's own
/// key. The two that need one are asked only once the user has saved it (Settings › Online), and it is kept where the
/// app keeps secrets, never in the catalog. Saving or forgetting one makes the catalogues anew and raises
/// <see cref="NameChanged"/>, so a book none of them found is asked again.
/// </summary>
public sealed class OwnCatalogues : IBookCatalogue
{
    /// <summary>Where the user's RPGGeek token is kept, under BoardGameGeek's API address.</summary>
    public const string RpgGeekKey = "https://boardgamegeek.com/xmlapi2";

    /// <summary>Where the user's Google Books key is kept, under its API address.</summary>
    public const string GoogleBooksKey = "https://www.googleapis.com/books/v1";

    readonly IApiKeyStore _keys;
    readonly TimeProvider _clock;
    readonly HttpMessageHandler? _handler;
    // Kept as the keys change, with the answers they have kept and the pace they keep.
    readonly AdventureLookupCatalogue _adventures;
    readonly OpenLibraryCatalogue _openLibrary;
    RpgGeekCatalogue? _rpgGeek;
    GoogleBooksCatalogue? _googleBooks;
    string? _rpgGeekToken;
    string? _googleBooksKey;
    BookCatalogues _chain;

    public OwnCatalogues(IApiKeyStore keys, TimeProvider? clock = null, HttpMessageHandler? handler = null,
        AdventureLookupCatalogue? adventures = null, OpenLibraryCatalogue? openLibrary = null)
    {
        _keys = keys;
        _clock = clock ?? TimeProvider.System;
        _handler = handler;
        _adventures = adventures ?? new AdventureLookupCatalogue(AdventureLookupCatalogue.CreateClient(handler), _clock);
        _openLibrary = openLibrary ?? new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(handler), _clock);
        _chain = Build();
    }

    public string Name => _chain.Name;

    public bool FindsByTitleAlone => _chain.FindsByTitleAlone;

    public event EventHandler? NameChanged;

    public bool HasRpgGeekToken => _rpgGeekToken is not null;

    public bool HasGoogleBooksKey => _googleBooksKey is not null;

    /// <summary>Saves the user's RPGGeek token, from an app they registered with BoardGameGeek. False when it couldn't be kept.</summary>
    public bool SaveRpgGeekToken(string token) => Save(RpgGeekKey, token);

    public void ForgetRpgGeekToken() => Forget(RpgGeekKey);

    /// <summary>Saves the user's Google Books API key, from their own Google Cloud project. False when it couldn't be kept.</summary>
    public bool SaveGoogleBooksKey(string key) => Save(GoogleBooksKey, key);

    public void ForgetGoogleBooksKey() => Forget(GoogleBooksKey);

    public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default) => _chain.FindAsync(isbns, ct);

    public Task<CatalogueAnswer> AskAsync(IReadOnlyList<string> isbns, IReadOnlyList<string> titles, IReadOnlyList<string> codes,
        IReadOnlyList<string>? lines = null, IReadOnlyList<string>? families = null, CancellationToken ct = default) =>
        _chain.AskAsync(isbns, titles, codes, lines, families, ct);

    public Task<BookRecord?> ReadAsync(string source, string key, CancellationToken ct = default) => _chain.ReadAsync(source, key, ct);

    bool Save(string name, string value)
    {
        if (value.Trim() is not { Length: > 0 } trimmed || !_keys.Remember(name, trimmed)) return false;
        Rebuild();
        return true;
    }

    void Forget(string name)
    {
        _keys.Forget(name);
        Rebuild();
    }

    void Rebuild()
    {
        var before = _chain.Name;
        _chain = Build();
        if (_chain.Name != before) NameChanged?.Invoke(this, EventArgs.Empty);
    }

    BookCatalogues Build()
    {
        var token = _keys.Find(RpgGeekKey) is { Length: > 0 } t ? t : null;
        var key = _keys.Find(GoogleBooksKey) is { Length: > 0 } k ? k : null;
        // A catalogue whose key didn't change keeps the answers it has.
        if (token != _rpgGeekToken) _rpgGeek = token is null ? null : new RpgGeekCatalogue(RpgGeekCatalogue.CreateClient(_handler), token, _clock);
        if (key != _googleBooksKey) _googleBooks = key is null ? null : new GoogleBooksCatalogue(GoogleBooksCatalogue.CreateClient(_handler), key, _clock);
        (_rpgGeekToken, _googleBooksKey) = (token, key);
        List<IBookCatalogue> catalogues = [];
        if (_rpgGeek is not null) catalogues.Add(_rpgGeek);
        catalogues.Add(_adventures);
        catalogues.Add(_openLibrary);
        if (_googleBooks is not null) catalogues.Add(_googleBooks);
        return new BookCatalogues(catalogues);
    }
}

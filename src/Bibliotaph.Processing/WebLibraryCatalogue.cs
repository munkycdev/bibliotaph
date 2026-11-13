using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// Where a PC looks books up. While it's connected, through its web library, which asks Open Library and then the
/// catalogues the server holds a key for, such as Google Books, so no PC keeps a key; and Open Library directly while
/// it isn't. Its name is where the web library said it asks, kept for the next start, so a book none of them knew is
/// asked again only once the web library can ask somewhere more.
/// </summary>
public sealed class WebLibraryCatalogue(WebLibrary web, IBookCatalogue direct, SettingsStore settings) : IBookCatalogue
{
    volatile string? _webCatalogues;
    volatile bool _loaded;

    public string Name => web.HasKey ? _webCatalogues ?? direct.Name : direct.Name;

    /// <summary>
    /// While connected, whether the web library asks AdventureLookup or RPGGeek, as its catalogues' names say, so a book
    /// printing no ISBN or code is sent with its title only where it can be found that way.
    /// </summary>
    public bool FindsByTitleAlone => web.HasKey
        ? Name.Split(" or ").Any(n => n is AdventureLookupCatalogue.Source or RpgGeekCatalogue.Source)
        : direct.FindsByTitleAlone;

    public event EventHandler? NameChanged;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        if (await web.LookupCataloguesAsync(ct) is { } catalogues) await LearnAsync(catalogues, ct);
    }

    public async Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default) =>
        (await AskAsync(isbns, [], [], ct: ct)).Records is [var first, ..] ? first : null;

    public async Task<CatalogueAnswer> AskAsync(IReadOnlyList<string> isbns, IReadOnlyList<string> titles, IReadOnlyList<string> codes,
        IReadOnlyList<string>? lines = null, IReadOnlyList<string>? families = null, CancellationToken ct = default)
    {
        if (!web.HasKey) return await direct.AskAsync(isbns, titles, codes, lines, families, ct);
        await LoadAsync(ct);
        var answer = await web.LookUpAsync(isbns, titles, codes, lines ?? [], families ?? [], ct)
            ?? throw new CatalogueUnavailableException($"Waiting for {(await web.AddressAsync(ct)).Host}.");
        await LearnAsync(answer.Catalogues, ct);
        if (answer.Problem is { } problem) throw new CatalogueUnavailableException(problem);
        var records = answer.Records ?? (answer.Record is { } first ? [first] : []);
        return new CatalogueAnswer([.. records.Select(Read)], answer.AskedOf ?? answer.Catalogues,
            [.. (answer.Candidates ?? []).Select(c => new CatalogueItem(c.Source, c.Key, c.Name))]);
    }

    /// <summary>
    /// Through the web library while connected, which holds RPGGeek's token: a PC on its own asks only Open Library,
    /// which is read by ISBN alone. Only RPGGeek's records are asked for, since no other catalogue's can be read by
    /// its id, and an answer from another PC about one of theirs waits for a lookup here to give the record.
    /// </summary>
    public async Task<BookRecord?> ReadAsync(string source, string key, CancellationToken ct = default)
    {
        if (!web.HasKey) return await direct.ReadAsync(source, key, ct);
        if (source != RpgGeekCatalogue.Source || !key.All(char.IsAsciiDigit)) return null;
        var answer = await web.ReadRecordAsync(source, key, ct)
            ?? throw new CatalogueUnavailableException($"Waiting for {(await web.AddressAsync(ct)).Host}.");
        if (answer.Problem is { } problem) throw new CatalogueUnavailableException(problem);
        return answer.Record is { } record ? Read(record) : null;
    }

    static BookRecord Read(PcBookRecord r) => new(r.Source, r.Key, r.Isbn, r.Title, r.Authors, r.Publishers, r.Published,
        r.Values?.Select(v => new CatalogueValue(v.Field, v.Value)).ToList(), r.Pages, r.Families);

    async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        // Read twice at worst, and never over what was learned meanwhile.
        _webCatalogues ??= await settings.GetAsync(SettingKeys.LookUpWebCatalogues, ct);
        _loaded = true;
    }

    async Task LearnAsync(string catalogues, CancellationToken ct)
    {
        if (catalogues == _webCatalogues) return;
        _webCatalogues = catalogues;
        await settings.SetAsync(SettingKeys.LookUpWebCatalogues, catalogues, ct);
        NameChanged?.Invoke(this, EventArgs.Empty);
    }
}

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

    public event EventHandler? NameChanged;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        if (await web.LookupCataloguesAsync(ct) is { } catalogues) await LearnAsync(catalogues, ct);
    }

    public async Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
    {
        if (!web.HasKey) return await direct.FindAsync(isbns, ct);
        await LoadAsync(ct);
        var answer = await web.LookUpAsync(isbns, ct)
            ?? throw new CatalogueUnavailableException($"{(await web.AddressAsync(ct)).Host} can't be reached just now, so books wait to be looked up.");
        await LearnAsync(answer.Catalogues, ct);
        if (answer.Problem is { } problem) throw new CatalogueUnavailableException(problem);
        return answer.Record is { } r ? new BookRecord(r.Source, r.Key, r.Isbn, r.Title, r.Authors, r.Publishers, r.Published) : null;
    }

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

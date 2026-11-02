using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What one of the user's Nornis worlds may read: the books its collection holds, sub-collections included, and their
/// pages. Only a world that is mapped, still has its collection and isn't gone has one.
/// </summary>
public sealed record NornisWorldBooks(long CollectionId, IReadOnlyList<LibraryEntry> Entries, LibraryFilter Filter, PageScope Pages)
{
    /// <summary>The book a page of a document belongs to in this world: an adventure inside the file if the page is in it, else the file.</summary>
    public LibraryEntry? EntryFor(long documentId, int pdfPage) =>
        Entries.Where(e => e.DocumentId == documentId && (e.Pages is not { } pages || (pdfPage >= pages.First && pdfPage <= pages.Last)))
            .OrderBy(e => e.IsPart ? 0 : 1).FirstOrDefault();
}

/// <summary>
/// What a Nornis world reaches in the library, the same on the web and on Windows: the books in its collection,
/// sub-collections included, with their pages, and the worlds a book can be sent to as an excerpt.
/// </summary>
public sealed class NornisScope(NornisWorldStore worlds, LibraryStore library, LibraryQueries queries, LibraryAskService ask)
{
    /// <summary>The world's books, or null when it isn't one of the library's live worlds with its collection.</summary>
    public async Task<NornisWorldBooks?> WorldAsync(Guid worldId, CancellationToken ct = default) =>
        await worlds.CollectionOfAsync(worldId.ToString("D"), ct) is { } collectionId ? await BooksAsync(collectionId, ct) : null;

    async Task<NornisWorldBooks> BooksAsync(long collectionId, CancellationToken ct)
    {
        var visible = await library.GetVisibleEntryIdsAsync(ct: ct);
        var filter = new LibraryFilter(visible, Sort: LibrarySort.Title, Group: ScopeKeys.Collection(collectionId));
        // Books owned elsewhere have nothing to read, and a pack is read through its files.
        var entries = (await queries.ListAsync(filter, ct: ct)).Where(e => !e.IsElsewhere && !e.IsPack).ToList();
        var pages = PageScope.Of(await queries.GetPageSpansAsync(filter, SearchPlan.From(SearchQuery.Parse("")), ct));
        return new NornisWorldBooks(collectionId, entries, filter, pages);
    }

    /// <summary>
    /// Send to Nornis for a document, or for a binder item's <paramref name="pages"/>: the live worlds that hold it, each
    /// with its excerpt address on Nornis at <paramref name="origin"/>, and why it can't go when it can't.
    /// </summary>
    public async Task<NornisSend> SendAsync(string origin, long documentId, (int First, int Last, string? Label)? pages, CancellationToken ct = default)
    {
        var sendable = (await WorldsHoldingAsync(documentId, pages?.First, ct))
            .Select(w => new NornisSendWorld(Guid.Parse(w.WorldId), w.Name, $"{origin.TrimEnd('/')}/bibliotaph/excerpt?world={w.WorldId}&document={documentId}"))
            .ToList();
        if (sendable.Count == 0) return new NornisSend([]);
        var problem = await ask.IsKeptFromAiAsync(documentId, ct) ? NornisSend.KeptFromAi
            : pages is { } chosen && chosen.Last - chosen.First + 1 > NornisSend.MostPages ? NornisSend.TooManyPages
            : null;
        return new NornisSend(sendable, problem, pages?.First, pages?.Last, pages?.Label);
    }

    /// <summary>
    /// The live worlds that hold a document, or the page <paramref name="fromPage"/> of it: an adventure inside a file
    /// lends only its own pages, so it's offered where its pages are.
    /// </summary>
    public async Task<IReadOnlyList<NornisWorldInfo>> WorldsHoldingAsync(long documentId, int? fromPage = null, CancellationToken ct = default)
    {
        var holding = new List<NornisWorldInfo>();
        foreach (var world in await worlds.LiveAsync(ct))
        {
            if (!Guid.TryParse(world.WorldId, out _)) continue;
            var books = await BooksAsync(world.CollectionId, ct);
            if (fromPage is { } page ? books.Pages.Contains(documentId, page) : books.Pages.Holds(documentId)) holding.Add(world);
        }
        return holding;
    }
}

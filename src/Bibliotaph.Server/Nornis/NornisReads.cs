using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Server.Libraries;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Nornis;

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
/// The one check every read Nornis makes passes through, and the way a read waits for a library: briefly, so a call
/// is answered within about two seconds, with 503 and code opening while the library is still coming back.
/// </summary>
public static class NornisReads
{
    /// <summary>How long a call waits for a library to open before answering opening.</summary>
    public static readonly TimeSpan OpeningWait = TimeSpan.FromSeconds(1);

    /// <summary>What Nornis is told to wait before asking again.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>A lease on the user's library, or the opening refusal when it isn't open within <see cref="OpeningWait"/>; it goes on opening.</summary>
    public static async Task<(LibraryLease? Lease, IResult? Refusal)> LeaseAsync(UserLibraries libraries, string userId, HttpContext context, CancellationToken ct)
    {
        if (await libraries.LeaseIfOpenAsync(userId) is { } open) return (open, null);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(OpeningWait);
        try
        {
            return (await libraries.OpenAsync(userId, wait.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (null, NornisProblems.Refuse(StatusCodes.Status503ServiceUnavailable, NornisProblems.Opening, "The library is opening. Ask again in a few seconds."));
        }
    }

    /// <summary>
    /// The world's books, or null when the world isn't one of this user's live worlds: every refusal then is the same
    /// 404, so nothing tells Nornis what else the library holds.
    /// </summary>
    public static async Task<NornisWorldBooks?> WorldAsync(UserLibrary library, Guid worldId, CancellationToken ct)
    {
        long collectionId;
        await using (var catalog = await library.Contexts.CreateDbContextAsync(ct))
        {
            var id = worldId.ToString("D");
            var world = await catalog.NornisWorlds.AsNoTracking().SingleOrDefaultAsync(w => w.WorldId == id, ct);
            if (world is not { GoneUtc: null, CollectionId: { } collection }) return null;
            collectionId = collection;
        }
        var visible = await library.Get<LibraryStore>().GetVisibleEntryIdsAsync(ct: ct);
        var filter = new LibraryFilter(visible, Sort: LibrarySort.Title, Group: ScopeKeys.Collection(collectionId));
        var queries = library.Get<LibraryQueries>();
        // Books owned elsewhere have nothing to read, and a pack is read through its files.
        var entries = (await queries.ListAsync(filter, ct: ct)).Where(e => !e.IsElsewhere && !e.IsPack).ToList();
        var pages = PageScope.Of(await queries.GetPageSpansAsync(filter, SearchPlan.From(SearchQuery.Parse("")), ct));
        return new NornisWorldBooks(collectionId, entries, filter, pages);
    }

    /// <summary>The cover names of the books in every live world of the user's: the only covers Nornis may fetch.</summary>
    public static async Task<IReadOnlySet<string>> CoversAsync(UserLibrary library, CancellationToken ct)
    {
        List<string> worlds;
        await using (var catalog = await library.Contexts.CreateDbContextAsync(ct))
            worlds = await catalog.NornisWorlds.AsNoTracking().Where(w => w.GoneUtc == null && w.CollectionId != null).Select(w => w.WorldId).ToListAsync(ct);
        var covers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var world in worlds)
            if (Guid.TryParse(world, out var id) && await WorldAsync(library, id, ct) is { } books)
                foreach (var entry in books.Entries)
                    if (entry.Cover is { } cover) covers.Add(cover);
        return covers;
    }
}

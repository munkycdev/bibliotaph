using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// The Summary page's figures, from both databases: index.db's for what the books are and what has been done with
/// them, catalog.db's for the folders' sizes and the user's own things. The Windows app shows it as it is, and the web
/// library sends the same figures to the browser, so the two never disagree.
/// </summary>
public sealed class LibrarySummaryService(LibraryStore library, SourceRootStore roots, SummaryStore store, SummaryQueries queries, TimeProvider clock)
{
    public async Task<LibrarySummary> GetAsync(CancellationToken ct = default)
    {
        var scope = await library.GetVisibleEntryIdsAsync(ct: ct);
        var summary = await queries.GetAsync(scope, DateOnly.FromDateTime(clock.GetLocalNow().DateTime), ct);
        var bytes = await store.GetFolderBytesAsync(ct);
        var folders = new List<SummaryFolder>();
        foreach (var root in await roots.ListAsync(ct))
        {
            var books = await queries.CountBooksAsync(await library.GetVisibleEntryIdsAsync(root.Id, ct), ct);
            folders.Add(new SummaryFolder(root.Id, root.Path, books, bytes.GetValueOrDefault(root.Id), root.Availability == SourceRootAvailability.Offline));
        }
        var work = await store.GetUserWorkAsync(ct);
        return summary with
        {
            Folders = [.. folders.OrderByDescending(f => f.Books).ThenBy(f => f.Id)],
            Bytes = folders.Sum(f => f.Bytes),
            Collections = work.Collections,
            Binders = work.Binders,
            BinderItems = work.BinderItems,
            Notes = work.Notes,
            SavedSearches = work.SavedSearches,
        };
    }

    /// <summary>The books and their size alone, for the line on Home that leads here.</summary>
    public async Task<(long Books, long Bytes)> GetTotalsAsync(CancellationToken ct = default)
    {
        var books = await queries.CountBooksAsync(await library.GetVisibleEntryIdsAsync(ct: ct), ct);
        return (books, (await store.GetFolderBytesAsync(ct)).Values.Sum());
    }
}

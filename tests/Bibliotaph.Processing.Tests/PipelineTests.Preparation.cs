using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 3a: hearts and opens reach the Library through index.db, and come back after it is rebuilt.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task Hearts_and_opens_are_projected_at_once_and_again_after_a_rebuild()
    {
        Copy(pdfs.KnownText, "Adventures/Known Text.pdf");
        Copy(pdfs.Scanned, "Scans/Goblin Scan.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(2, cards.Count);
        var (known, scan) = (cards[0], cards[1]);
        var favorites = new FavoritesService(_favorites, _entries, _projector);
        var reading = new ReadingService(_reading, _projector);
        var projected = new List<EntryId>();
        _projector.Projected += (_, ids) => projected.AddRange(ids);

        Assert.Equal([known.EntryId], await favorites.SetAsync([known.EntryId], favorite: true, Ct));
        Assert.Equal(known.EntryId, await favorites.GetCardAsync(known.DocumentId, Ct));
        Assert.Equal(scan.EntryId, await reading.RecordOpenAsync(scan.DocumentId, Ct));
        await reading.SavePositionAsync(scan.DocumentId, 1, Ct);

        Assert.Equal([known.EntryId, scan.EntryId], projected);
        Assert.Equal([known.EntryId], (await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct)).Select(c => c.EntryId));
        Assert.Equal([known.EntryId], (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("favorite:yes")), new LibraryFilter(), ct: Ct)).Select(c => c.EntryId));
        var opened = Assert.Single(await search.ListAsync(new LibraryFilter(Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), ct: Ct));
        Assert.Equal(scan.EntryId, opened.EntryId);
        Assert.NotNull(opened.OpenedUtc);
        Assert.Equal(1, await reading.GetPositionAsync(scan.DocumentId, Ct));

        // index.db is derived: emptied and projected again, the marks come back from catalog.db.
        await new IndexStore(_writer).SetScopesAsync(null, [], Ct);
        await new IndexStore(_writer).SetOpenedAsync(null, new Dictionary<EntryId, DateTime>(), Ct);
        Assert.Empty(await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct));
        await _projector.ProjectAllAsync(Ct);
        Assert.True((await search.ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.EntryId == known.EntryId).Favorite);
        Assert.Equal([scan.EntryId], (await search.ListAsync(new LibraryFilter(OnlyOpened: true), ct: Ct)).Select(c => c.EntryId));

        await favorites.SetAsync([known.EntryId], favorite: false, Ct);
        Assert.Empty(await search.ListAsync(new LibraryFilter(Group: ScopeKeys.Favorites), ct: Ct));
    }
}

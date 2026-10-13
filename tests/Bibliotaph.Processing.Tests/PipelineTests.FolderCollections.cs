using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Collections from folders through the real pipeline: which folders are proposed in Needs review, Accept with Undo,
/// nested folders as nested collections, Not now and Never, and a collection that follows its folder until it is
/// changed by hand.
/// </summary>
public sealed partial class PipelineTests
{
    ReviewService FolderReview() => new(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
        new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, folders: _folderCollections);

    static async Task<IReadOnlyList<FolderCollectionProposal>> FolderCardsAsync(ReviewService review) => (await review.GetQueueAsync(Ct)).Folders!;

    /// <summary>The books index.db has in a collection's scope, which a scoped Library and <c>collection:</c> read.</summary>
    long Scoped(long collectionId)
    {
        using var c = _index.OpenRead();
        return c.ExecuteScalar<long>("SELECT count(*) FROM entry_scope WHERE scope = @scope", new { scope = ScopeKeys.Collection(collectionId) });
    }

    async Task<CollectionInfo?> CollectionAsync(string name) => (await _collections.ListAsync(Ct)).SingleOrDefault(c => c.Name == name);

    [Fact]
    public async Task A_folder_of_three_books_is_proposed_as_a_collection_and_one_inside_it_as_a_collection_inside_that()
    {
        foreach (var (name, width) in new[] { ("A", 700), ("B", 701), ("C", 702) }) Image($"Ashen Vale/{name}.png", width);
        foreach (var (name, width) in new[] { ("Ford", 703), ("Mill", 704), ("Tower", 705) }) Image($"Ashen Vale/Fordwatch/{name}.png", width);
        // Two books aren't enough, and folders named only by vocabulary terms sort by what the fields already say.
        Image("Lantern Coast/One.png", 706);
        Image("Lantern Coast/Two.png", 707);
        foreach (var (name, width) in new[] { ("X", 708), ("Y", 709), ("Z", 710) }) Image($"D&D 5e/{name}.png", width);
        foreach (var (name, width) in new[] { ("X", 711), ("Y", 712), ("Z", 713) }) Image($"Pathfinder Adventures/{name}.png", width);
        foreach (var (name, width) in new[] { ("X", 714), ("Y", 715), ("Z", 716) }) Image($"{name}.png", width);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var review = FolderReview();

        var proposed = await FolderCardsAsync(review);
        Assert.Equal(["Ashen Vale", "Fordwatch"], proposed.Select(p => p.Name));
        Assert.All(proposed, p => Assert.Equal(3, p.Books.Count));
        Assert.Null(proposed[0].ParentName);
        Assert.Equal(Path.Combine(_library, "Ashen Vale"), proposed[0].FullPath);
        var waiting = await review.CountAsync(Ct);

        // Accept: the collection, its books projected into its scope, and the folder inside it now proposed inside it.
        var undoVale = await review.AnswerFolderAsync(proposed[0], CollectionAnswer.Accept, Ct);
        Assert.NotNull(undoVale);
        var vale = await CollectionAsync("Ashen Vale");
        Assert.NotNull(vale);
        Assert.Equal((null, 3), (vale.ParentId, vale.OwnCount));
        Assert.Equal(3, Scoped(vale.Id));
        Assert.Equal(waiting - 1, await review.CountAsync(Ct));
        var maps = Assert.Single(await FolderCardsAsync(review));
        Assert.Equal(("Fordwatch", "Ashen Vale"), (maps.Name, maps.ParentName));

        Assert.NotNull(await review.AnswerFolderAsync(maps, CollectionAnswer.Accept, Ct));
        var inner = await CollectionAsync("Fordwatch");
        Assert.Equal(vale.Id, inner!.ParentId);
        // The outer collection's scope holds the inner one's books too.
        Assert.Equal(6, Scoped(vale.Id));

        // Undo takes the collection away and asks again.
        await undoVale();
        Assert.Null(await CollectionAsync("Ashen Vale"));
        Assert.Equal(["Ashen Vale"], (await FolderCardsAsync(review)).Select(p => p.Name));
        Assert.Null((await CollectionAsync("Fordwatch"))!.ParentId);

        // Made after the folder inside it, the outer collection takes the inner one in; Undo puts it back at the top.
        var again = await review.AnswerFolderAsync((await FolderCardsAsync(review))[0], CollectionAnswer.Accept, Ct);
        vale = await CollectionAsync("Ashen Vale");
        Assert.Equal(vale!.Id, (await CollectionAsync("Fordwatch"))!.ParentId);
        await again!();
        Assert.Null((await CollectionAsync("Fordwatch"))!.ParentId);
    }

    [Fact]
    public async Task Never_is_remembered_by_the_folder_under_its_library_folder_and_Not_now_waits_until_the_list_is_made_again()
    {
        foreach (var (name, width) in new[] { ("A", 720), ("B", 721), ("C", 722) }) Image($"Ashen Vale/{name}.png", width);
        foreach (var (name, width) in new[] { ("A", 723), ("B", 724), ("C", 725) }) Image($"Lantern Coast/{name}.png", width);
        foreach (var (name, width) in new[] { ("A", 726), ("B", 727), ("C", 728) }) Image($"Wintermoot Road/{name}.png", width);
        var root = await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var review = FolderReview();
        var proposed = await FolderCardsAsync(review);
        Assert.Equal(["Ashen Vale", "Lantern Coast", "Wintermoot Road"], proposed.Select(p => p.Name));
        var waiting = await review.CountAsync(Ct);

        // Books already together in a collection made by hand aren't asked about.
        var cards = await CardsAsync();
        var road = await _collectionsService.CreateAsync("Road trip", ct: Ct);
        await _collectionsService.AddAsync(road.Id, [.. new[] { "A", "B", "C" }.Select(n => cards[$"Wintermoot Road/{n}.png"].Entry)], Ct);
        Assert.Equal(["Ashen Vale", "Lantern Coast"], (await FolderCardsAsync(review)).Select(p => p.Name));

        // Not now: gone from the count and the list until the list is made again.
        var later = await review.AnswerFolderAsync(proposed[1], CollectionAnswer.NotNow, Ct);
        Assert.Equal(waiting - 2, await review.CountAsync(Ct));
        Assert.Equal(["Ashen Vale", "Lantern Coast"], (await FolderCardsAsync(review)).Select(p => p.Name));
        await review.AnswerFolderAsync(proposed[1], CollectionAnswer.NotNow, Ct);
        await later!();
        Assert.Equal(waiting - 1, await review.CountAsync(Ct));

        // Never: not asked again, Undo asks again, and Never again holds once the library folder has moved for good.
        var never = await review.AnswerFolderAsync(proposed[0], CollectionAnswer.Never, Ct);
        Assert.Equal(["Lantern Coast"], (await FolderCardsAsync(review)).Select(p => p.Name));
        await never!();
        Assert.Equal(["Ashen Vale", "Lantern Coast"], (await FolderCardsAsync(review)).Select(p => p.Name));
        await review.AnswerFolderAsync(proposed[0], CollectionAnswer.Never, Ct);
        Assert.Null(await CollectionAsync("Ashen Vale"));

        var moved = Path.Combine(_dir, "Moved Library");
        Directory.Move(_library, moved);
        Assert.True(await _roots.RelocateAsync(root.Id, moved, Ct));
        _service.RequestScan();
        await SettleAsync();
        var after = Assert.Single(await FolderCardsAsync(review));
        Assert.Equal(("Lantern Coast", Path.Combine(moved, "Lantern Coast")), (after.Name, after.FullPath));
    }

    [Fact]
    public async Task A_collection_made_from_a_folder_gains_the_books_that_land_there_until_its_books_are_changed_by_hand()
    {
        foreach (var (name, width) in new[] { ("A", 730), ("B", 731), ("C", 732) }) Image($"Ashen Vale/{name}.png", width);
        Image("Elsewhere/Lantern.png", 733);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var review = FolderReview();
        await review.AnswerFolderAsync(Assert.Single(await FolderCardsAsync(review)), CollectionAnswer.Accept, Ct);
        var vale = (await CollectionAsync("Ashen Vale"))!;
        Assert.True(Assert.Single(await _folderCollectionStore.ListAsync(Ct)).Follows);

        // A new book in the folder joins, and is projected into the collection's scope.
        Image("Ashen Vale/D.png", 734);
        _service.RequestScan();
        await SettleUntilAsync(async () => (await CollectionAsync("Ashen Vale"))!.OwnCount == 4);
        Assert.Equal(4, Scoped(vale.Id));

        // Deleting the collection and bringing it back keeps it following its folder.
        var deleted = await _collectionsService.DeleteAsync(vale.Id, Ct);
        Assert.Null(Assert.Single(await _folderCollectionStore.ListAsync(Ct)).CollectionId);
        Assert.Empty(await FolderCardsAsync(review));
        await _collectionsService.RestoreAsync(deleted!, Ct);
        Assert.True(Assert.Single(await _folderCollectionStore.ListAsync(Ct)).Follows);

        // A book taken out by hand: the collection stops following, so the next new book stays out.
        var cards = await CardsAsync();
        await _collectionsService.RemoveAsync(vale.Id, [cards["Ashen Vale/A.png"].Entry], Ct);
        Assert.Equal(FolderCollectionState.Unlinked, Assert.Single(await _folderCollectionStore.ListAsync(Ct)).State);
        Image("Ashen Vale/E.png", 735);
        _service.RequestScan();
        await SettleUntilAsync(async () => (await CardsAsync()).ContainsKey("Ashen Vale/E.png"));
        await _folderCollections.FollowAsync(Ct);
        Assert.Equal(3, (await CollectionAsync("Ashen Vale"))!.OwnCount);
        // Nor is the folder asked about again.
        Assert.Empty(await FolderCardsAsync(review));
    }

    [Fact]
    public async Task Adding_a_book_by_hand_stops_a_collection_following_its_folder_but_what_the_folder_brings_does_not()
    {
        foreach (var (name, width) in new[] { ("A", 740), ("B", 741), ("C", 742) }) Image($"Ashen Vale/{name}.png", width);
        Image("Elsewhere/Lantern.png", 743);
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var review = FolderReview();
        await review.AnswerFolderAsync(Assert.Single(await FolderCardsAsync(review)), CollectionAnswer.Accept, Ct);
        var vale = (await CollectionAsync("Ashen Vale"))!;

        Assert.Empty(await _collectionsService.JoinAsync(vale.Id, [.. (await CardsAsync()).Where(c => c.Key.StartsWith("Ashen Vale/", StringComparison.Ordinal)).Select(c => c.Value.Entry)], Ct));
        Assert.Equal(0, await _folderCollections.FollowAsync(Ct));
        Assert.True(Assert.Single(await _folderCollectionStore.ListAsync(Ct)).Follows);

        await _collectionsService.AddAsync(vale.Id, [(await CardsAsync())["Elsewhere/Lantern.png"].Entry], Ct);
        Assert.Equal(FolderCollectionState.Unlinked, Assert.Single(await _folderCollectionStore.ListAsync(Ct)).State);
        Assert.Equal(4, Scoped(vale.Id));
    }
}

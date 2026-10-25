using System.Windows;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Every collection, kept in memory for the pages and menus: their tree, their names as paths ("Campaign › Maps"),
/// the scope each opens the Library with, and the recent ones a book's menu offers. It reloads after any collection
/// changes, on the UI thread.
/// </summary>
public sealed class CollectionDirectory : ICollectionChoices
{
    readonly CollectionsService _collections;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly ILogger<CollectionDirectory> _log;
    Dictionary<long, CollectionInfo> _byId = [];

    public CollectionDirectory(CollectionsService collections, PinsService pins, LibraryStore library, LibraryQueries queries, CoverImages covers,
        ILogger<CollectionDirectory> log)
    {
        _collections = collections;
        _library = library;
        _queries = queries;
        _covers = covers;
        _log = log;
        collections.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(LoadAsync);
        // A collection pinned or unpinned moves among the others.
        pins.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(LoadAsync);
    }

    /// <summary>Raised on the UI thread after the collections were reloaded.</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<CollectionInfo> All => _byId.Values;

    public bool Any => _byId.Count > 0;

    /// <summary>A book menu's Add to collection items, as of the last reload.</summary>
    public IReadOnlyList<CollectionMenuChoice> MenuChoices { get; private set; } = [];

    public async Task LoadAsync()
    {
        try
        {
            var all = await Task.Run(() => _collections.ListAsync());
            _byId = all.ToDictionary(c => c.Id);
            MenuChoices = MenuChoicesFor(all, PathOf);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the collections failed");
        }
    }

    /// <summary>
    /// The Add to collection items for these collections: the ones used last, by path, then New, then Choose when
    /// there are more than those. <paramref name="pathOf"/> names each.
    /// </summary>
    public static IReadOnlyList<CollectionMenuChoice> MenuChoicesFor(IReadOnlyList<CollectionInfo> all, Func<long, string> pathOf) =>
    [
        .. CollectionText.Recent(all, pathOf).Select(c => new CollectionMenuChoice(CollectionMenuKind.Collection, c.Id, CollectionText.AddItem(c.Name), pathOf(c.Id))),
        new(CollectionMenuKind.New, null, CollectionText.NewItem, null),
        .. CollectionText.OffersChoose(all.Count) ? [new CollectionMenuChoice(CollectionMenuKind.Choose, null, CollectionText.ChooseItem, null)] : Array.Empty<CollectionMenuChoice>(),
    ];

    public CollectionInfo? Find(long collectionId) => _byId.GetValueOrDefault(collectionId);

    /// <summary>The collections a book was added to itself, for the chips in its details.</summary>
    public Task<IReadOnlyList<long>> CollectionsOfAsync(EntryId entryId) => _collections.GetForEntryAsync(entryId);

    /// <summary>The collections inside <paramref name="parentId"/>, or at the top, by name. Only the sidebar keeps the pinned ones' own order.</summary>
    public IReadOnlyList<CollectionInfo> ChildrenOf(long? parentId) => CollectionText.ChildrenOf(_byId.Values, parentId);

    /// <summary>"Campaign › Maps": the collection's name after those of the collections it sits in.</summary>
    public string PathOf(long collectionId) => CollectionText.PathOf(_byId, collectionId);

    /// <summary>
    /// Every collection, depth first, for a picker: each after the one it sits in. <paramref name="excluding"/> leaves out
    /// a collection and everything under it, for Move to….
    /// </summary>
    public IReadOnlyList<CollectionChoice> Tree(long? excluding = null) =>
        [.. CollectionText.Tree(_byId.Values, excluding).Select(t => new CollectionChoice(t.Collection.Id, t.Collection.Name, PathOf(t.Collection.Id), t.Depth))];

    /// <summary>What the Library shows for a collection: its books and its sub-collections' books.</summary>
    public LibraryScope ScopeFor(CollectionInfo collection) => new(ScopeKeys.Collection(collection.Id), collection.Name,
        collection.Description ?? CollectionText.Subtitle, CollectionText.EmptyTitle, CollectionText.EmptyMessage)
    {
        CollectionId = collection.Id,
        Path = PathOf(collection.Id),
    };

    /// <summary>
    /// The cards for these collections: each with how many books it holds, sub-collections included, and the covers of
    /// the two added last. Only books in the library's shown folders count.
    /// </summary>
    public async Task<IReadOnlyList<CollectionCardViewModel>> CardsAsync(IReadOnlyList<CollectionInfo> collections)
    {
        if (collections.Count == 0) return [];
        var visible = await _library.GetVisibleEntryIdsAsync();
        var counts = await Task.Run(() => _queries.CountGroupsAsync("collection:", visible));
        var cards = new List<CollectionCardViewModel>();
        foreach (var collection in collections)
        {
            var key = ScopeKeys.Collection(collection.Id);
            var covers = await Task.Run(() => _queries.ListAsync(new LibraryFilter(visible, Sort: LibrarySort.RecentlyAdded, Group: key), limit: 2));
            var books = counts.GetValueOrDefault(key);
            var children = _byId.Values.Count(c => c.ParentId == collection.Id);
            cards.Add(new CollectionCardViewModel(collection, CollectionText.Count(books, children), [.. covers.Select(e => new LibraryItemViewModel(e, _covers))]));
        }
        return cards;
    }
}

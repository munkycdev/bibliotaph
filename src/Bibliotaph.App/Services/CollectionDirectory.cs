using System.Globalization;
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
/// Every collection, kept in memory for the pages and menus (slice 3 plan, choices 8 to 10): their tree, their names
/// as paths ("Campaign › Maps"), the scope each opens the Library with, and the recent ones a book's menu offers. It
/// reloads after any collection changes, on the UI thread.
/// </summary>
public sealed class CollectionDirectory
{
    /// <summary>How many recent collections a book's menu offers before New collection… and Choose….</summary>
    const int RecentCount = 4;

    readonly CollectionsService _collections;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly ILogger<CollectionDirectory> _log;
    Dictionary<long, CollectionInfo> _byId = [];

    public CollectionDirectory(CollectionsService collections, LibraryStore library, LibraryQueries queries, CoverImages covers, ILogger<CollectionDirectory> log)
    {
        _collections = collections;
        _library = library;
        _queries = queries;
        _covers = covers;
        _log = log;
        collections.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(LoadAsync);
    }

    /// <summary>Raised on the UI thread after the collections were reloaded.</summary>
    public event EventHandler? Changed;

    public IReadOnlyCollection<CollectionInfo> All => _byId.Values;

    public bool Any => _byId.Count > 0;

    public async Task LoadAsync()
    {
        try
        {
            var all = await Task.Run(() => _collections.ListAsync());
            _byId = all.ToDictionary(c => c.Id);
            CollectionMenu.Choices =
            [
                .. all.OrderByDescending(c => c.UsedUtc).Take(RecentCount)
                    .Select(c => new CollectionMenuChoice(CollectionMenuKind.Collection, c.Id, $"Add to {c.Name}", PathOf(c.Id))),
                new(CollectionMenuKind.New, null, "Add to a new collection…", null),
                .. all.Count > RecentCount ? [new CollectionMenuChoice(CollectionMenuKind.Choose, null, "Add to another collection…", null)] : Array.Empty<CollectionMenuChoice>(),
            ];
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the collections failed");
        }
    }

    public CollectionInfo? Find(long collectionId) => _byId.GetValueOrDefault(collectionId);

    /// <summary>The collections a book was added to itself, for the chips in its details.</summary>
    public Task<IReadOnlyList<long>> CollectionsOfAsync(EntryId entryId) => _collections.GetForEntryAsync(entryId);

    /// <summary>The collections inside <paramref name="parentId"/>, or at the top: pinned first, then by name (choice 9).</summary>
    public IReadOnlyList<CollectionInfo> ChildrenOf(long? parentId) =>
        [.. _byId.Values.Where(c => c.ParentId == parentId).OrderByDescending(c => c.Pinned).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The pinned collections wherever they sit, for Home, by name.</summary>
    public IReadOnlyList<CollectionInfo> Pinned => [.. _byId.Values.Where(c => c.Pinned).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>"Campaign › Maps": the collection's name after those of the collections it sits in.</summary>
    public string PathOf(long collectionId)
    {
        var names = new List<string>();
        for (long? at = collectionId; at is { } id && _byId.TryGetValue(id, out var c) && names.Count <= _byId.Count; at = c.ParentId) names.Add(c.Name);
        names.Reverse();
        return string.Join(" › ", names);
    }

    /// <summary>
    /// Every collection, depth first, for a picker: each after the one it sits in. <paramref name="excluding"/> leaves out
    /// a collection and everything under it, for Move to….
    /// </summary>
    public IReadOnlyList<CollectionChoice> Tree(long? excluding = null)
    {
        var result = new List<CollectionChoice>();
        void Add(long? parent, int depth)
        {
            foreach (var child in ChildrenOf(parent).Where(c => c.Id != excluding))
            {
                result.Add(new CollectionChoice(child.Id, child.Name, PathOf(child.Id), depth));
                if (depth < 32) Add(child.Id, depth + 1);
            }
        }
        Add(null, 0);
        return result;
    }

    /// <summary>What the Library shows for a collection: its books and its sub-collections' books.</summary>
    public LibraryScope ScopeFor(CollectionInfo collection) => new(ScopeKeys.Collection(collection.Id), collection.Name,
        collection.Description ?? "A collection. The books in its sub-collections show here too.",
        "Nothing in this collection yet.", "Add books from the Library: right-click a book, or choose Select and tick several, then add them to this collection.")
    {
        CollectionId = collection.Id,
        Path = PathOf(collection.Id),
    };

    /// <summary>
    /// The cards for these collections (choice 9): each with how many books it holds, sub-collections included, and
    /// the covers of the two added last. Only books in the library's shown folders count.
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
            cards.Add(new CollectionCardViewModel(collection, Count(books, children), [.. covers.Select(e => new LibraryItemViewModel(e, _covers))]));
        }
        return cards;
    }

    static string Count(long books, int children)
    {
        var parts = new List<string> { books == 0 ? "No books yet" : books == 1 ? "1 book" : $"{books.ToString("N0", CultureInfo.CurrentCulture)} books" };
        if (children > 0) parts.Add(children == 1 ? "1 collection" : $"{children.ToString("N0", CultureInfo.CurrentCulture)} collections");
        return string.Join(" · ", parts);
    }
}

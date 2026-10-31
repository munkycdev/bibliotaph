using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// What pages do with collections: adding books to one, new or picked, taking
/// them out, and making, renaming, moving and deleting collections. Each answers with the note to show and its Undo,
/// or null when the dialog was cancelled.
/// </summary>
public sealed class CollectionActions(ApiClient api, CollectionDialogs dialogs)
{
    /// <summary>A note for the page shown next, as after a collection was deleted from its own page.</summary>
    public ActionResult? Pending { get; set; }

    /// <summary>One of a book menu's Add to collection items, for these books: "Ghost Tower" or "3 books" in the note.</summary>
    public async Task<ActionResult?> AddAsync(IReadOnlyList<long> books, string what, CollectionMenuItem item)
    {
        if (books.Count == 0) return null;
        return await TryAsync(async () =>
        {
            switch (item.Kind)
            {
                case "collection" when item.Id is { } id:
                    return await api.AddToCollectionAsync(id, new CollectionBooks(books, what));
                case "new":
                    if (await dialogs.NameAsync("Add to a new collection", "Create and add") is not { } named) return null;
                    var created = await api.CreateCollectionAsync(new CollectionName(named.Name, named.Description));
                    return await api.AddToCollectionAsync(created.Collection.Id, new CollectionBooks(books, what));
                case "choose":
                    if (await dialogs.PickAsync($"Add {what} to a collection", await TreeAsync()) is not { Id: { } picked }) return null;
                    return await api.AddToCollectionAsync(picked, new CollectionBooks(books, what));
                default:
                    return null;
            }
        });
    }

    public Task<ActionResult?> RemoveAsync(IReadOnlyList<long> books, string what, long collectionId) =>
        TryAsync(async () => await api.RemoveFromCollectionAsync(collectionId, new CollectionBooks(books, what)));

    /// <summary>New collection: at the top, or inside another. Returns the one made, for the page to open.</summary>
    public async Task<CollectionNode?> CreateAsync(CollectionNode? parent)
    {
        if (await dialogs.NameAsync(parent is null ? "New collection" : $"New collection in {parent.Name}", "Create") is not { } named) return null;
        try
        {
            return (await api.CreateCollectionAsync(new CollectionName(named.Name, named.Description, parent?.Id))).Collection;
        }
        catch (ApiProblemException e)
        {
            Pending = new ActionResult(e.Message);
            return null;
        }
    }

    public async Task<ActionResult?> RenameAsync(CollectionNode collection)
    {
        if (await dialogs.NameAsync("Rename collection", "Save", collection.Name, collection.Description) is not { } named) return null;
        return await TryAsync(async () => await api.RenameCollectionAsync(collection.Id, new CollectionName(named.Name, named.Description)));
    }

    /// <summary>Move to…: into another collection, or to the top, but never into itself or one inside it.</summary>
    public async Task<ActionResult?> MoveAsync(CollectionNode collection)
    {
        var tree = await TreeAsync();
        var inside = tree.SkipWhile(c => c.Id != collection.Id).ToList();
        var depth = inside.FirstOrDefault()?.Depth ?? 0;
        // The collection and those under it: everything after it that sits deeper, up to the next at its depth or above.
        var below = inside.Take(1).Concat(inside.Skip(1).TakeWhile(c => c.Depth > depth)).Select(c => c.Id).ToHashSet();
        List<CollectionChoice> choices = [.. tree.Where(c => !below.Contains(c.Id) && c.Id != collection.ParentId)];
        if (collection.ParentId is not null) choices.Insert(0, new CollectionChoice(null, "Collections (the top level)", "Collections", 0));
        if (await dialogs.PickAsync($"Move {collection.Name} to", choices) is not { } choice) return null;
        return await TryAsync(async () => await api.MoveCollectionAsync(collection.Id, new CollectionMove(choice.Id)));
    }

    /// <summary>Deletes a collection, never its books.</summary>
    public Task<ActionResult?> DeleteAsync(long collectionId) => TryAsync(async () => await api.DeleteCollectionAsync(collectionId));

    async Task<IReadOnlyList<CollectionChoice>> TreeAsync()
    {
        var list = await api.CollectionsAsync();
        return [.. list.Tree.Select((c, i) => new CollectionChoice(c.Id, c.Name, c.Path, list.Depths[i]))];
    }

    static async Task<ActionResult?> TryAsync(Func<Task<ActionResult?>> change)
    {
        try
        {
            return await change();
        }
        catch (ApiProblemException e)
        {
            return new ActionResult(e.Message);
        }
    }
}

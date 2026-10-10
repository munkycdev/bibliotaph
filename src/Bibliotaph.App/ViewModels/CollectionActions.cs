using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// What a page does with collections (slice 3 plan, choices 8 to 10): adding books to one, new or picked, taking them
/// out, and making, renaming, moving, pinning and deleting collections. Each change says what it did in a note with
/// Undo, until the next change or until the page is left. One per page; the page shows <see cref="Dialog"/> over
/// itself and refreshes on <see cref="Done"/>.
/// </summary>
public sealed partial class CollectionActions(CollectionsService collections, CollectionDirectory directory, ILogger<CollectionActions> log) : ObservableObject
{
    /// <summary>Naming or picking a collection, while it is open over the page.</summary>
    [ObservableProperty]
    public partial CollectionDialogViewModel? Dialog { get; private set; }

    /// <summary>"Added 3 books to Maps.", beside its Undo.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial Func<Task>? UndoAction { get; private set; }

    public bool HasUndo => UndoAction is not null;

    /// <summary>Raised on the UI thread after a change, so the page shows it.</summary>
    public event EventHandler? Done;

    /// <summary>Raised after New collection made one, with it, so a page can open it.</summary>
    public event EventHandler<CollectionInfo>? Created;

    public CollectionDirectory Directory { get; } = directory;

    /// <summary>One of a menu's Add to collection items, for these books: "Ghost Tower" or "3 books" in the note.</summary>
    public void Add(IReadOnlyList<EntryId> books, string what, CollectionMenuChoice choice)
    {
        if (books.Count == 0) return;
        switch (choice.Kind)
        {
            case CollectionMenuKind.Collection when choice.CollectionId is { } id:
                _ = AddToAsync(books, what, id);
                break;
            case CollectionMenuKind.New:
                Open(CollectionDialogViewModel.Naming("Add to a new collection", "Create and add"), async result =>
                {
                    var created = await Run(() => collections.CreateAsync(result.Name, null, result.Description), "Making the collection failed");
                    if (created is not null) await AddToAsync(books, what, created.Id);
                });
                break;
            case CollectionMenuKind.Choose:
                Open(CollectionDialogViewModel.Picking($"Add {what} to a collection", Directory.Tree()), async result =>
                {
                    if (result.Choice?.Id is { } picked) await AddToAsync(books, what, picked);
                });
                break;
        }
    }

    async Task AddToAsync(IReadOnlyList<EntryId> books, string what, long collectionId)
    {
        var added = await Run(() => collections.AddAsync(collectionId, books), "Adding to a collection failed");
        if (added is null) return;
        var name = Directory.Find(collectionId)?.Name ?? "the collection";
        await FinishAsync(added.Count == 0 ? $"{Capital(what)} {(books.Count == 1 ? "is" : "are")} in {name} already." : $"Added {what} to {name}.",
            added.Count == 0 ? null : () => collections.RemoveAsync(collectionId, added));
    }

    /// <summary>Takes books out of a collection: Remove from collection in Select mode, or a chip's × in the details.</summary>
    public async Task RemoveAsync(IReadOnlyList<EntryId> books, string what, long collectionId)
    {
        var removed = await Run(() => collections.RemoveAsync(collectionId, books), "Removing from a collection failed");
        if (removed is null) return;
        var name = Directory.Find(collectionId)?.Name ?? "the collection";
        await FinishAsync(removed.Count == 0 ? $"{Capital(what)} {(books.Count == 1 ? "was" : "were")} only in {name} through a collection inside it."
            : $"Removed {what} from {name}.", removed.Count == 0 ? null : () => collections.AddAsync(collectionId, removed));
    }

    /// <summary>New collection: at the top, or inside <paramref name="parentId"/>.</summary>
    public void Create(long? parentId)
    {
        var heading = parentId is { } parent ? $"New collection in {Directory.Find(parent)?.Name}" : "New collection";
        Open(CollectionDialogViewModel.Naming(heading, "Create"), async result =>
        {
            var created = await Run(() => collections.CreateAsync(result.Name, parentId, result.Description), "Making the collection failed");
            if (created is null) return;
            await FinishAsync(null, null);
            Created?.Invoke(this, created);
        });
    }

    public void Rename(CollectionInfo collection) =>
        Open(CollectionDialogViewModel.Naming("Rename collection", "Save", collection.Name, collection.Description), async result =>
        {
            if (await Run(async () => { await collections.RenameAsync(collection.Id, result.Name, result.Description); return true; }, "Renaming the collection failed"))
                await FinishAsync($"Renamed {collection.Name} to {result.Name}.", () => collections.RenameAsync(collection.Id, collection.Name, collection.Description));
        });

    /// <summary>Move to…: into another collection, or to the top, but never into itself or one inside it.</summary>
    public void Move(CollectionInfo collection)
    {
        var top = new CollectionChoice(null, "Collections (the top level)", "Collections", 0);
        var choices = Directory.Tree(excluding: collection.Id).Where(c => c.Id != collection.ParentId).ToList();
        if (collection.ParentId is not null) choices.Insert(0, top);
        Open(CollectionDialogViewModel.Picking($"Move {collection.Name} to", choices), async result =>
        {
            if (result.Choice is not { } choice) return;
            if (!await Run(async () => { await collections.MoveAsync(collection.Id, choice.Id); return true; }, "Moving the collection failed")) return;
            var where = choice.Id is null ? "the top level" : choice.Name;
            await FinishAsync($"Moved {collection.Name} to {where}.", () => collections.MoveAsync(collection.Id, collection.ParentId));
        });
    }

    public async Task SetPinnedAsync(CollectionInfo collection, bool pinned)
    {
        if (await Run(async () => { await collections.SetPinnedAsync(collection.Id, pinned); return true; }, "Pinning the collection failed"))
            await FinishAsync(pinned ? $"Pinned {collection.Name}. It shows first on Collections, and on Home." : $"Unpinned {collection.Name}.", null);
    }

    /// <summary>Deletes a collection, never its books; its sub-collections move up a level. Returns what Undo needs.</summary>
    public async Task<CollectionDeletion?> DeleteAsync(CollectionInfo collection)
    {
        var deleted = await Run(() => collections.DeleteAsync(collection.Id), "Deleting the collection failed");
        if (deleted is not null) await FinishAsync(null, null);
        return deleted;
    }

    /// <summary>The note after a delete, on the page shown after it, with Undo bringing the collection back.</summary>
    public void ShowDeleted(CollectionDeletion deleted)
    {
        Message = $"Deleted {deleted.Name}. Its books are still in your library{(deleted.Children.Count > 0 ? ", and its collections moved up a level" : "")}.";
        UndoAction = () => collections.RestoreAsync(deleted);
    }

    [RelayCommand(CanExecute = nameof(HasUndo))]
    async Task Undo()
    {
        if (UndoAction is not { } undo) return;
        UndoAction = null;
        Message = "Undoing…";
        if (await Run(async () => { await undo(); return true; }, "Undoing a collection change failed"))
        {
            Message = null;
            await FinishAsync(null, null);
        }
        else Message = "That couldn't be undone.";
    }

    /// <summary>Esc or Cancel closes the dialog; the note goes when the page is left.</summary>
    public void Close()
    {
        Dialog?.CancelCommand.Execute(null);
        Message = null;
        UndoAction = null;
    }

    void Open(CollectionDialogViewModel dialog, Func<CollectionDialogResult, Task> then)
    {
        dialog.Closed += async (_, result) =>
        {
            Dialog = null;
            if (result is not null) await then(result);
        };
        Dialog = dialog;
    }

    async Task FinishAsync(string? message, Func<Task>? undo)
    {
        Message = message;
        UndoAction = undo;
        await Directory.LoadAsync();
        Done?.Invoke(this, EventArgs.Empty);
    }

    async Task<T?> Run<T>(Func<Task<T>> action, string failure)
    {
        try
        {
            return await Task.Run(action);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Failure}", failure);
            Message = "That didn't work. The log has the details.";
            UndoAction = null;
            return default;
        }
    }

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// What a page does with session packs: adding books or pages to one, the current pack, another or a new one,
/// making, editing, duplicating and deleting packs, and naming sections. Each change says what it did in a note, with
/// Undo where there is something to undo, until the next change or until the page is left. One per page; the page
/// shows <see cref="Dialog"/> over itself.
/// </summary>
public sealed partial class SessionActions(SessionsService sessions, SessionDirectory directory, ILogger<SessionActions> log) : ObservableObject
{
    /// <summary>Naming a pack or a section, picking a pack, or Add pages…, while it is open over the page.</summary>
    [ObservableProperty]
    public partial SessionDialogViewModel? Dialog { get; private set; }

    /// <summary>"Added Warehouse ambush to The midnight bell.", beside its Undo.</summary>
    public UndoSlot Undo { get; } = new(log);

    /// <summary>Raised on the UI thread after a change, so the page shows it.</summary>
    public event EventHandler? Done;

    /// <summary>Raised after New session made one, with it, so a page can open it.</summary>
    public event EventHandler<SessionPackInfo>? Created;

    public SessionDirectory Directory { get; } = directory;

    /// <summary>One of a menu's Add to session items, for these books: "Ghost Tower" or "3 books" in the note.</summary>
    public void Add(IReadOnlyList<EntryId> books, string what, SessionMenuChoice choice)
    {
        if (books.Count == 0) return;
        IReadOnlyList<NewSessionItem> items = [.. books.Select(b => new NewSessionItem(b))];
        AddTo(choice, what, id => sessions.AddAsync(id, items));
    }

    /// <summary>
    /// Add page: one page of the open book, called after the bookmark it sits under, or a whole image, to the current
    /// pack or the one picked in the menu; before there is a pack, it asks for one first.
    /// </summary>
    public void AddPage(long documentId, int page, string what, SessionMenuChoice? choice = null, bool wholeFile = false) =>
        AddTo(choice ?? CurrentChoice(), what, async id =>
            await (wholeFile ? sessions.AddDocumentAsync(id, documentId) : sessions.AddPagesAsync(id, documentId, page, page)) is { } added ? [added] : []);

    /// <summary>
    /// Add this page in the online reader: the page of a book owned online at <paramref name="link"/>, labelled
    /// <paramref name="label"/>, to the current binder or the one picked in the menu.
    /// </summary>
    public void AddOnlinePage(EntryId book, string link, string? label, SessionMenuChoice? choice = null)
    {
        IReadOnlyList<NewSessionItem> items = [new NewSessionItem(book, Label: label, Link: link)];
        AddTo(choice ?? CurrentChoice(), "the page", id => sessions.AddAsync(id, items));
    }

    SessionMenuChoice CurrentChoice() =>
        Directory.Current is { } current ? new(SessionMenuKind.Session, current.Id, "") : new(SessionMenuKind.New, null, "");

    void AddTo(SessionMenuChoice choice, string what, Func<long, Task<IReadOnlyList<long>>> add)
    {
        switch (choice.Kind)
        {
            case SessionMenuKind.Session when choice.PackId is { } id:
                AddToAsync(id, what, () => add(id)).FireAndForget();
                break;
            case SessionMenuKind.New:
                NewThen(BinderText.NewHeading, BinderText.NewAction, pack => AddToAsync(pack.Id, what, () => add(pack.Id)));
                break;
            case SessionMenuKind.Choose:
                Open(SessionDialogViewModel.Picking(BinderText.PickHeading(what), Directory.All), async result =>
                {
                    if (result.PackId is { } picked) await AddToAsync(picked, what, () => add(picked));
                });
                break;
        }
    }

    /// <summary>
    /// Add pages…: From and To as typed (prefilled from the page in view or the selection), a label (from the
    /// bookmark there), and which pack and section.
    /// </summary>
    public async Task AddPagesAsync(long documentId, string from, string to, int firstPage, Func<string, int?> findPage)
    {
        if (Directory.Current is null)
        {
            NewThen(BinderText.NewHeading, "Create", _ => AddPagesAsync(documentId, from, to, firstPage, findPage));
            return;
        }
        var label = await Run(() => sessions.SuggestLabelAsync(documentId, firstPage), "Finding the page's bookmark failed") ?? "";
        Open(SessionDialogViewModel.Pages(Directory.All, from, to, label, findPage, id => Task.Run(async () => (await sessions.GetAsync(id))?.Sections ?? [])),
            async result =>
            {
                if (result.PackId is not { } pack) return;
                var what = result.Label ?? (result.FirstPage == result.LastPage ? "the page" : "the pages");
                await AddToAsync(pack, what, async () =>
                    await sessions.AddPagesAsync(pack, documentId, result.FirstPage, result.LastPage, result.Label, result.SectionId) is { } id ? [id] : []);
            });
    }

    async Task AddToAsync(long packId, string what, Func<Task<IReadOnlyList<long>>> add)
    {
        var added = await Run(add, "Adding to a session failed");
        if (added is null) return;
        var name = Directory.Find(packId)?.Title ?? "the binder";
        await FinishAsync(added.Count == 0 ? BinderText.NotAdded : BinderText.Added(what, name),
            added.Count == 0 ? null : async () => await sessions.RemoveItemsAsync(added));
    }

    /// <summary>New session: a title and a date, then the pack opens (<see cref="Created"/>).</summary>
    public void Create() => NewThen("New binder", "Create", pack =>
    {
        Created?.Invoke(this, pack);
        return Task.CompletedTask;
    });

    void NewThen(string heading, string actionText, Func<SessionPackInfo, Task> then) =>
        Open(SessionDialogViewModel.Naming(heading, actionText, SystemChoices()), async result =>
        {
            var created = await Run(() => sessions.CreateAsync(result.Title, result.Date, result.KeepAtHand, result.GameSystem), "Making the session failed");
            if (created is null) return;
            await Directory.LoadAsync();
            await then(created);
        });

    /// <summary>Edit: a pack's title and date, or whether it's kept at hand and for which game system.</summary>
    public void Edit(SessionPackInfo pack) =>
        Open(SessionDialogViewModel.Naming("Edit binder", "Save", SystemChoices(), pack.Title, pack.Date, pack.KeepAtHand, pack.GameSystem), async result =>
        {
            if (await Run(async () =>
                {
                    await sessions.UpdateAsync(pack.Id, result.Title, result.Date, result.KeepAtHand, result.GameSystem);
                    return true;
                }, "Saving the session failed"))
                await FinishAsync(null, null);
        });

    IReadOnlyList<Choice<string?>> SystemChoices() => [.. Directory.Systems.Select(s => new Choice<string?>(s.Value, s.Label))];

    /// <summary>Add section with a name typed in the dialog ("Other…"); the suggested names add straight away.</summary>
    public void AddSection(long packId) =>
        Open(SessionDialogViewModel.NamingSection("Add section", "Add"), result => AddSectionAsync(packId, result.Title));

    public async Task AddSectionAsync(long packId, string name)
    {
        if (await Run(() => sessions.AddSectionAsync(packId, name), "Adding the section failed") is { } section)
            await FinishAsync(BinderText.SectionAdded(section.Name), null);
    }

    public void RenameSection(long sectionId, string name) =>
        Open(SessionDialogViewModel.NamingSection("Rename section", "Save", name), async result =>
        {
            if (await Run(async () => { await sessions.RenameSectionAsync(sectionId, result.Title); return true; }, "Renaming the section failed"))
                await FinishAsync(null, null);
        });

    public async Task DeleteSectionAsync(long sectionId, string name)
    {
        if (await Run(async () => { await sessions.DeleteSectionAsync(sectionId); return true; }, "Deleting the section failed"))
            await FinishAsync(BinderText.SectionDeleted(name), null);
    }

    /// <summary>Takes items out of a pack, with Undo.</summary>
    public async Task RemoveAsync(IReadOnlyList<long> itemIds, string what, string pack)
    {
        var removed = await Run(() => sessions.RemoveItemsAsync(itemIds), "Removing from the session failed");
        if (removed is { Count: > 0 }) await FinishAsync(BinderText.Removed(what, pack), () => sessions.RestoreItemsAsync(removed));
    }

    /// <summary>Duplicate: a copy of the pack, without its date, which then opens.</summary>
    public async Task<SessionPackInfo?> DuplicateAsync(SessionPackInfo pack)
    {
        var copy = await Run(() => sessions.DuplicateAsync(pack.Id, BinderText.CopyTitle(pack.Title)), "Duplicating the session failed");
        if (copy is not null) await FinishAsync(null, null);
        return copy;
    }

    /// <summary>Deletes a pack, never its books. Returns what Undo needs.</summary>
    public async Task<SessionPackSnapshot?> DeleteAsync(SessionPackInfo pack)
    {
        var deleted = await Run(() => sessions.DeleteAsync(pack.Id), "Deleting the session failed");
        if (deleted is not null) await FinishAsync(null, null);
        return deleted;
    }

    /// <summary>The note after a delete, on the page shown after it, with Undo bringing the pack back.</summary>
    public void ShowDeleted(SessionPackSnapshot deleted) =>
        Undo.Offer(BinderText.Deleted(deleted.Title), Undoing(async () => await sessions.RestoreAsync(deleted)));

    /// <summary>Esc or Cancel closes the dialog; the note goes when the page is left.</summary>
    public void Close()
    {
        Dialog?.CancelCommand.Execute(null);
        Undo.Clear();
    }

    void Open(SessionDialogViewModel dialog, Func<SessionDialogResult, Task> then)
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
        if (message is null) Undo.Clear();
        else Undo.Offer(message, undo is null ? null : Undoing(undo));
        await ShowChangeAsync();
    }

    /// <summary>What Undo runs: <paramref name="undo"/> off the UI thread, then the page shows the change.</summary>
    Func<Task> Undoing(Func<Task> undo) => async () =>
    {
        await Task.Run(undo);
        await ShowChangeAsync();
    };

    async Task ShowChangeAsync()
    {
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
            Undo.Say(BinderText.Failed);
            return default;
        }
    }
}

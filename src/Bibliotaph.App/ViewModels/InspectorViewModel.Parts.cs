using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>An adventure in the file's details: its title, and its pages in the copy the file opens, or that they weren't found there.</summary>
public sealed record InspectorPart(EntryId EntryId, string Title, PartPages? Pages, string PagesText);

/// <summary>
/// The details' adventures: a file lists the adventures marked in it, with Add, Edit range and Remove; an adventure
/// says which file it is in, with Edit range and Remove adventure. Pages are typed as the file numbers them.
/// </summary>
public sealed partial class InspectorViewModel
{
    PartsService? _parts;
    IReadOnlyList<string?> _labels = [];

    /// <summary>Raised to show another card's details, as an adventure's file or one of a file's adventures.</summary>
    public event EventHandler<EntryId>? ShowEntryRequested;

    /// <summary>Raised after an adventure's own Remove adventure, with what Undo needs: its file's details show next.</summary>
    public event EventHandler<(RemovedPart Removed, string Title)>? PartRemoved;

    /// <summary>A PDF that is a book's own file can have adventures marked in it.</summary>
    public bool CanMarkParts => _parts is not null && !Item.IsPack && !Item.IsElsewhere && !Item.IsPart && Item.Entry.Format == SourceFormats.Pdf;

    /// <summary>The card is an adventure inside a file.</summary>
    public bool IsPart => Item.IsPart;

    /// <summary>"In Drowned Abbey · pp. 3–4", for an adventure.</summary>
    public string PartOf => Item.Entry.PartLine ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParts), nameof(PartsHeading))]
    public partial IReadOnlyList<InspectorPart> Parts { get; private set; } = [];

    public bool HasParts => Parts.Count > 0;

    public string PartsHeading => DetailsText.PartsHeading(Parts.Count);

    /// <summary>Adding an adventure, or changing one's pages, in the details.</summary>
    [ObservableProperty]
    public partial PartEditorViewModel? PartEditor { get; private set; }

    /// <summary>"Removed “The Crypt”.", beside its Undo.</summary>
    public UndoSlot PartUndo { get; }

    async Task LoadPartsAsync(PartsService parts)
    {
        _parts = parts;
        if (Item.IsElsewhere || Item.IsPack || Item.Entry.Format != SourceFormats.Pdf) return;
        _labels = await Task.Run(() => parts.GetPageLabelsAsync(Item.DocumentId));
        if (!Item.IsPart) await ReloadPartsAsync();
        OnPropertyChanged(nameof(CanMarkParts));
    }

    async Task ReloadPartsAsync()
    {
        if (_parts is not { } parts) return;
        var all = await Task.Run(() => parts.GetAsync(Item.EntryId));
        Parts = [.. all.Select(p => new InspectorPart(p.EntryId, p.Title, p.Pages,
            DetailsText.PartPages(p.Pages, _labels)))];
    }

    int? FindPage(string text) => PageNumbers.Find(text, _labels, _labels.Count);

    string Shown(int page) => PageNumbers.Display(page, _labels);

    /// <summary>Add: pages of this file as an adventure with a card of its own.</summary>
    [RelayCommand]
    void AddPart()
    {
        if (!CanMarkParts || _parts is not { } parts) return;
        var documentId = Item.DocumentId;
        OpenPartEditor(new PartEditorViewModel("Mark pages as an adventure", "Make its card", "", Shown(0), "", FindPage, async (title, pages) =>
        {
            var added = await Task.Run(() => parts.AddAsync(documentId, pages, title));
            if (added.Problem is not null) return added.Problem;
            PartUndo.Say(DetailsText.PartAdded(title));
            await PartsChangedAsync();
            return null;
        }));
    }

    /// <summary>Edit range on one of the file's adventures, or on the adventure whose details these are.</summary>
    [RelayCommand]
    void EditPartRange(InspectorPart? part)
    {
        if (_parts is not { } parts) return;
        var (partId, title, at) = part is null ? (Item.EntryId, Item.Title, Item.Entry.Pages) : (part.EntryId, part.Title, part.Pages);
        if (part is null && !IsPart) return;
        var documentId = Item.DocumentId;
        var (from, to) = at is { } pages ? (Shown(pages.First), pages.Count == 1 ? "" : Shown(pages.Last)) : ("", "");
        OpenPartEditor(new PartEditorViewModel($"The pages of “{title}”", "Save pages", null, from, to, FindPage, async (_, pages) =>
        {
            var moved = await Task.Run(() => parts.SetPagesAsync(partId, documentId, pages));
            if (moved.Problem is not null) return moved.Problem;
            SayPart(DetailsText.PartMoved(title, pages.Describe(_labels)), moved.Undo);
            await PartsChangedAsync();
            return null;
        }));
    }

    /// <summary>Remove on one of the file's adventures: its card goes and its pages are the file's again, with Undo.</summary>
    [RelayCommand]
    async Task RemovePart(InspectorPart part)
    {
        if (_parts is not { } parts) return;
        if (await Task.Run(() => parts.RemoveAsync(part.EntryId)) is not { } removed) return;
        ShowPartRemoved(removed, part.Title);
        await PartsChangedAsync();
    }

    /// <summary>Remove adventure, in an adventure's own details: its file's details show next, with Undo.</summary>
    [RelayCommand]
    async Task RemoveThisPart()
    {
        if (_parts is not { } parts || !IsPart) return;
        if (await Task.Run(() => parts.RemoveAsync(Item.EntryId)) is { } removed) PartRemoved?.Invoke(this, (removed, Item.Title));
    }

    /// <summary>Says an adventure was removed, with Undo that brings its card back.</summary>
    public void ShowPartRemoved(RemovedPart removed, string title)
    {
        var parts = _parts;
        SayPart(DetailsText.PartRemoved(title), parts is null ? null : () => Task.Run(() => parts.RestoreAsync(removed)));
    }

    /// <summary>The file an adventure is in, or an adventure in this file: its details.</summary>
    [RelayCommand]
    void ShowPartFile()
    {
        if (Item.Entry.ParentEntryId is { } file) ShowEntryRequested?.Invoke(this, file);
    }

    [RelayCommand]
    void ShowPart(InspectorPart part) => ShowEntryRequested?.Invoke(this, part.EntryId);

    [RelayCommand]
    void DismissPartMessage() => PartUndo.Clear();

    /// <summary>Says what changed in the adventures, with Undo that takes it back and lists them again.</summary>
    void SayPart(string message, Func<Task>? undo) => PartUndo.Offer(message, undo is null ? null : async () =>
    {
        await undo();
        await PartsChangedAsync();
    });

    void OpenPartEditor(PartEditorViewModel editor)
    {
        PartEditor?.CancelCommand.Execute(null);
        editor.Closed += (_, _) =>
        {
            if (PartEditor == editor) PartEditor = null;
        };
        PartEditor = editor;
    }

    async Task PartsChangedAsync()
    {
        await ReloadPartsAsync();
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }
}

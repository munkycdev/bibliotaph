using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>What Select mode needs from the Library page whose books it ticks.</summary>
public interface ILibrarySelectionPage
{
    /// <summary>The books the results show now, in order: what Ctrl+A and Shift+click tick.</summary>
    IReadOnlyList<LibraryItemViewModel> Shown { get; }

    /// <summary>The results show matching pages rather than books, so there is nothing for Ctrl+A to tick.</summary>
    bool ShowsPages { get; }

    /// <summary>
    /// The page is the one in view. A change that finishes after the page was left offers no Undo, as leaving ends
    /// the Undo of one that finished before.
    /// </summary>
    bool IsCurrent { get; }

    /// <summary>A book's title from the cards the page has shown, or null when it hasn't shown it.</summary>
    string? TitleOf(EntryId entryId);

    /// <summary>Closes the book's details, as a dialog opens over the page.</summary>
    void CloseDetails();

    /// <summary>Shows the library again after a change.</summary>
    Task RefreshAsync();
}

/// <summary>
/// The Library's Select mode: the books ticked, the bulk editor, and the Select bar's Same book, Pack together, Remove
/// adventures and Export these, with the note under the bar that says what the last change did, beside its Undo.
/// </summary>
public sealed partial class LibrarySelectionViewModel : ObservableObject
{
    readonly ILibrarySelectionPage _page;
    readonly MetadataService _metadata;
    readonly CopiesService _copies;
    readonly PackService _packs;
    readonly LibraryStore _library;
    readonly PartsService _parts;
    readonly ExportService _export;
    readonly IFilePicker _files;
    readonly ILogger _log;

    /// <param name="page">The page whose books are ticked.</param>
    /// <param name="metadata">Loads the bulk editor, and undoes its edit.</param>
    /// <param name="copies">Joins the ticked cards as one book, for Same book.</param>
    /// <param name="packs">Packs them together, for Pack together.</param>
    /// <param name="library">Names the pack Pack together offers.</param>
    /// <param name="parts">Removes ticked adventures, and brings them back.</param>
    /// <param name="export">Writes the ticked books as CSV, for Export these.</param>
    /// <param name="files">Asks where the CSV goes.</param>
    /// <param name="log">Failures.</param>
    public LibrarySelectionViewModel(ILibrarySelectionPage page, MetadataService metadata, CopiesService copies, PackService packs, LibraryStore library,
        PartsService parts, ExportService export, IFilePicker files, ILogger log)
    {
        _page = page;
        _metadata = metadata;
        _copies = copies;
        _packs = packs;
        _library = library;
        _parts = parts;
        _export = export;
        _files = files;
        _log = log;
        Undo = new UndoSlot(log);
        Books.Changed += (_, _) => OnBooksChanged();
    }

    /// <summary>The books ticked in Select mode, kept while the search and filters change.</summary>
    public BookSelection Books { get; } = new();

    /// <summary>
    /// "Edited 12 books.", under the Select bar, after a bulk edit, Same book, Pack together, Remove adventures, a split
    /// or a removal, beside its Undo: until the next of them, or until the Library is left.
    /// </summary>
    public UndoSlot Undo { get; }

    /// <summary>The bulk editor, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand), nameof(SelectAllCommand))]
    public partial BulkEditViewModel? BulkEdit { get; private set; }

    /// <summary>The Same book dialog, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinSelectedCommand))]
    public partial SameBookDialogViewModel? SameBookDialog { get; private set; }

    /// <summary>The Pack together dialog, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackSelectedCommand))]
    public partial PackTogetherDialogViewModel? PackDialog { get; private set; }

    void OnBooksChanged()
    {
        EditSelectedCommand.NotifyCanExecuteChanged();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        SelectRangeCommand.NotifyCanExecuteChanged();
        JoinSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanJoinSelected));
        PackSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPackSelected));
        RemoveSelectedPartsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveSelectedParts));
    }

    /// <summary>Select: a checkbox on every book, and a click ticks a book instead of opening its details.</summary>
    [RelayCommand]
    void StartSelecting() => Books.Start();

    /// <summary>Done, or Esc: leaves Select mode, and the ticks with it.</summary>
    [RelayCommand]
    void StopSelecting() => Books.Stop();

    /// <summary>Shift+click in Select mode: ticks every book from the last one clicked to this one.</summary>
    [RelayCommand(CanExecute = nameof(IsSelecting))]
    void SelectRange(LibraryItemViewModel item) => Books.SelectRange(_page.Shown, item);

    bool IsSelecting() => Books.IsActive;

    bool CanSelectAll => Books.IsActive && !_page.ShowsPages && BulkEdit is null;

    /// <summary>Ctrl+A in Select mode: ticks every book in the current results.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    void SelectAll() => Books.SelectAll(_page.Shown);

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    void ClearSelection() => Books.Clear();

    bool CanClearSelection => Books.HasAny;

    bool CanEditSelected => Books.HasAny && BulkEdit is null;

    /// <summary>Opens the bulk editor for the ticked books, shown or not.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    async Task EditSelected()
    {
        try
        {
            var bulk = await BulkEditViewModel.LoadAsync(Books.EntryIds, _metadata);
            bulk.Closed += async (_, result) => await BulkEditClosedAsync(result);
            _page.CloseDetails();
            BulkEdit = bulk;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading metadata for {Count} selected documents failed", Books.Count);
        }
    }

    async Task BulkEditClosedAsync(BulkEditResult? result)
    {
        BulkEdit = null;
        if (result is null) return;
        // Leaving the Library while it applied ends the Undo, as leaving afterwards would. Undo puts back exactly what
        // the fields held before.
        SayDone(result.Books == 0 ? "Those books already had these values, so nothing changed." : $"Edited {BulkEditViewModel.Books(result.Books)}.",
            result.Books > 0 && _page.IsCurrent ? () => _metadata.UndoManyAsync(result.Undo) : null,
            "The edit couldn't be undone.");
        await _page.RefreshAsync();
    }

    /// <summary>Export these: the ticked books as CSV, the rows Settings > Backup's Export CSV writes.</summary>
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    async Task ExportSelected()
    {
        if (_files.SaveFile("Export these books for a spreadsheet", "Bibliotaph books.csv", "CSV (*.csv)|*.csv", ".csv") is not { } path) return;
        Undo.Clear();
        try
        {
            var rows = await _export.WriteCsvAsync(path, Books.EntryIds);
            Undo.Say($"Exported {BulkEditViewModel.Books(rows)} to {System.IO.Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogError(ex, "Exporting {Count} selected books to {Path} failed", Books.Count, path);
            Undo.Say($"The export couldn't be saved: {ex.Message}");
        }
    }

    // Same book.

    /// <summary>
    /// Same book fits two or more cards for books or single images, with at most one book owned elsewhere among them.
    /// Packs aren't offered: a pack is many files, not a copy of a book.
    /// </summary>
    public bool CanJoinSelected => SameBookDialog is null && Books.Count >= 2 && Books.Items is var cards
        && !cards.Any(c => c.IsPack) && cards.Count(c => c.IsElsewhere) <= 1;

    /// <summary>Same book: the dialog for which file opens, then the ticked cards become one book.</summary>
    [RelayCommand(CanExecute = nameof(CanJoinSelected))]
    async Task JoinSelected()
    {
        try
        {
            var dialog = await SameBookDialogViewModel.LoadAsync(_copies, Books.Items);
            dialog.Closed += async (_, result) => await SameBookClosedAsync(result);
            _page.CloseDetails();
            SameBookDialog = dialog;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the copies of {Count} selected cards failed", Books.Count);
        }
        OnPropertyChanged(nameof(CanJoinSelected));
    }

    /// <summary>After Join: the library with one card where there were several, and Undo beside a note saying so.</summary>
    async Task SameBookClosedAsync(SameBookResult? result)
    {
        SameBookDialog = null;
        OnPropertyChanged(nameof(CanJoinSelected));
        if (result is null) return;
        var title = _page.TitleOf(result.Join.EntryId) ?? "one book";
        // Leaving the Library while it joined ends the Undo, as leaving afterwards would. Undo gives each card its
        // files back, with what it brought.
        SayDone($"Joined {result.Cards.ToString("N0", CultureInfo.CurrentCulture)} cards as {title}.",
            _page.IsCurrent ? () => _copies.UndoJoinAsOneBookAsync(result.Join) : null,
            "The cards couldn't be taken apart again. Use Not the same book in the card's Copies list.");
        Books.Clear();
        await _page.RefreshAsync();
    }

    // Pack together.

    /// <summary>
    /// Pack together fits two or more cards for single files, images or PDFs (a party's character sheets), or packs:
    /// no books owned elsewhere, no adventures inside files, and no file with adventures marked in it, whose cards
    /// name it.
    /// </summary>
    public bool CanPackSelected => PackDialog is null && Books.Count >= 2
        && Books.Items.All(c => c.IsPack || c is { IsElsewhere: false, IsPart: false, Entry.Parts: 0 }
            && (SourceFormats.IsImage(c.Entry.Format) || c.Entry.Format == SourceFormats.Pdf));

    /// <summary>Pack together: the dialog for the pack's name, then the ticked cards become one pack.</summary>
    [RelayCommand(CanExecute = nameof(CanPackSelected))]
    async Task PackSelected()
    {
        try
        {
            var dialog = await PackTogetherDialogViewModel.LoadAsync(_packs, _library, Books.Items);
            dialog.Closed += async (_, result) => await PackDialogClosedAsync(result);
            _page.CloseDetails();
            PackDialog = dialog;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening Pack together for {Count} selected cards failed", Books.Count);
        }
        OnPropertyChanged(nameof(CanPackSelected));
    }

    /// <summary>After Pack together: the library with the pack, and Undo beside a note saying what happened.</summary>
    async Task PackDialogClosedAsync(PackTogetherDialogResult? result)
    {
        PackDialog = null;
        OnPropertyChanged(nameof(CanPackSelected));
        if (result is null) return;
        // Leaving the Library while it packed ends the Undo, as leaving afterwards would. Undo puts the cards back as
        // they were: merged packs come back, and files have their own cards again.
        SayDone(result.Message, _page.IsCurrent ? () => _packs.UndoPackTogetherAsync(result.Result) : null,
            "The pack couldn't be put back as it was. Use Split in its details.");
        Books.Clear();
        await _page.RefreshAsync();
    }

    // Remove adventures: the ticked adventures' cards go at once, as each one's own Remove adventure would.

    /// <summary>Remove adventures fits ticked cards that are all adventures inside files.</summary>
    public bool CanRemoveSelectedParts => !IsRemovingParts && Books.HasAny && Books.Items.All(c => c.IsPart);

    bool IsRemovingParts { get; set; }

    /// <summary>Remove adventures: each ticked adventure's card goes and its pages are its file's again, with one Undo for them all.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedParts))]
    async Task RemoveSelectedParts()
    {
        var cards = Books.Items;
        var removed = new List<RemovedPart>();
        IsRemovingParts = true;
        OnBooksChanged();
        _page.CloseDetails();
        Undo.Say("Removing…");
        string message;
        try
        {
            foreach (var card in cards)
                if (await Task.Run(() => _parts.RemoveAsync(card.EntryId)) is { } part) removed.Add(part);
            message = $"Removed {LibraryItemViewModel.Adventures(removed.Count)}. Their pages are only their files' again.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Removing {Count} selected adventures failed after {Removed}", cards.Count, removed.Count);
            message = removed.Count == 0 ? "The adventures couldn't be removed." : $"Only {LibraryItemViewModel.Adventures(removed.Count)} could be removed.";
        }
        // Leaving the Library while it removed ends the Undo, as leaving afterwards would. Undo brings each adventure
        // back as a card with what was set on it.
        SayDone(message, removed.Count > 0 && _page.IsCurrent ? async () =>
        {
            foreach (var part in Enumerable.Reverse(removed)) await _parts.RestoreAsync(part);
        } : null, "The adventures couldn't all be brought back. Mark their pages again from their files' details.");
        IsRemovingParts = false;
        Books.Clear();
        await _page.RefreshAsync();
    }

    /// <summary>
    /// Says under the Select bar what a change did, with Undo when <paramref name="undo"/> is given: it runs off the UI
    /// thread, and the library is shown again whether or not it worked. <paramref name="failed"/> is what the note says
    /// if it didn't.
    /// </summary>
    public void SayDone(string message, Func<Task>? undo, string failed) => Undo.Offer(message, undo is null ? null : async () =>
    {
        try
        {
            await Task.Run(undo);
        }
        finally
        {
            await _page.RefreshAsync();
        }
    }, failed);

    /// <summary>Esc while Same book, Pack together or the bulk editor is open: closes it, or its summary. False when none is open.</summary>
    public bool CloseDialog()
    {
        if (SameBookDialog is { } sameBook) sameBook.CancelCommand.Execute(null);
        else if (PackDialog is { } packing) packing.CancelCommand.Execute(null);
        else if (BulkEdit is { } bulk) bulk.EscapeCommand.Execute(null);
        else return false;
        return true;
    }

    /// <summary>
    /// Leaving the Library: the note and its Undo go, as do the dialogs, except a bulk edit already being applied,
    /// which finishes.
    /// </summary>
    public void Close()
    {
        // A bulk edit can be undone until the Library is left, and a split likewise.
        Undo.Clear();
        SameBookDialog = null;
        PackDialog = null;
        if (BulkEdit is { IsApplyingStep: false }) BulkEdit = null;
    }
}

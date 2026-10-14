using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>What the notes panel needs from the reader it sits beside: the page in view, how the book numbers its pages, and where to go.</summary>
public interface IPageNotesReader
{
    /// <summary>The page in view.</summary>
    int CurrentPageIndex { get; }

    /// <summary>How many pages the open PDF has.</summary>
    int PageCount { get; }

    /// <summary>The open PDF's printed page labels, by page; empty, or null at a page, where it has none.</summary>
    IReadOnlyList<string?> PageLabels { get; }

    /// <summary>Scrolls to a page.</summary>
    void GoToPage(int pageIndex);

    /// <summary>Shows the Notes panel, as once a note is saved.</summary>
    void ShowNotes();

    /// <summary>A short message that clears itself.</summary>
    void ShowNotice(string text);

    /// <summary>A note gone to whose page changed, or is still being looked for: the reader says so, with Use this page.</summary>
    void ShowNotePlace(PageNoteOpen note);

    /// <summary>The open book's notes were read again, as when any reader changed one of them.</summary>
    void NotesRead(long documentId);
}

/// <summary>
/// The reader's Notes panel: the notes on the open book's pages, from all its copies, each going to its pages, and
/// Note, which adds one on the page in view. A note can be changed in the same dialog, or deleted with Undo until the
/// next change. The list is read again whenever any reader changes one of the book's notes.
/// </summary>
public sealed partial class PageNotesViewModel : ObservableObject
{
    readonly IPageNotes _notes;
    readonly IPageNotesReader _reader;
    readonly Dispatcher _dispatcher;
    readonly ILogger _log;
    long? _documentId;
    EntryId? _entry;
    // Moves on as a book opens or closes, so a list that finishes reading after it is dropped.
    int _version;

    /// <param name="notes">The notes, read and changed.</param>
    /// <param name="reader">The reader the panel sits beside.</param>
    /// <param name="dispatcher">The reader's, for changes raised on other threads.</param>
    /// <param name="log">Failures; never a note's text.</param>
    public PageNotesViewModel(IPageNotes notes, IPageNotesReader reader, Dispatcher dispatcher, ILogger log)
    {
        _notes = notes;
        _reader = reader;
        _dispatcher = dispatcher;
        _log = log;
        Undo = new UndoSlot(log);
    }

    /// <summary>The notes on the open book's pages, from all its copies, in page order.</summary>
    public ObservableCollection<PageNoteRow> Items { get; } = [];

    /// <summary>"Notes", or "Notes (3)".</summary>
    public string Label => Items.Count > 0 ? $"Notes ({Items.Count.ToString("N0", CultureInfo.CurrentCulture)})" : "Notes";

    [ObservableProperty]
    public partial bool HasNotes { get; private set; }

    /// <summary>Adding or changing a note, as a dialog over the reader.</summary>
    [ObservableProperty]
    public partial PageNoteDialogViewModel? Dialog { get; private set; }

    /// <summary>"Deleted the note on p. 12.", beside its Undo.</summary>
    public UndoSlot Undo { get; }

    /// <summary>A PDF is open: lists its book's notes, and keeps the list up to date.</summary>
    public void Attach(long documentId)
    {
        _version++;
        _documentId = documentId;
        LoadAsync().FireAndForget();
    }

    /// <summary>The book closed: the list empties, and a dialog still open and the last note's Undo go.</summary>
    public void Detach()
    {
        _version++;
        _documentId = null;
        Dialog?.CancelCommand.Execute(null);
        Undo.Clear();
        _notes.PageNotesChanged -= OnPageNotesChanged;
        Items.Clear();
        HasNotes = false;
        OnPropertyChanged(nameof(Label));
    }

    /// <summary>Note: a note on the page in view, or on the page with selected text, which the dialog can widen to a range.</summary>
    [RelayCommand]
    void Add(int? selectionPage)
    {
        if (_documentId is not { } documentId) return;
        var page = Math.Clamp(selectionPage ?? _reader.CurrentPageIndex, 0, Math.Max(0, _reader.PageCount - 1));
        var shown = PageNumbers.Display(page, _reader.PageLabels);
        OpenDialog(new PageNoteDialogViewModel($"A note on page {shown}", "Save note", shown, "", "", FindPage), async result =>
        {
            var added = await Task.Run(() => _notes.AddPageNoteAsync(documentId, result.FirstPage, result.LastPage, result.Text));
            if (added is null) Undo.Say("The note couldn't be saved. The log may have the details.");
            else
            {
                _reader.ShowNotes();
                Undo.Say($"Saved the note on {SessionItemRow.Pages(added.Range)}.");
            }
        });
    }

    /// <summary>
    /// Changes a note's text or pages, shown as they are in the open document. Pages left as they were stay where the
    /// note is; new ones are of the open document.
    /// </summary>
    [RelayCommand]
    void Edit(PageNoteRow row)
    {
        if (_documentId is not { } documentId) return;
        var range = row.Note.Range;
        var (first, last) = row.PagesIn(documentId);
        var labels = _reader.PageLabels;
        var (from, to) = (PageNumbers.Display(first, labels), first == last ? "" : PageNumbers.Display(last, labels));
        OpenDialog(new PageNoteDialogViewModel($"The note on {row.Pages}", "Save note", from, to, row.Text, FindPage), async result =>
        {
            var (document, firstPage, lastPage) = result.FirstPage == first && result.LastPage == last
                ? (range.DocumentId, range.FirstPdfPage, range.LastPdfPage)
                : (documentId, result.FirstPage, result.LastPage);
            var updated = await Task.Run(() => _notes.UpdatePageNoteAsync(row.Note, result.Text, document, firstPage, lastPage));
            if (updated is null) Undo.Say("That note isn't there any more.");
        });
    }

    /// <summary>Deletes a page note, with Undo until the next change.</summary>
    [RelayCommand]
    async Task Delete(PageNoteRow row)
    {
        var deleted = await Task.Run(() => _notes.DeletePageNoteAsync(row.Id));
        if (deleted is null) return;
        Undo.Offer($"Deleted the note on {row.Pages}.", async () => await Task.Run(() => _notes.RestorePageNoteAsync(deleted)));
    }

    /// <summary>
    /// A note in the panel: the reader goes to its first page, where it was found in this copy. One whose page changed
    /// says so, with Use this page. A note made in another copy goes to the page found for it here by fingerprint or
    /// printed page number; one not found goes to the same page, saying it may differ.
    /// </summary>
    [RelayCommand]
    void GoTo(PageNoteRow row)
    {
        if (_documentId is not { } documentId) return;
        var (first, last) = row.PagesIn(documentId);
        _reader.GoToPage(Math.Clamp(first, 0, Math.Max(0, _reader.PageCount - 1)));
        if (row.Place is { State: PagePlaceState.Changed or PagePlaceState.Pending } place && place.DocumentId == documentId)
        {
            _reader.ShowNotePlace(new PageNoteOpen(row.Id, place.State, place.Reason, first, last));
        }
        else if (!row.IsFound || row.Shown.DocumentId != documentId)
        {
            _reader.ShowNotice("This note was made in another copy of the book, so its page may be a little different here.");
        }
    }

    /// <summary>Hides what the last note change did, and its Undo.</summary>
    [RelayCommand]
    void DismissMessage() => Undo.Clear();

    int? FindPage(string text) => PageNumbers.Find(text, _reader.PageLabels, _reader.PageCount);

    void OpenDialog(PageNoteDialogViewModel dialog, Func<PageNoteDialogResult, Task> then)
    {
        dialog.Closed += async (_, result) =>
        {
            Dialog = null;
            if (result is null) return;
            try
            {
                await then(result);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Saving a page note failed");
                Undo.Say("That didn't work. The log has the details.");
            }
        };
        Dialog = dialog;
    }

    /// <summary>The open book's page notes, read again whenever any reader changes one of them.</summary>
    async Task LoadAsync()
    {
        _notes.PageNotesChanged -= OnPageNotesChanged;
        _notes.PageNotesChanged += OnPageNotesChanged;
        if (_documentId is not { } documentId) return;
        var version = _version;
        try
        {
            var (entry, notes, places) = await Task.Run(async () =>
            {
                var notes = await _notes.GetPageNotesForDocumentAsync(documentId);
                return (await _notes.GetEntryAsync(documentId), notes, await _notes.ListAsync(notes, documentId));
            });
            if (version != _version) return;
            _entry = entry;
            Items.Clear();
            foreach (var note in notes) Items.Add(new PageNoteRow(note, places.GetValueOrDefault(note.Id)));
            HasNotes = Items.Count > 0;
            OnPropertyChanged(nameof(Label));
            _reader.NotesRead(documentId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the page notes failed");
        }
    }

    void OnPageNotesChanged(object? sender, EntryId entryId)
    {
        if (entryId != _entry) return;
        if (_dispatcher.CheckAccess()) LoadAsync().FireAndForget();
        else _dispatcher.InvokeAsync(LoadAsync);
    }
}

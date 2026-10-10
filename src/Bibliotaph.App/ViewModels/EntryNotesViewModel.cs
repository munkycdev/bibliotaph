using System.Collections.ObjectModel;
using System.Windows.Threading;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A page note as the reader, run mode and the details list it: which pages, and the text, and where it opens in the
/// book as it is now (slice 4h plan, choice 2), with why when that isn't simply its pages.
/// </summary>
public sealed class PageNoteRow(PageNoteInfo note, PagePlace? place = null)
{
    public PageNoteInfo Note { get; } = note;

    /// <summary>Where the note opens now: in the book's current copy when it is on another, found as a session item's pages are.</summary>
    public PagePlace? Place { get; } = place;

    public long Id => Note.Id;

    /// <summary>"p. 42", "p. 42–44", as the book numbers its pages.</summary>
    public string Pages => SessionItemRow.Pages(Note.Range);

    public string Text => Note.Text;

    /// <summary>"This page changed: check it.", or that a new version is still being read; null when the note opens at its pages.</summary>
    public string? Reason => Place is { State: not (PagePlaceState.Ready or PagePlaceState.OtherCopy) } place ? place.Reason : null;

    public bool HasReason => Reason is not null;

    public bool IsChanged => Place?.State == PagePlaceState.Changed;

    public string AccessibleName => $"{Pages}: {Text}{(Reason is null ? "" : ". " + Reason)}";

    /// <summary>
    /// The note's pages in this document: where it was found there, or its own pages, which in another copy are the
    /// same page numbers.
    /// </summary>
    public (int FirstPage, int LastPage) PagesIn(long documentId) =>
        Place is { DocumentId: { } found } place && found == documentId && found != Note.Range.DocumentId
            ? (place.FirstPage, place.LastPage)
            : (Note.Range.FirstPdfPage, Note.Range.LastPdfPage);

    /// <summary>Whether the note is on any of these pages of this document, where it was made or where it was found.</summary>
    public bool Overlaps(long documentId, int firstPage, int lastPage)
    {
        if (Note.Range.DocumentId != documentId && Place?.DocumentId != documentId) return false;
        var (first, last) = PagesIn(documentId);
        return first <= lastPage && last >= firstPage;
    }
}

/// <summary>
/// The Notes tab of the details (slice 3 plan, choice 18): the book's own note, saved as it is typed and found by
/// search, and the notes on its pages, added in the reader, each opening the book at its pages.
/// </summary>
public sealed partial class EntryNotesViewModel : ObservableObject
{
    readonly NotesService _notes;
    readonly ILogger _log;
    readonly DispatcherTimer _saveTimer;
    bool _loading;
    bool _dirty;

    public EntryNotesViewModel(EntryId entryId, NotesService notes, ILogger log)
    {
        EntryId = entryId;
        _notes = notes;
        _log = log;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) => await SaveAsync(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
    }

    public EntryId EntryId { get; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    partial void OnTextChanged(string value)
    {
        if (_loading) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public ObservableCollection<PageNoteRow> PageNotes { get; } = [];

    [ObservableProperty]
    public partial bool HasPageNotes { get; private set; }

    /// <summary>"Notes", or "Notes (3)" when the book has a note of its own or notes on its pages.</summary>
    public string TabLabel => (Text.Trim().Length > 0 ? 1 : 0) + PageNotes.Count is var count and > 0 ? $"Notes ({count})" : "Notes";

    public async Task LoadAsync()
    {
        try
        {
            var (text, pages, places) = await Task.Run(async () =>
            {
                var pages = await _notes.GetPageNotesAsync(EntryId);
                return (await _notes.GetEntryNoteAsync(EntryId), pages, await _notes.ResolveAsync(pages));
            });
            if (!_dirty)
            {
                _loading = true;
                Text = text ?? "";
                _loading = false;
            }
            PageNotes.Clear();
            foreach (var note in pages) PageNotes.Add(new PageNoteRow(note, places.GetValueOrDefault(note.Id)));
            HasPageNotes = PageNotes.Count > 0;
            OnPropertyChanged(nameof(TabLabel));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the notes of entry {EntryId} failed", EntryId);
        }
    }

    /// <summary>Saves what was typed now, as when the details close before the pause in typing.</summary>
    public void Flush()
    {
        if (_dirty) _ = SaveAsync();
    }

    async Task SaveAsync()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        var text = Text;
        OnPropertyChanged(nameof(TabLabel));
        try
        {
            await Task.Run(() => _notes.SetEntryNoteAsync(EntryId, text));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving the note of entry {EntryId} failed", EntryId);
        }
    }
}

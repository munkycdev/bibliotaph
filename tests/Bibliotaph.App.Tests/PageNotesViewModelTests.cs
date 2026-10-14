using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>
/// The reader's Notes panel: listing the open book's notes, Note and changing one in the dialog, going to a note,
/// and deleting one with Undo.
/// </summary>
public class PageNotesViewModelTests
{
    const long Open = 7;
    const long OtherCopy = 3;
    static readonly EntryId Book = new(1001);
    static readonly DateTime Made = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The book's page notes, in memory, recording each change asked for.</summary>
    sealed class FakePageNotes : IPageNotes
    {
        public List<PageNoteInfo> Notes { get; } = [];

        public List<string> Calls { get; } = [];

        readonly List<EventHandler<EntryId>> _watching = [];

        /// <summary>How many are listening for changes to the notes.</summary>
        public int Watchers => _watching.Count;

        /// <summary>Set as a note is added or changed, with the document and pages it was given.</summary>
        public TaskCompletionSource<(long Document, int First, int Last, string Text)> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<EntryId>? PageNotesChanged
        {
            add => _watching.Add(value!);
            remove => _watching.Remove(value!);
        }

        public Task<EntryId?> GetEntryAsync(long documentId, CancellationToken ct = default) => Task.FromResult<EntryId?>(Book);

        public Task<IReadOnlyList<PageNoteInfo>> GetPageNotesForDocumentAsync(long documentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PageNoteInfo>>([.. Notes]);

        public Task<IReadOnlyDictionary<long, PageNotePages>> ListAsync(IReadOnlyList<PageNoteInfo> pageNotes, long? documentId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, PageNotePages>>(new Dictionary<long, PageNotePages>());

        public Task<PageNoteInfo?> AddPageNoteAsync(long documentId, int firstPage, int lastPage, string text, CancellationToken ct = default)
        {
            Saved.TrySetResult((documentId, firstPage, lastPage, text));
            return Task.FromResult<PageNoteInfo?>(Note(99, documentId, firstPage, text));
        }

        public Task<PageNoteInfo?> UpdatePageNoteAsync(PageNoteInfo note, string text, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
        {
            Saved.TrySetResult((documentId, firstPage, lastPage, text));
            return Task.FromResult<PageNoteInfo?>(note with { Text = text });
        }

        public Task<PageNoteInfo?> DeletePageNoteAsync(long noteId, CancellationToken ct = default)
        {
            Calls.Add($"delete {noteId}");
            var note = Notes.Find(n => n.Id == noteId);
            if (note is not null) Notes.Remove(note);
            return Task.FromResult(note);
        }

        public Task<PageNoteInfo?> RestorePageNoteAsync(PageNoteInfo deleted, CancellationToken ct = default)
        {
            Calls.Add($"restore {deleted.Id}");
            Notes.Add(deleted);
            return Task.FromResult<PageNoteInfo?>(deleted);
        }
    }

    /// <summary>The reader beside the panel: twenty pages, and what the panel asked of it.</summary>
    sealed class FakeReader : IPageNotesReader
    {
        public int CurrentPageIndex { get; set; }

        public int PageCount { get; set; } = 20;

        public IReadOnlyList<string?> PageLabels { get; set; } = [];

        public List<int> WentTo { get; } = [];

        public List<string> Notices { get; } = [];

        public List<PageNoteOpen> Places { get; } = [];

        public TaskCompletionSource<long> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void GoToPage(int pageIndex) => WentTo.Add(pageIndex);

        public void ShowNotes()
        {
        }

        public void ShowNotice(string text) => Notices.Add(text);

        public void ShowNotePlace(PageNoteOpen note) => Places.Add(note);

        public void NotesRead(long documentId) => Read.TrySetResult(documentId);
    }

    readonly FakePageNotes _notes = new();
    readonly FakeReader _reader = new();
    readonly PageNotesViewModel _panel;

    public PageNotesViewModelTests() => _panel = new PageNotesViewModel(_notes, _reader, Dispatcher.CurrentDispatcher, NullLogger.Instance);

    static PageNoteInfo Note(long id, long documentId, int page, string text = "The bell rings at midnight.") =>
        new(id, Book, new PageRange(documentId, page, page), text, Made, Made);

    /// <summary>Opens the panel on the document being read, and waits for its notes to be listed.</summary>
    async Task AttachAsync()
    {
        _panel.Attach(Open);
        await _reader.Read.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    static Task<T> Within<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Opening_a_book_lists_its_notes_and_watches_for_changes()
    {
        _notes.Notes.AddRange([Note(1, Open, 2), Note(2, Open, 11)]);

        await AttachAsync();

        Assert.Equal([1L, 2L], _panel.Items.Select(n => n.Id));
        Assert.True(_panel.HasNotes);
        Assert.Equal("Notes (2)", _panel.Label);
        Assert.Equal(1, _notes.Watchers);
    }

    [Fact]
    public async Task Closing_the_book_empties_the_list_cancels_the_dialog_and_stops_watching()
    {
        _notes.Notes.Add(Note(1, Open, 2));
        await AttachAsync();
        _panel.AddCommand.Execute(null);
        _panel.Undo.Say("Saved the note on p. 3.");

        _panel.Detach();

        Assert.Empty(_panel.Items);
        Assert.False(_panel.HasNotes);
        Assert.Equal("Notes", _panel.Label);
        Assert.Null(_panel.Dialog);
        Assert.Null(_panel.Undo.Message);
        Assert.Equal(0, _notes.Watchers);
    }

    [Fact]
    public async Task Deleting_a_note_offers_Undo_which_puts_it_back()
    {
        var note = Note(5, Open, 11);
        _notes.Notes.Add(note);

        await _panel.DeleteCommand.ExecuteAsync(new PageNoteRow(note));
        var offered = (_panel.Undo.Message, _panel.Undo.HasUndo, _notes.Notes.Count);
        await _panel.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(("Deleted the note on p. 12.", true, 0), offered);
        Assert.Equal(["delete 5", "restore 5"], _notes.Calls);
        Assert.Equal([note], _notes.Notes);
        Assert.Null(_panel.Undo.Message);
        Assert.False(_panel.Undo.HasUndo);
    }

    [Fact]
    public async Task Deleting_a_note_that_is_already_gone_offers_nothing()
    {
        await _panel.DeleteCommand.ExecuteAsync(new PageNoteRow(Note(5, Open, 11)));

        Assert.Null(_panel.Undo.Message);
        Assert.False(_panel.Undo.HasUndo);
    }

    [Fact]
    public async Task Hiding_the_message_takes_its_Undo_away()
    {
        var note = Note(5, Open, 11);
        _notes.Notes.Add(note);
        await _panel.DeleteCommand.ExecuteAsync(new PageNoteRow(note));

        _panel.DismissMessageCommand.Execute(null);

        Assert.Null(_panel.Undo.Message);
        Assert.False(_panel.Undo.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Note_asks_for_a_note_on_the_page_in_view_as_the_book_numbers_it()
    {
        _reader.PageLabels = ["i", "ii", "1", "2"];
        _reader.CurrentPageIndex = 2;
        await AttachAsync();

        _panel.AddCommand.Execute(null);
        var dialog = _panel.Dialog!;
        dialog.Text = "Ambush at the mill.";
        dialog.SaveCommand.Execute(null);

        Assert.Equal("A note on page 1", dialog.Heading);
        Assert.Null(_panel.Dialog);
        Assert.Equal((Open, 2, 2, "Ambush at the mill."), await Within(_notes.Saved.Task));
    }

    [Fact]
    public void Note_does_nothing_with_no_book_open()
    {
        _panel.AddCommand.Execute(null);

        Assert.Null(_panel.Dialog);
    }

    [Fact]
    public async Task Changing_only_the_text_of_a_note_from_another_copy_leaves_it_on_its_own_pages()
    {
        await AttachAsync();
        var note = Note(5, OtherCopy, 40);
        var row = new PageNoteRow(note, new PageNotePages(new PagePlace(OtherCopy, 40, 40, PagePlaceState.Ready), new PageRange(Open, 11, 11)));

        _panel.EditCommand.Execute(row);
        var dialog = _panel.Dialog!;
        dialog.Text = "The bell rings at dawn.";
        dialog.SaveCommand.Execute(null);

        Assert.Equal(("12", ""), (dialog.From, dialog.To));
        Assert.Equal((OtherCopy, 40, 40, "The bell rings at dawn."), await Within(_notes.Saved.Task));
    }

    [Fact]
    public async Task Moving_a_note_to_other_pages_puts_it_on_the_pages_of_the_copy_being_read()
    {
        await AttachAsync();
        var note = Note(5, OtherCopy, 40);
        var row = new PageNoteRow(note, new PageNotePages(new PagePlace(OtherCopy, 40, 40, PagePlaceState.Ready), new PageRange(Open, 11, 11)));

        _panel.EditCommand.Execute(row);
        var dialog = _panel.Dialog!;
        dialog.From = "15";
        dialog.To = "16";
        dialog.SaveCommand.Execute(null);

        Assert.Equal((Open, 14, 15, note.Text), await Within(_notes.Saved.Task));
    }

    [Fact]
    public async Task Going_to_a_note_goes_to_its_first_page_in_this_copy()
    {
        await AttachAsync();

        _panel.GoToCommand.Execute(new PageNoteRow(Note(5, Open, 11)));

        Assert.Equal([11], _reader.WentTo);
        Assert.Empty(_reader.Notices);
        Assert.Empty(_reader.Places);
    }

    [Fact]
    public async Task Going_to_a_note_past_the_last_page_goes_to_the_last_page()
    {
        _reader.PageCount = 5;
        await AttachAsync();

        _panel.GoToCommand.Execute(new PageNoteRow(Note(5, Open, 11)));

        Assert.Equal([4], _reader.WentTo);
    }

    [Fact]
    public async Task Going_to_a_note_from_another_copy_not_found_here_says_its_page_may_differ()
    {
        await AttachAsync();
        var row = new PageNoteRow(Note(5, OtherCopy, 11),
            new PageNotePages(new PagePlace(OtherCopy, 11, 11, PagePlaceState.Ready), new PageRange(Open, 11, 11), Found: false));

        _panel.GoToCommand.Execute(row);

        Assert.Equal([11], _reader.WentTo);
        Assert.Equal(["This note was made in another copy of the book, so its page may be a little different here."], _reader.Notices);
    }

    [Fact]
    public async Task Going_to_a_note_whose_page_changed_offers_Use_this_page()
    {
        await AttachAsync();
        const string reason = "This page changed: check it.";
        var row = new PageNoteRow(Note(5, Open, 11), new PageNotePages(new PagePlace(Open, 11, 11, PagePlaceState.Changed, reason), new PageRange(Open, 11, 11)));

        _panel.GoToCommand.Execute(row);

        Assert.Equal([11], _reader.WentTo);
        Assert.Equal([new PageNoteOpen(5, PagePlaceState.Changed, reason, 11, 11)], _reader.Places);
        Assert.Empty(_reader.Notices);
    }
}

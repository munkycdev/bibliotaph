using System.IO;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>The Library's Select mode: ticking what the page shows, clearing, what the Select bar offers, and its note with Undo.</summary>
public class LibrarySelectionViewModelTests
{
    // The cards never ask for a cover in these tests, so the cache folder is never read.
    static readonly CoverImages Covers = new(new CoverCache(new AppPaths(Path.GetTempPath())), NullLogger<CoverImages>.Instance);

    /// <summary>The page the books are ticked on: what it shows, and how often it was asked to show the library again.</summary>
    sealed class FakePage : ILibrarySelectionPage
    {
        public List<LibraryItemViewModel> Books { get; } = [];

        public IReadOnlyList<LibraryItemViewModel> Shown => Books;

        public bool ShowsPages { get; set; }

        public bool IsCurrent { get; set; } = true;

        public int Refreshes { get; private set; }

        public int DetailsClosed { get; private set; }

        public string? TitleOf(EntryId entryId) => Books.FirstOrDefault(b => b.EntryId == entryId)?.Title;

        public void CloseDetails() => DetailsClosed++;

        public Task RefreshAsync()
        {
            Refreshes++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A save dialog that is always cancelled.</summary>
    sealed class CancelledPicker : IFilePicker
    {
        public string? OpenFile(string title, string filter, string? initialFolder = null) => null;

        public string? SaveFile(string title, string suggestedName, string filter, string defaultExtension) => null;

        public string? PickFolder(string title) => null;

        public IReadOnlyList<string> PickFolders(string title) => [];
    }

    readonly FakePage _page = new();
    readonly LibrarySelectionViewModel _selection;

    public LibrarySelectionViewModelTests()
    {
        _page.Books.AddRange(new[] { "Mill Road", "The Sunken Bell", "Ashes of Varn" }.Select((title, i) => Book(title, i)));
        // Ticking, clearing and the note never reach the library's services, so these tests give none.
        _selection = new LibrarySelectionViewModel(_page, null!, null!, null!, null!, null!, null!, new CancelledPicker(), NullLogger.Instance);
    }

    static LibraryItemViewModel Book(string title, int index, EntryKind kind = EntryKind.Whole) =>
        new(new LibraryEntry(new EntryId(1000 + index), index + 1, title, "pdf", 32, null, null, DateTime.UtcNow, Searchable: true, EntryKind: kind), Covers);

    [Fact]
    public void Select_all_ticks_every_book_shown_once_Select_mode_is_on()
    {
        Assert.False(_selection.SelectAllCommand.CanExecute(null));

        _selection.StartSelectingCommand.Execute(null);
        Assert.True(_selection.SelectAllCommand.CanExecute(null));
        _selection.SelectAllCommand.Execute(null);

        Assert.Equal(3, _selection.Books.Count);
        Assert.All(_page.Books, b => Assert.True(b.IsSelected));
    }

    [Fact]
    public void Select_all_is_not_offered_while_the_results_show_matching_pages()
    {
        _page.ShowsPages = true;
        _selection.StartSelectingCommand.Execute(null);

        Assert.False(_selection.SelectAllCommand.CanExecute(null));
    }

    [Fact]
    public void Clear_unticks_every_book_and_is_offered_only_while_some_are_ticked()
    {
        _selection.StartSelectingCommand.Execute(null);
        Assert.False(_selection.ClearSelectionCommand.CanExecute(null));
        _selection.SelectAllCommand.Execute(null);
        Assert.True(_selection.ClearSelectionCommand.CanExecute(null));

        _selection.ClearSelectionCommand.Execute(null);

        Assert.False(_selection.Books.HasAny);
        Assert.All(_page.Books, b => Assert.False(b.IsSelected));
        Assert.False(_selection.ClearSelectionCommand.CanExecute(null));
        Assert.True(_selection.Books.IsActive);
    }

    [Fact]
    public void Shift_click_ticks_the_books_shown_between_the_last_click_and_this_one()
    {
        _selection.StartSelectingCommand.Execute(null);
        _selection.Books.Toggle(_page.Books[0]);

        _selection.SelectRangeCommand.Execute(_page.Books[2]);

        Assert.Equal(3, _selection.Books.Count);
    }

    [Fact]
    public void Done_leaves_Select_mode_and_the_ticks_with_it()
    {
        _selection.StartSelectingCommand.Execute(null);
        _selection.SelectAllCommand.Execute(null);

        _selection.StopSelectingCommand.Execute(null);

        Assert.False(_selection.Books.IsActive);
        Assert.False(_selection.Books.HasAny);
        Assert.False(_selection.SelectRangeCommand.CanExecute(_page.Books[0]));
    }

    [Fact]
    public void Same_book_is_offered_for_two_books_but_not_for_a_pack()
    {
        var pack = Book("Handouts", 3, EntryKind.Pack);
        _page.Books.Add(pack);
        _selection.StartSelectingCommand.Execute(null);

        _selection.Books.Toggle(_page.Books[0]);
        Assert.False(_selection.CanJoinSelected);
        _selection.Books.Toggle(_page.Books[1]);
        Assert.True(_selection.CanJoinSelected);
        Assert.True(_selection.JoinSelectedCommand.CanExecute(null));
        _selection.Books.Toggle(pack);

        Assert.False(_selection.CanJoinSelected);
    }

    [Fact]
    public async Task A_change_said_with_Undo_runs_its_undo_then_shows_the_library_again()
    {
        var undone = 0;
        _selection.SayDone("Edited 2 books.", () =>
        {
            undone++;
            return Task.CompletedTask;
        }, "The edit couldn't be undone.");

        Assert.Equal("Edited 2 books.", _selection.Undo.Message);
        Assert.True(_selection.Undo.HasUndo);

        await _selection.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, undone);
        Assert.Equal(1, _page.Refreshes);
        Assert.Null(_selection.Undo.Message);
    }

    [Fact]
    public async Task An_undo_that_fails_still_shows_the_library_again_and_says_what_to_do()
    {
        _selection.SayDone("Joined 2 cards as Mill Road.", () => throw new IOException("The disk is gone."), "Use Not the same book instead.");

        await _selection.Undo.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, _page.Refreshes);
        Assert.Equal("Use Not the same book instead.", _selection.Undo.Message);
    }

    [Fact]
    public void Esc_finds_no_dialog_to_close_when_none_is_open()
    {
        Assert.False(_selection.CloseDialog());
    }

    [Fact]
    public void Leaving_the_Library_takes_the_note_and_its_Undo_away()
    {
        _selection.SayDone("Edited 2 books.", () => Task.CompletedTask, "The edit couldn't be undone.");

        _selection.Close();

        Assert.Null(_selection.Undo.Message);
        Assert.False(_selection.Undo.HasUndo);
    }

    [Fact]
    public async Task Export_cancelled_in_the_save_dialog_leaves_the_note_as_it_was()
    {
        _selection.StartSelectingCommand.Execute(null);
        _selection.SelectAllCommand.Execute(null);
        _selection.Undo.Say("Edited 3 books.");

        await _selection.ExportSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Edited 3 books.", _selection.Undo.Message);
    }
}

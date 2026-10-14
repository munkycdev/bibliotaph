using Bibliotaph.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Tests;

/// <summary>The note a page shows after a change, with its Undo: offered, run once, failed, cleared and replaced.</summary>
public class UndoSlotTests
{
    /// <summary>Keeps what was logged, to see that a failed Undo says why in the log.</summary>
    sealed class RecordingLog : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    readonly RecordingLog _log = new();
    readonly UndoSlot _slot;
    int _undone;

    public UndoSlotTests() => _slot = new UndoSlot(_log);

    Task Undo()
    {
        _undone++;
        return Task.CompletedTask;
    }

    [Fact]
    public void An_offer_shows_its_note_with_Undo()
    {
        _slot.Offer("Added 3 books to Maps.", Undo);

        Assert.Equal("Added 3 books to Maps.", _slot.Message);
        Assert.True(_slot.HasUndo);
        Assert.True(_slot.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void A_note_said_without_an_undo_has_no_Undo()
    {
        _slot.Say("This item can't be opened right now.");

        Assert.Equal("This item can't be opened right now.", _slot.Message);
        Assert.False(_slot.HasUndo);
        Assert.False(_slot.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_runs_once_and_takes_the_note_away()
    {
        _slot.Offer("Edited 2 books.", Undo);

        await _slot.UndoCommand.ExecuteAsync(null);
        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Equal(1, _undone);
        Assert.Null(_slot.Message);
        Assert.False(_slot.HasUndo);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task While_it_runs_the_note_says_so_and_Undo_is_gone()
    {
        var running = new TaskCompletionSource();
        _slot.Offer("Edited 2 books.", () => running.Task);

        var undo = _slot.UndoCommand.ExecuteAsync(null);
        var during = (_slot.Message, _slot.HasUndo);
        running.SetResult();
        await undo;

        Assert.Equal(("Undoing…", false), during);
        Assert.Null(_slot.Message);
    }

    [Fact]
    public async Task An_undo_that_fails_says_so_in_the_one_wording_and_logs_why()
    {
        var problem = new InvalidOperationException("The catalog is locked.");
        _slot.Offer("Joined 2 cards as one book.", () => Task.FromException(problem));

        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Equal("That couldn't be undone.", _slot.Message);
        Assert.False(_slot.HasUndo);
        var (level, exception) = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Same(problem, exception);
    }

    [Fact]
    public async Task An_undo_that_fails_says_what_its_offer_said_to_do_instead()
    {
        _slot.Offer("Made a pack of 2 files.", () => Task.FromException(new InvalidOperationException("The catalog is locked.")),
            "The pack couldn't be put back as it was. Use Split in its details.");

        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Equal("The pack couldn't be put back as it was. Use Split in its details.", _slot.Message);
        Assert.False(_slot.HasUndo);
        Assert.Single(_log.Entries);
    }

    [Fact]
    public async Task A_note_said_after_a_failure_text_was_offered_fails_in_the_one_wording()
    {
        _slot.Offer("Made a pack of 2 files.", Undo, "The pack couldn't be put back as it was. Use Split in its details.");
        _slot.Offer("Removed 1 book from Maps.", () => Task.FromException(new InvalidOperationException("The catalog is locked.")));

        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Equal("That couldn't be undone.", _slot.Message);
    }

    [Fact]
    public async Task Clear_takes_the_note_and_its_Undo_away()
    {
        _slot.Offer("Removed 2 adventures.", Undo);

        _slot.Clear();
        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Null(_slot.Message);
        Assert.False(_slot.HasUndo);
        Assert.False(_slot.UndoCommand.CanExecute(null));
        Assert.Equal(0, _undone);
    }

    [Fact]
    public async Task A_new_offer_replaces_the_last_and_only_its_undo_runs()
    {
        var first = 0;
        _slot.Offer("Added 2 books to Maps.", () =>
        {
            first++;
            return Task.CompletedTask;
        });

        _slot.Offer("Removed 1 book from Maps.", Undo);
        await _slot.UndoCommand.ExecuteAsync(null);

        Assert.Equal(0, first);
        Assert.Equal(1, _undone);
        Assert.Null(_slot.Message);
    }

    [Fact]
    public async Task A_note_said_while_an_undo_runs_stays_once_it_finishes()
    {
        var running = new TaskCompletionSource();
        _slot.Offer("Edited 2 books.", () => running.Task);

        var undo = _slot.UndoCommand.ExecuteAsync(null);
        _slot.Offer("Removed 1 book from Maps.", Undo);
        running.SetResult();
        await undo;

        Assert.Equal("Removed 1 book from Maps.", _slot.Message);
        Assert.True(_slot.HasUndo);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The note a page shows after a change, "Added 3 books to Maps.", with Undo while the change can be taken back. It
/// holds one note at a time: each change's note replaces the last, and its Undo with it. Undo says so while it runs,
/// and if it fails, says that, with what to do instead where there is something, and logs why.
/// </summary>
public sealed partial class UndoSlot(ILogger log) : ObservableObject
{
    /// <summary>What the note says after an Undo that failed, unless its offer said otherwise.</summary>
    const string Failed = "That couldn't be undone.";

    Func<Task>? _undo;
    string? _failed;
    // Every note moves this on, so an undo that finishes after another change has said something leaves that note.
    int _note;

    /// <summary>What the last change did, or null when there is nothing to say.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The note has an Undo.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool HasUndo { get; private set; }

    /// <summary>Says what a change did, with Undo when <paramref name="undo"/> is given, in place of whatever was said.</summary>
    /// <param name="message">What the change did.</param>
    /// <param name="undo">What takes the change back, or null when nothing can.</param>
    /// <param name="failed">What the note says if the undo fails, such as how to put things back by hand.</param>
    public void Offer(string message, Func<Task>? undo, string? failed = null) => Show(message, undo, failed);

    /// <summary>A note with nothing to undo, such as why something couldn't be done.</summary>
    public void Say(string message) => Show(message, null, null);

    /// <summary>No note, and no Undo.</summary>
    public void Clear() => Show(null, null, null);

    void Show(string? message, Func<Task>? undo, string? failed)
    {
        _note++;
        _undo = undo;
        _failed = failed;
        Message = message;
        HasUndo = undo is not null;
    }

    /// <summary>Runs what the note offered, once.</summary>
    [RelayCommand(CanExecute = nameof(HasUndo))]
    async Task Undo()
    {
        if (_undo is not { } undo) return;
        var failed = _failed ?? Failed;
        Say("Undoing…");
        var note = _note;
        try
        {
            await undo();
            if (note == _note) Clear();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Undoing a change failed");
            Say(failed);
        }
    }
}

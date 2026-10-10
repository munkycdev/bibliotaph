using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What the page note dialog gives back: the pages (zero-based, inclusive) and the text.</summary>
public sealed record PageNoteDialogResult(int FirstPage, int LastPage, string Text);

/// <summary>
/// A note on pages, in the reader (slice 3 plan, choice 18): From and To as the reader names pages (printed labels or
/// PDF page numbers), To left blank for one page, and the note.
/// </summary>
public sealed partial class PageNoteDialogViewModel(string heading, string actionText, string from, string to, string text, Func<string, int?> findPage)
    : ObservableObject
{
    /// <summary>Raised when the dialog closes: with the note, or null when it was cancelled.</summary>
    public event EventHandler<PageNoteDialogResult?>? Closed;

    public string Heading { get; } = heading;

    public string ActionText { get; } = actionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string From { get; set; } = from;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string To { get; set; } = to;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Text { get; set; } = text;

    public string? PagesProblem => Range() is null ? "Type pages as the book numbers them, or PDF page numbers." : null;

    (int First, int Last)? Range()
    {
        if (findPage(From) is not { } first) return null;
        var last = To.Trim().Length == 0 ? first : findPage(To);
        return last is { } l ? (Math.Min(first, l), Math.Max(first, l)) : null;
    }

    bool CanSave => Text.Trim().Length > 0 && Range() is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save()
    {
        if (Range() is { } range) Closed?.Invoke(this, new PageNoteDialogResult(range.First, range.Last, Text.Trim()));
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}

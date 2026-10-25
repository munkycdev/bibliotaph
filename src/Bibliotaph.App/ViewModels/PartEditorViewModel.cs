using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Marks pages of a file as an adventure of its own, or changes an adventure's pages: its title when it is new, and
/// its first and last pages as the file numbers them (printed labels or PDF page numbers), To left blank for one
/// page. Saving asks <paramref name="save"/>, which says what's wrong, if anything, so the editor stays open with
/// that until it's fixed or cancelled.
/// </summary>
public sealed partial class PartEditorViewModel(string heading, string actionText, string? title, string from, string to, Func<string, int?> findPage,
    Func<string, PartPages, Task<string?>> save) : ObservableObject
{
    /// <summary>Raised when the editor closes, saved or cancelled.</summary>
    public event EventHandler? Closed;

    public string Heading { get; } = heading;

    public string ActionText { get; } = actionText;

    /// <summary>A new adventure is named here; one being moved keeps its name, edited in the inspector like any card's.</summary>
    public bool ShowsTitle { get; } = title is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; } = title ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string From { get; set; } = from;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string To { get; set; } = to;

    /// <summary>Why the pages or the title couldn't be saved, as the library said.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    public string? PagesProblem => Pages() is null ? Processing.DetailsText.PagesHint : null;

    PartPages? Pages()
    {
        if (findPage(From) is not { } first) return null;
        var last = To.Trim().Length == 0 ? first : findPage(To);
        return last is { } l ? new PartPages(first, l) : null;
    }

    bool CanSave => !IsSaving && Pages() is not null && (!ShowsTitle || Title.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task Save()
    {
        if (Pages() is not { } pages) return;
        IsSaving = true;
        try
        {
            Problem = await save(Title.Trim(), pages);
        }
        finally
        {
            IsSaving = false;
        }
        if (Problem is null) Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, EventArgs.Empty);
}

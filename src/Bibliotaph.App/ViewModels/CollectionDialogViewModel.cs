using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What the collection dialog closed with: a name and description, or the collection picked.</summary>
public sealed record CollectionDialogResult(string Name, string? Description, CollectionChoice? Choice);

/// <summary>
/// The dialog over a page for collections (slice 3 plan, choices 8 to 10): naming one (New collection, Rename), or
/// picking one from all of them (Add to another collection, Move to), with a box that narrows the list as you type.
/// </summary>
public sealed partial class CollectionDialogViewModel : ObservableObject
{
    readonly IReadOnlyList<CollectionChoice> _choices;

    CollectionDialogViewModel(string eyebrow, string heading, string actionText, bool naming, IReadOnlyList<CollectionChoice> choices)
    {
        Eyebrow = eyebrow;
        Heading = heading;
        ActionText = actionText;
        IsNaming = naming;
        _choices = choices;
        Shown = choices;
    }

    /// <summary>Asks for a name and an optional description, filled in for Rename.</summary>
    public static CollectionDialogViewModel Naming(string heading, string actionText, string name = "", string? description = null) =>
        new("COLLECTION", heading, actionText, naming: true, []) { Name = name, Description = description ?? "" };

    /// <summary>Asks which collection, from <paramref name="choices"/> in tree order.</summary>
    public static CollectionDialogViewModel Picking(string heading, IReadOnlyList<CollectionChoice> choices) =>
        new("COLLECTIONS", heading, "", naming: false, choices);

    /// <summary>Raised when the dialog closes: with what was chosen, or null when it was cancelled.</summary>
    public event EventHandler<CollectionDialogResult?>? Closed;

    public string Eyebrow { get; }

    public string Heading { get; }

    public string ActionText { get; }

    public bool IsNaming { get; }

    public bool IsPicking => !IsNaming;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    /// <summary>Words that narrow the picker's list to the collections whose path has them.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatch))]
    public partial IReadOnlyList<CollectionChoice> Shown { get; private set; }

    public bool HasNoMatch => IsPicking && Shown.Count == 0;

    partial void OnFilterChanged(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // While filtering, each collection shows by its path, unindented, so a match deep in the tree reads clearly.
        Shown = words.Length == 0 ? _choices
            : [.. _choices.Where(c => words.All(w => c.Path.Contains(w, StringComparison.CurrentCultureIgnoreCase))).Select(c => c with { Name = c.Path, Depth = 0 })];
    }

    bool CanSave => Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save() => Closed?.Invoke(this, new CollectionDialogResult(Name.Trim(), Description.Trim() is { Length: > 0 } d ? d : null, null));

    /// <summary>A click or Enter on a collection in the list.</summary>
    [RelayCommand]
    void Pick(CollectionChoice choice) => Closed?.Invoke(this, new CollectionDialogResult("", null, choice));

    /// <summary>Enter in the filter box picks the first collection it leaves.</summary>
    [RelayCommand]
    void PickFirst()
    {
        if (Shown.Count > 0) Pick(Shown[0]);
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}

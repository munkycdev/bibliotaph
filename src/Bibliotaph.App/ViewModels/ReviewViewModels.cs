using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What the review cards ask of Needs review. Each returns what undoes it, or a problem with what was typed.</summary>
public interface IReviewActions
{
    Task<Func<Task>> AcceptAsync(ReviewItem item);
    Task AcceptAllAsync(ReviewItem item);
    Task<Func<Task>> RejectAsync(ReviewItem item);
    Task<(string? Problem, Func<Task>? Undo)> EditAsync(ReviewItem item, string typed);
    Task<Func<Task>> AddTermAsync(PendingTerm term);
    Task<Func<Task>> MapTermAsync(PendingTerm term, Term target);
    Task<Func<Task>> RejectTermAsync(PendingTerm term);
    void Open(ReviewItem item);
    void Decided(int change);
}

/// <summary>
/// A card that, once decided, folds to one line saying what happened, with Undo, until the page is loaded again.
/// </summary>
public abstract partial class DecidedCardViewModel(IReviewActions actions) : ObservableObject
{
    Func<Task>? _undo;

    protected IReviewActions Actions { get; } = actions;

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    public partial string DoneText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Shows the card as decided, and keeps what undoes it.</summary>
    public void MarkDone(string text, Func<Task> undo)
    {
        DoneText = text;
        _undo = undo;
        IsDone = true;
        Actions.Decided(-1);
    }

    protected async Task DecideAsync(Func<Task<Func<Task>>> decide, string done)
    {
        await RunAsync(async () => MarkDone(done, await decide()));
    }

    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    Task Undo() => RunAsync(async () =>
    {
        if (_undo is null) return;
        await _undo();
        _undo = null;
        IsDone = false;
        Actions.Decided(+1);
    });
}

/// <summary>
/// One field of one document (spec 5.5): what it shows now, what the card suggests, and the evidence, with Accept,
/// Edit and Reject. "Accept all" takes every card that proposes the same values for the same field.
/// </summary>
public sealed partial class ReviewCardViewModel : DecidedCardViewModel
{
    readonly Vocabulary _vocabulary;

    public ReviewCardViewModel(ReviewItem item, Vocabulary vocabulary, IReviewActions actions) : base(actions)
    {
        Item = item;
        _vocabulary = vocabulary;
        var issue = item.Issue;
        CurrentText = Describe(issue.Current, "Not set");
        SuggestedText = Describe(issue.Suggested, "");
        ProposedText = Describe(issue.Proposed, "");
        Evidence = issue.Evidence is { } claim ? MetadataValueViewModel.Describe(claim, pages: false) + "." : "";
        // Sources that disagree: where the value showing now came from, too.
        CurrentSource = issue.Kind == ReviewKind.Conflict && issue.Current.Count > 0 ? MetadataValueViewModel.Describe(issue.Current[0].Source) + "." : "";
        PageText = issue.Evidence is { Pages.Count: > 0 } withPages
            ? (withPages.Pages.Count == 1 ? "Page " : "Pages ") + string.Join(", ", withPages.Pages.Select(p => (p + 1).ToString(CultureInfo.CurrentCulture))) + "."
            : "";
        Choices = MetadataFieldViewModel.ChoicesFor(issue.Field, vocabulary);
        EditText = issue.Kind == ReviewKind.MissingTitle ? "" : SuggestedText;
    }

    public ReviewItem Item { get; }

    public string Title => Item.Title;

    public string FieldLabel => Item.Issue.Field.Label;

    public ReviewKind Kind => Item.Issue.Kind;

    /// <summary>Why the card is here, in a line.</summary>
    public string Reason => Kind switch
    {
        ReviewKind.Conflict => "Sources disagree.",
        ReviewKind.MissingTitle => "This looks like a file's name rather than the book's title.",
        _ => "Suggested; nobody has confirmed it.",
    };

    public string CurrentText { get; }

    /// <summary>For a conflict, where the current value came from; empty otherwise.</summary>
    public string CurrentSource { get; }

    public string SuggestedText { get; }

    /// <summary>What Accept adds or changes, which Reject turns down.</summary>
    public string ProposedText { get; }

    public bool HasSuggestion => Item.Issue.CanAccept;

    public string Evidence { get; }

    public string PageText { get; }

    public bool HasEvidence => Evidence.Length > 0;

    public bool CanAccept => Item.Issue.CanAccept;

    public bool CanAcceptAll => CanAccept && Item.GroupSize > 1;

    public string AcceptAllLabel => $"Accept all {Item.GroupSize:N0}";

    public string AcceptAllHint => $"Accept every card that suggests {ProposedText} for {FieldLabel}";

    /// <summary>Reject keeps what the field shows; a missing title has nothing to turn down, so it just keeps it.</summary>
    public bool CanReject => Kind != ReviewKind.MissingTitle || Item.Issue.Current.Count > 0;

    public string RejectLabel => Kind == ReviewKind.MissingTitle ? "Keep as is" : "Reject";

    public string RejectHint => Kind switch
    {
        ReviewKind.MissingTitle => "Keep this title; it won't be asked about again",
        ReviewKind.Conflict => $"Keep {CurrentText}; {ProposedText} won't be suggested again",
        _ => $"{ProposedText} won't be suggested again",
    };

    public string EditLabel => Kind == ReviewKind.MissingTitle ? "Type a title" : "Edit";

    public IReadOnlyList<string> Choices { get; }

    public bool UsesChoices => Choices.Count > 0;

    public string EditHint => Item.Issue.Field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Year => "2019",
        _ when Item.Issue.Field.Multiple => "Separate values with commas",
        _ => "",
    };

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial string Resolution { get; private set; } = "";

    partial void OnEditTextChanged(string value) => Resolution = MetadataFieldViewModel.DescribeResolution(Item.Issue.Field, value, _vocabulary);

    [ObservableProperty]
    public partial string? PickedChoice { get; set; }

    partial void OnPickedChoiceChanged(string? value)
    {
        if (value is null) return;
        if (!Item.Issue.Field.Multiple) EditText = value;
        else
        {
            var current = MetadataValues.Split(Item.Issue.Field, EditText ?? "");
            if (!current.Contains(value, StringComparer.CurrentCultureIgnoreCase)) EditText = string.Join(", ", current.Append(value));
        }
        PickedChoice = null;
    }

    string Describe(IReadOnlyList<EffectiveValue> values, string none) =>
        values.Count == 0 ? none : string.Join(", ", values.Select(v => MetadataValueViewModel.Display(Item.Issue.Field, v.Value, _vocabulary)));

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AcceptAsync(Item), $"{FieldLabel} is now {SuggestedText}.");

    [RelayCommand]
    Task AcceptAll() => RunAsync(() => Actions.AcceptAllAsync(Item));

    [RelayCommand]
    Task Reject() => DecideAsync(() => Actions.RejectAsync(Item), Kind switch
    {
        ReviewKind.MissingTitle => "Kept the title.",
        ReviewKind.Conflict => $"Kept {CurrentText}.",
        _ => $"Rejected {ProposedText}.",
    });

    [RelayCommand]
    void Edit()
    {
        Problem = null;
        IsEditing = true;
    }

    [RelayCommand]
    void CancelEdit()
    {
        IsEditing = false;
        Problem = null;
    }

    [RelayCommand]
    Task Save() => RunAsync(async () =>
    {
        var typed = EditText ?? "";
        var (problem, undo) = await Actions.EditAsync(Item, typed);
        Problem = problem;
        if (undo is null) return;
        IsEditing = false;
        MarkDone(MetadataValues.Split(Item.Issue.Field, typed).Count == 0 ? $"{FieldLabel} cleared." : $"{FieldLabel} is now {MetadataText.Tidy(typed)}.", undo);
    });

    [RelayCommand]
    void Open() => Actions.Open(Item);
}

/// <summary>
/// A name a classifier used that the vocabulary doesn't know (choice 4 (b)), once for all the books that wait on it:
/// add it as a term, file it under an existing term, or reject it.
/// </summary>
public sealed partial class TermCardViewModel : DecidedCardViewModel
{
    public TermCardViewModel(PendingTerm term, Vocabulary vocabulary, IReviewActions actions) : base(actions)
    {
        Term = term;
        Targets = [.. vocabulary.InVocabulary(term.Term.Vocabulary).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    public PendingTerm Term { get; }

    public string Label => Term.Term.Label;

    string Noun => Term.Field.Label.ToLower(CultureInfo.CurrentCulture);

    public string Chip => $"New {Noun}";

    public string Detail => Term.Documents == 1 ? "Suggested for one book." : $"Suggested for {Term.Documents:N0} books.";

    public string Example => Term.Example is { } quote ? $"“{quote}”" : "";

    public bool HasExample => Term.Example is not null;

    public string AddLabel => $"Add as a new {Noun}";

    /// <summary>The terms this one can be filed under: same vocabulary, by label.</summary>
    public IReadOnlyList<Term> Targets { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MapCommand))]
    public partial Term? Target { get; set; }

    [RelayCommand]
    Task Add() => DecideAsync(() => Actions.AddTermAsync(Term), $"Added {Label} as a new {Noun}.");

    [RelayCommand(CanExecute = nameof(CanMap))]
    Task Map() => Target is { } target ? DecideAsync(() => Actions.MapTermAsync(Term, target), $"Filed {Label} under {target.Label}.") : Task.CompletedTask;

    bool CanMap() => Target is not null;

    [RelayCommand]
    Task Reject() => DecideAsync(() => Actions.RejectTermAsync(Term), $"Rejected {Label}.");
}

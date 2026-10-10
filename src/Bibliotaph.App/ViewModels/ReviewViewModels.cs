using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
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
    Task<Func<Task>> AnswerVersionAsync(VersionItem item, VersionAnswer answer);
    Task<Func<Task>> AnswerPackAsync(PackProposal proposal, PackAnswer answer);
    Task<Func<Task>> AnswerElsewhereAsync(ElsewhereItem item, ElsewhereAnswer answer);
    Task<Func<Task>?> UsePlaceAsync(RevisionPlace place);
    void OpenPlace(RevisionItem item, RevisionPlace place);
    void ShowFolder(string path);
    void Open(ReviewItem item);
    void OpenDocument(long documentId, string title);
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
        Evidence = issue.Kind == ReviewKind.CopiesDisagree ? "You set this on the other copy's card before the two were joined."
            : issue.Evidence is { } claim ? MetadataValueViewModel.Describe(claim, pages: false) + "." : "";
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
        ReviewKind.CopiesDisagree => "You set different values on two copies of this book.",
        ReviewKind.MissingTitle => "This looks like a file's name rather than the book's title.",
        _ => "Suggested; nobody has confirmed it.",
    };

    public string CurrentText { get; }

    public string CurrentLabel => Kind == ReviewKind.CopiesDisagree ? "This card" : "Current";

    public string SuggestedLabel => Kind == ReviewKind.CopiesDisagree ? "The other copy" : "Suggested";

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

    public string RejectLabel => Kind switch
    {
        ReviewKind.MissingTitle => "Keep as is",
        ReviewKind.CopiesDisagree => "Keep this card's",
        _ => "Reject",
    };

    public string RejectHint => Kind switch
    {
        ReviewKind.MissingTitle => "Keep this title; it won't be asked about again",
        ReviewKind.Conflict or ReviewKind.CopiesDisagree => $"Keep {CurrentText}; {ProposedText} won't be suggested again",
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
        ReviewKind.Conflict or ReviewKind.CopiesDisagree => $"Kept {CurrentText}.",
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

/// <summary>
/// "Looks like a new version of" a book (F2 plan, choice 4): a file that shares most of its pages with a book in the
/// library, or its title and publisher, but isn't the same text page for page. Make it current, keep it as another
/// copy, or say it's a separate book.
/// </summary>
public sealed partial class VersionCardViewModel(VersionItem item, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public VersionItem Item { get; } = item;

    PendingVersion Version => Item.Version;

    public string Title => Item.Title;

    public string Heading => $"Looks like a new version of {Item.BookTitle}";

    public string Evidence => Version.Evidence == VersionEvidence.SharedPages
        ? $"{Version.SharedPages.ToString("N0", CultureInfo.CurrentCulture)} of {Version.ComparedPages.ToString("N0", CultureInfo.CurrentCulture)} pages are the same as the book's; the rest differ."
        : "It has the book's title and publisher.";

    public string Detail => $"This file: {Pages(Version.PageCount)}. The book: {Pages(Version.MatchedPageCount)}.";

    public string Path => Item.Path ?? "";

    public string BookPath => Item.BookPath ?? "";

    static string Pages(int? count) => count switch
    {
        null => "pages unknown",
        1 => "1 page",
        _ => $"{count.Value.ToString("N0", CultureInfo.CurrentCulture)} pages",
    };

    [RelayCommand]
    Task MakeCurrent() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.MakeCurrent), $"{Title} is now the copy {Item.BookTitle} opens.");

    [RelayCommand]
    Task KeepAsCopy() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.KeepAsCopy), $"{Title} is now another copy of {Item.BookTitle}.");

    [RelayCommand]
    Task SeparateBook() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.SeparateBook), $"Kept {Title} as a separate book.");

    [RelayCommand]
    void OpenFile() => Actions.OpenDocument(Version.DocumentId, Title);

    [RelayCommand]
    void OpenBook() => Actions.OpenDocument(Version.MatchedDocumentId, Item.BookTitle);
}

/// <summary>
/// A smaller or mixed folder or ZIP of images (F4 plan, choice 3): "Make these 12 images one card?", with the first
/// few images to show what they are.
/// </summary>
public sealed partial class PackCardViewModel(PackProposal proposal, IReadOnlyList<CoverTile> tiles, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public PackProposal Proposal { get; } = proposal;

    public string Title => Proposal.Name;

    public string Heading => $"Make these {LibraryItemViewModel.Images(Proposal.Images.Count)} one card?";

    public string Detail => Proposal.BesidePdfs
        ? "They sit beside PDFs, so they may be a book's handouts rather than a set of their own."
        : "A pack shows as one card, with its images a click away. Nothing on disk changes.";

    public string Path => Proposal.Place.FullPath;

    /// <summary>The first images' covers, in file name order.</summary>
    public IReadOnlyList<CoverTile> Tiles { get; } = tiles;

    [RelayCommand]
    Task MakePack() => DecideAsync(() => Actions.AnswerPackAsync(Proposal, PackAnswer.Packed), $"{Title} is now one card.");

    [RelayCommand]
    Task KeepSeparate() => DecideAsync(() => Actions.AnswerPackAsync(Proposal, PackAnswer.Split), $"Kept the images in {Title} as separate cards.");

    [RelayCommand]
    void ShowFolder() => Actions.ShowFolder(Path);
}

/// <summary>
/// A new file with the title of a book owned elsewhere (F5 plan, choice 6): "You own X elsewhere. Is this its file?".
/// Same book joins the file to the book's card, which keeps everything set on it.
/// </summary>
public sealed partial class ElsewhereCardViewModel(ElsewhereItem item, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public ElsewhereItem Item { get; } = item;

    public string Title => Item.FileTitle;

    public string Heading => Item.AlsoOwn is { } owned
        ? $"You own {Item.BookTitle} elsewhere ({owned}). Is this its file?"
        : $"You own {Item.BookTitle} elsewhere. Is this its file?";

    public string Detail => $"Same book puts this file on the card for {Item.BookTitle}, keeping everything you set on it. Nothing on disk changes.";

    public string Path => Item.Path ?? "";

    [RelayCommand]
    Task SameBook() => DecideAsync(() => Actions.AnswerElsewhereAsync(Item, ElsewhereAnswer.SameBook), $"{Item.BookTitle} now opens {Title}.");

    [RelayCommand]
    Task SeparateBook() => DecideAsync(() => Actions.AnswerElsewhereAsync(Item, ElsewhereAnswer.SeparateBook), $"Kept {Title} as a separate book.");

    [RelayCommand]
    void OpenFile() => Actions.OpenDocument(Item.Match.DocumentId, Title);
}

/// <summary>
/// A new version of a book whose places need a look (slice 4h plan, choice 3): "New version of X: 12 places found
/// their page, 2 need a look". Each that needs one opens at the page suggested for it, or takes it with Use this page;
/// those found by themselves are listed and need nothing. The card is done once every place is checked.
/// </summary>
public sealed partial class RevisionCardViewModel : DecidedCardViewModel
{
    /// <summary>Places found by themselves listed by name; the rest are counted.</summary>
    const int FoundShown = 8;

    public RevisionCardViewModel(RevisionItem item, IReviewActions actions) : base(actions)
    {
        Item = item;
        Rows = [.. item.Places.Where(p => p.Outcome != PageCheck.Found).Select(p => new RevisionRowViewModel(this, p))];
        var found = item.Places.Where(p => p.Outcome == PageCheck.Found).ToList();
        Found = [.. found.Take(FoundShown).Select(RevisionRowViewModel.Describe)];
        FoundMore = found.Count > FoundShown ? $"and {(found.Count - FoundShown).ToString("N0", CultureInfo.CurrentCulture)} more" : "";
    }

    public RevisionItem Item { get; }

    public string Title => Item.BookTitle;

    public string Heading
    {
        get
        {
            var look = Rows.Count(r => !r.IsChecked);
            var found = Item.Found;
            var foundText = found == 1 ? "1 place found its page" : $"{found.ToString("N0", CultureInfo.CurrentCulture)} places found their page";
            var lookText = look == 1 ? "1 needs a look" : $"{look.ToString("N0", CultureInfo.CurrentCulture)} need a look";
            return $"New version of {Item.BookTitle}: {foundText}, {lookText}";
        }
    }

    public string Path => Item.Path ?? "";

    public string Detail => Item.NeedLook == 1
        ? "Until it's checked, the place that needs a look opens at the same page number in this version. Open it to look, or take that page with Use this page."
        : "Until they're checked, the places that need a look open at the same page number in this version. Open one to look, or take that page with Use this page.";

    /// <summary>The places that needed a look, and those already checked.</summary>
    public IReadOnlyList<RevisionRowViewModel> Rows { get; }

    /// <summary>The places that found their page by themselves, which need nothing.</summary>
    public IReadOnlyList<string> Found { get; }

    public bool HasFound => Found.Count > 0;

    public string FoundMore { get; }

    public bool HasFoundMore => FoundMore.Length > 0;

    [RelayCommand]
    void OpenBook() => Actions.OpenDocument(Item.DocumentId, Item.BookTitle);

    internal void Open(RevisionRowViewModel row) => Actions.OpenPlace(Item, row.Place);

    internal Task<Func<Task>?> UseAsync(RevisionRowViewModel row) => Actions.UsePlaceAsync(row.Place);

    /// <summary>A place was checked or put back: the heading counts again, and the card is done once none needs a look.</summary>
    internal void RowChanged()
    {
        OnPropertyChanged(nameof(Heading));
        if (IsDone || Rows.Any(r => !r.IsChecked)) return;
        var undos = Rows.Select(r => r.TakeUndo()).OfType<Func<Task>>().ToList();
        MarkDone($"Every place in the new version of {Item.BookTitle} is checked.", async () =>
        {
            foreach (var undo in undos) await undo();
            foreach (var row in Rows) row.Restored();
            OnPropertyChanged(nameof(Heading));
        });
    }
}

/// <summary>A place on a "new version" card: a session item or a page note, the page suggested for it, Open and Use this page.</summary>
public sealed partial class RevisionRowViewModel(RevisionCardViewModel card, RevisionPlace place) : ObservableObject
{
    Func<Task>? _undo;
    readonly bool _checkedBefore = place.Outcome == PageCheck.Checked;

    public RevisionPlace Place { get; } = place;

    public string Heading => Describe(Place);

    /// <summary>"Was p. 12 · opens at p. 12 here, to be checked".</summary>
    public string Where
    {
        get
        {
            var was = SessionItemRow.Pages(Place.Place.Range);
            var here = Place.PageLabel ?? (Place.Check.FirstPdfPage + 1).ToString(CultureInfo.CurrentCulture);
            return IsChecked ? $"Was {was} · now p. {here}, checked" : $"Was {was} · opens at p. {here} here, to be checked";
        }
    }

    public bool IsNote => Place.Place.Kind == PlaceKind.PageNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Where), nameof(CanUse), nameof(CanUndoUse))]
    public partial bool IsChecked { get; private set; } = place.Outcome == PageCheck.Checked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUse), nameof(CanUndoUse))]
    public partial bool IsBusy { get; private set; }

    public bool CanUse => !IsChecked && !IsBusy;

    /// <summary>Undo for a page taken here, until the whole card is done.</summary>
    public bool CanUndoUse => IsChecked && _undo is not null && !IsBusy;

    public string OpenName => $"Open {Heading}";

    public string UseName => $"Use the suggested page for {Heading}";

    public string UndoName => $"Undo the page for {Heading}";

    /// <summary>"Encounter map in Session 4", "Note: the lever opens the vault", as the card lists a place.</summary>
    public static string Describe(RevisionPlace place)
    {
        var placed = place.Place;
        if (placed.Kind == PlaceKind.PageNote)
        {
            var text = (placed.Text ?? "").ReplaceLineEndings(" ").Trim();
            return $"Note: {(text.Length > 60 ? text[..59].TrimEnd() + "…" : text)}";
        }
        var label = string.IsNullOrWhiteSpace(placed.Text) ? SessionItemRow.Pages(placed.Range) : placed.Text.Trim();
        return placed.PackTitle is { } pack ? $"{label} in {pack}" : label;
    }

    [RelayCommand]
    void Open() => card.Open(this);

    [RelayCommand]
    async Task Use()
    {
        if (!CanUse) return;
        IsBusy = true;
        try
        {
            _undo = await card.UseAsync(this) ?? (() => Task.CompletedTask);
            IsChecked = true;
        }
        finally
        {
            IsBusy = false;
        }
        card.RowChanged();
    }

    [RelayCommand]
    async Task UndoUse()
    {
        if (!CanUndoUse || _undo is not { } undo) return;
        IsBusy = true;
        try
        {
            await undo();
            _undo = null;
            IsChecked = false;
        }
        finally
        {
            IsBusy = false;
        }
        card.RowChanged();
    }

    /// <summary>The card is done: its Undo puts back every page taken here.</summary>
    internal Func<Task>? TakeUndo()
    {
        var undo = _undo;
        _undo = null;
        OnPropertyChanged(nameof(CanUndoUse));
        return undo;
    }

    /// <summary>After the card's Undo: the place needs a look again, unless it was checked before the card was shown.</summary>
    internal void Restored() => IsChecked = _checkedBefore;
}

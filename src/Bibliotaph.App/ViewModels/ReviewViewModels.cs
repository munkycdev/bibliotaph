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
    Task<IReadOnlyList<string?>> GetPageLabelsAsync(long documentId);
    Task<(string? Problem, Func<Task>? Undo)> SetPartPagesAsync(UnplacedPart part, PartPages pages);
    Task<Func<Task>> RemovePartAsync(UnplacedPart part);
    Task<(string? Problem, Func<Task>? Undo)> AcceptPartsAsync(PartProposalItem item, IReadOnlyList<ProposedPart> chosen);
    Task<Func<Task>> NotSplitAsync(PartProposalItem item);
    Task<Func<Task>> DismissCorrectionAsync(CorrectionItem item);
    void FixCorrection(CorrectionItem item);
    Task<Func<Task>> AnswerRuleAsync(FolderRuleInfo rule, bool use);
    Task<Func<Task>> AnswerFolderAsync(FolderCollectionProposal proposal, CollectionAnswer answer);
    Task<Func<Task>> AnswerSuggestionAsync(CollectionSuggestion suggestion, CollectionAnswer answer);
    void OpenPlace(RevisionItem item, RevisionPlace place);
    Task OpenPlaceInWindowAsync(RevisionItem item, RevisionPlace place);
    void ShowFolder(string path);
    void Open(ReviewItem item);
    Task OpenInWindowAsync(ReviewItem item);
    void OpenDocument(long documentId, string title);
    Task OpenDocumentInWindowAsync(long documentId, string title);
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
/// One field of one document: what it shows now, what the card suggests, and the evidence, with Accept, Edit and
/// Reject. "Accept all" takes every card that proposes the same values for the same field.
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
        FieldKind.Players => "4 or 3-5",
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

    [RelayCommand]
    Task OpenInWindow() => Actions.OpenInWindowAsync(Item);
}

/// <summary>
/// A name a classifier used that the vocabulary doesn't know, once for all the books that wait on it: add it as a
/// term, file it under an existing term, or reject it.
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
/// "Looks like a new version of" a book: a file that shares most of its pages with a book in the library, or its
/// title and publisher, but isn't the same text page for page. Make it current, keep it as another copy, or say it's
/// a separate book.
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
    Task OpenFileInWindow() => Actions.OpenDocumentInWindowAsync(Version.DocumentId, Title);

    [RelayCommand]
    void OpenBook() => Actions.OpenDocument(Version.MatchedDocumentId, Item.BookTitle);

    [RelayCommand]
    Task OpenBookInWindow() => Actions.OpenDocumentInWindowAsync(Version.MatchedDocumentId, Item.BookTitle);
}

/// <summary>
/// A smaller or mixed folder or ZIP of images: "Make these 12 images one card?", with the first few images to show
/// what they are.
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
/// A new file with the title of a book owned elsewhere: "You own X elsewhere. Is this its file?". Same book joins the
/// file to the book's card, which keeps everything set on it.
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

    [RelayCommand]
    Task OpenFileInWindow() => Actions.OpenDocumentInWindowAsync(Item.Match.DocumentId, Title);
}

/// <summary>
/// An adventure inside a file whose pages weren't found in the copy or new version the file opens now. Set its pages
/// types them as the file numbers them, starting from where they were; Remove adventure takes its card away. Until
/// then its card opens the file at its first page.
/// </summary>
public sealed partial class UnplacedPartCardViewModel(UnplacedPart part, IReadOnlyList<string?> labels, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public UnplacedPart Part { get; } = part;

    public string Title => Part.Title;

    public string Heading => $"The pages of {Part.Title} weren't found in the file {Part.FileTitle} opens now.";

    public static string Detail => "It may be a new version of the file, with pages added or moved. Set its pages to say where it is now, or remove the adventure.";

    /// <summary>Its pages being typed, inline on the card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial PartEditorViewModel? Editor { get; private set; }

    public bool IsEditing => Editor is not null;

    int? FindPage(string text) => Bibliotaph.Core.Reading.PageNumbers.Find(text, labels, labels.Count);

    string Shown(int page) => Bibliotaph.Core.Reading.PageNumbers.Display(page, labels);

    [RelayCommand]
    void SetPages()
    {
        var was = Part.WasPages is { } p && p.Last < labels.Count ? p : (PartPages?)null;
        var (from, to) = was is { } pages ? (Shown(pages.First), pages.Count == 1 ? "" : Shown(pages.Last)) : ("", "");
        var editor = new PartEditorViewModel($"The pages of {Part.Title}", "Save pages", null, from, to, FindPage, async (_, pages) =>
        {
            var (problem, undo) = await Actions.SetPartPagesAsync(Part, pages);
            if (problem is null) MarkDone($"{Part.Title} is on {pages.Describe(labels)} now.", undo ?? (() => Task.CompletedTask));
            return problem;
        });
        editor.Closed += (_, _) =>
        {
            if (Editor == editor) Editor = null;
        };
        Editor = editor;
    }

    [RelayCommand]
    Task RemovePart() => DecideAsync(() => Actions.RemovePartAsync(Part), $"Removed the adventure {Part.Title}. Its pages are only the file's again.");

    [RelayCommand]
    void OpenFile() => Actions.OpenDocument(Part.DocumentId, Part.FileTitle);

    [RelayCommand]
    Task OpenFileInWindow() => Actions.OpenDocumentInWindowAsync(Part.DocumentId, Part.FileTitle);
}

/// <summary>
/// "You said this book was cataloged wrong": Cataloged wrong… was closed without a change. Fix it opens the editor
/// again; the card goes once a correction is made there, or with It's fine now.
/// </summary>
public sealed partial class CorrectionCardViewModel(CorrectionItem item, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public CorrectionItem Item { get; } = item;

    public string Title => Item.Title;

    public string Heading { get; } = "You said this book was cataloged wrong.";

    public string Detail { get; } = "Its details were closed without a change. Fix it opens them again, with the values Bibliotaph guessed marked.";

    [RelayCommand]
    void FixIt() => Actions.FixCorrection(Item);

    [RelayCommand]
    Task ItsFine() => DecideAsync(() => Actions.DismissCorrectionAsync(Item), $"Left {Title} as it is.");

    [RelayCommand]
    void OpenFile()
    {
        if (Item.DocumentId is { } id) Actions.OpenDocument(id, Title);
    }

    [RelayCommand]
    Task OpenFileInWindow() => Item.DocumentId is { } id ? Actions.OpenDocumentInWindowAsync(id, Title) : Task.CompletedTask;
}

/// <summary>
/// "This file looks like it holds 5 adventures": where the book says it is a collection, and a ticked list of the
/// adventures proposed, with their titles and pages, which Edit lets the user change. Accept gives each ticked one a card of its own; "Not split" keeps the file
/// as one book. Either is remembered, and can be undone.
/// </summary>
public sealed partial class PartProposalCardViewModel : DecidedCardViewModel
{
    readonly IReadOnlyList<string?> _labels;

    public PartProposalCardViewModel(PartProposalItem item, IReadOnlyList<string?> labels, IReviewActions actions) : base(actions)
    {
        Item = item;
        _labels = labels;
        Rows = [.. item.Parts.Select(p => new ProposedPartRow(this, p, labels))];
    }

    public PartProposalItem Item { get; }

    public string Title => Item.FileTitle;

    public string Heading => $"This file looks like it holds {LibraryItemViewModel.Adventures(Item.Parts.Count)}.";

    public string Chip => Item.Source == PartProposalSource.Ai ? "Adventures? Read with AI" : "Adventures?";

    public string Detail
    {
        get
        {
            var says = Item.Evidence is { } evidence
                ? $"The book says “{evidence.Quote}” (p. {Bibliotaph.Core.Reading.PageNumbers.Display(evidence.PdfPage, _labels)}). "
                : "";
            var found = Item.Source == PartProposalSource.Ai
                ? "A model read its contents and the first page of each to find them."
                : "Its bookmarks and the blurbs on their first pages show where each starts.";
            return $"{says}{found} Accept gives each ticked adventure a card of its own that opens its pages; the file isn't changed.";
        }
    }

    public IReadOnlyList<ProposedPartRow> Rows { get; }

    /// <summary>The titles and pages are text boxes.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>Why Accept couldn't make the cards: pages that don't read, or overlap.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>"Make 3 cards", for the ticked adventures.</summary>
    public string AcceptLabel => Rows.Count(r => r.IsChecked) is var ticked && ticked == 1 ? "Make 1 card" : $"Make {ticked.ToString("N0", CultureInfo.CurrentCulture)} cards";

    internal void Ticked() => OnPropertyChanged(nameof(AcceptLabel));

    [RelayCommand]
    void Edit() => IsEditing = !IsEditing;

    [RelayCommand]
    Task Accept() => RunAsync(async () =>
    {
        var chosen = new List<ProposedPart>();
        foreach (var row in Rows.Where(r => r.IsChecked))
        {
            if (row.Read() is not { } part)
            {
                Problem = $"The pages of {(row.Title.Trim().Length > 0 ? row.Title.Trim() : "an adventure")} don't read as pages of this file.";
                IsEditing = true;
                return;
            }
            chosen.Add(part);
        }
        var (problem, undo) = await Actions.AcceptPartsAsync(Item, chosen);
        Problem = problem;
        if (problem is null) MarkDone($"{Item.FileTitle}: {LibraryItemViewModel.Adventures(chosen.Count)} with cards of their own.", undo ?? (() => Task.CompletedTask));
    });

    [RelayCommand]
    Task NotSplit() => DecideAsync(() => Actions.NotSplitAsync(Item), $"Kept {Item.FileTitle} as one book.");

    [RelayCommand]
    void OpenFile() => Actions.OpenDocument(Item.DocumentId, Item.FileTitle);

    [RelayCommand]
    Task OpenFileInWindow() => Actions.OpenDocumentInWindowAsync(Item.DocumentId, Item.FileTitle);
}

/// <summary>An adventure on a proposal card: ticked to make its card, and its title and pages as the file numbers them.</summary>
public sealed partial class ProposedPartRow(PartProposalCardViewModel card, ProposedPart part, IReadOnlyList<string?> labels) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    [ObservableProperty]
    public partial string Title { get; set; } = part.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesText), nameof(Summary))]
    public partial string From { get; set; } = Bibliotaph.Core.Reading.PageNumbers.Display(part.Pages.First, labels);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesText), nameof(Summary))]
    public partial string To { get; set; } = part.Pages.Count == 1 ? "" : Bibliotaph.Core.Reading.PageNumbers.Display(part.Pages.Last, labels);

    /// <summary>"pp. 34–52", or what was typed when it doesn't read.</summary>
    public string PagesText => Read()?.Pages.Describe(labels) ?? (To.Trim().Length == 0 ? $"p. {From}" : $"pp. {From}–{To}");

    /// <summary>"Levels 1–3", from its blurb; empty when it gave none.</summary>
    public string Levels => part.Levels is { } levels && LevelRange.TryParse(levels, out var range) ? range.Describe() : "";

    /// <summary>"4–6 players", from its blurb; empty when it gave none.</summary>
    public string Players => PartRules.Party(part.Quote)?.Describe() ?? "";

    /// <summary>"pp. 34–52 · Levels 1–3 · 4 players".</summary>
    public string Summary => string.Join(" · ", new[] { PagesText, Levels, Players }.Where(s => s.Length > 0));

    partial void OnIsCheckedChanged(bool value) => card.Ticked();

    /// <summary>The adventure as the row reads now, or null when its pages don't read as the file's.</summary>
    public ProposedPart? Read()
    {
        int? Find(string text) => Bibliotaph.Core.Reading.PageNumbers.Find(text, labels, labels.Count);
        if (Title.Trim().Length == 0 || Find(From) is not { } first) return null;
        var last = To.Trim().Length == 0 ? first : Find(To);
        return last is { } l ? part with { Title = Title.Trim(), Pages = new PartPages(first, l) } : null;
    }
}

/// <summary>
/// A proposed folder rule: the user set the same value on several books from one folder, so every file there could
/// get it as a suggestion. Use this rule switches it on; No thanks keeps it off, and it isn't proposed again.
/// Settings > Library lists the answered ones with a switch each.
/// </summary>
public sealed partial class FolderRuleCardViewModel(FolderRuleInfo rule, Vocabulary vocabulary, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public FolderRuleInfo Rule { get; } = rule;

    /// <summary>The folder's own name, or the library folder's for a rule on the whole of one.</summary>
    public string FolderName => FolderRuleText.FolderName(Rule);

    public string Title => $"A rule for “{FolderName}”";

    public string Heading => $"You set {FolderRuleText.Describe(Rule, vocabulary)} on {Rule.Books.ToString("N0", CultureInfo.CurrentCulture)} books in this folder. "
        + "Suggest it for every file in it?";

    public string Detail { get; } = "Files in this folder and its subfolders, new ones too, get it as a suggestion. Anything you set on a book yourself stays. "
        + "Settings > Library can switch the rule off.";

    public string Path => FolderRuleText.FullPath(Rule);

    [RelayCommand]
    Task UseRule() => DecideAsync(() => Actions.AnswerRuleAsync(Rule, true),
        $"Files in “{FolderName}” now get {FolderRuleText.Describe(Rule, vocabulary)} as a suggestion.");

    [RelayCommand]
    Task NoRule() => DecideAsync(() => Actions.AnswerRuleAsync(Rule, false), $"No rule for “{FolderName}”. Bibliotaph won't suggest this one again.");

    [RelayCommand]
    void ShowFolder() => Actions.ShowFolder(Path);
}

/// <summary>
/// A folder that could be a collection: "Make a collection “X” from these 12 books?". Accept makes it, and books that
/// land in the folder later join it until the user adds or removes one by hand; Not now puts the card away until the
/// list is made again; Never isn't asked again.
/// </summary>
public sealed partial class FolderCollectionCardViewModel(FolderCollectionProposal proposal, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public FolderCollectionProposal Proposal { get; } = proposal;

    public string Title => Proposal.Name;

    string Books => Proposal.Books.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string Heading => $"Make a collection “{Proposal.Name}” from these {Books} books?";

    public string Detail => "Books that land in this folder later join it too, until you add or remove a book by hand."
        + (Proposal.ParentName is { } parent ? $" It goes inside “{parent}”, made from the folder above." : "");

    public string Path => Proposal.FullPath;

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.Accept), $"Made the collection “{Proposal.Name}” with {Books} books.");

    [RelayCommand]
    Task NotNow() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.NotNow),
        $"“{Proposal.Name}” can wait. It's asked about again the next time this list is made.");

    [RelayCommand]
    Task Never() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.Never),
        $"No collection from “{Proposal.Name}”. Bibliotaph won't ask about this folder again.");

    [RelayCommand]
    void ShowFolder() => Actions.ShowFolder(Path);
}

/// <summary>
/// Books that could join a collection: "Add these 4 books to “X”?", because they share what most of its books share.
/// Accept adds them; Not now puts the card away until the list is made again; Never isn't asked again for these books.
/// </summary>
public sealed partial class CollectionSuggestionCardViewModel(CollectionSuggestion suggestion, Vocabulary vocabulary, IReviewActions actions)
    : DecidedCardViewModel(actions)
{
    public CollectionSuggestion Suggestion { get; } = suggestion;

    public string Title => Suggestion.Name;

    public IReadOnlyList<SuggestedBook> Books => Suggestion.Books;

    string What => Books.Count == 1 ? "this book" : $"these {Books.Count.ToString("N0", CultureInfo.CurrentCulture)} books";

    string Added => Books.Count == 1 ? "1 book" : $"{Books.Count.ToString("N0", CultureInfo.CurrentCulture)} books";

    public string Heading => $"Add {What} to “{Suggestion.Name}”?";

    /// <summary>What most of the collection's books share, which these share too.</summary>
    public string Detail
    {
        get
        {
            var reasons = new List<string>();
            if (Suggestion.Series is { } series) reasons.Add($"are in the series “{series}”");
            if (Suggestion.Setting is { } setting && Suggestion.System is { } system)
                reasons.Add($"are set in {vocabulary.Label(MetadataFields.Settings.Vocabulary!, setting)} for {vocabulary.Label(MetadataFields.System.Vocabulary!, system)}");
            return $"Most of the books in this collection {string.Join(", or ", reasons)}, and so {(Books.Count == 1 ? "is this one" : "are these")}.";
        }
    }

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.Accept), $"Added {Added} to “{Suggestion.Name}”.");

    [RelayCommand]
    Task NotNow() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.NotNow),
        $"“{Suggestion.Name}” can wait. It's asked about again the next time this list is made.");

    [RelayCommand]
    Task Never() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.Never),
        $"Bibliotaph won't suggest {(Books.Count == 1 ? "this book" : "these books")} for “{Suggestion.Name}” again.");

    [RelayCommand]
    void OpenBook(SuggestedBook book)
    {
        if (book.DocumentId is { } id) Actions.OpenDocument(id, book.Title);
    }

    [RelayCommand]
    Task OpenBookInWindow(SuggestedBook book) => book.DocumentId is { } id ? Actions.OpenDocumentInWindowAsync(id, book.Title) : Task.CompletedTask;
}

/// <summary>How a folder rule reads, on its Needs review card and in Settings > Library.</summary>
public static class FolderRuleText
{
    public static string FolderName(FolderRuleInfo rule) =>
        rule.Folder.Name is { Length: > 0 } name ? name : System.IO.Path.GetFileName(rule.RootPath.TrimEnd('\\', '/')) is { Length: > 0 } top ? top : rule.RootPath;

    /// <summary>"system Pathfinder 2e", "types Adventure": the field in lower case, then the value as it reads.</summary>
    public static string Describe(FolderRuleInfo rule, Vocabulary vocabulary) => MetadataFields.Find(rule.Field) is { } field
        ? $"{field.Label.ToLower(CultureInfo.CurrentCulture)} {MetadataValueViewModel.Display(field, rule.Value, vocabulary)}"
        : $"{rule.Field} {rule.Value}";

    public static string FullPath(FolderRuleInfo rule) => rule.Folder.Folder.Length == 0 ? rule.RootPath : System.IO.Path.Combine(rule.RootPath, rule.Folder.Folder);
}

/// <summary>
/// A new version of a book whose places need a look: "New version of X: 12 places found their page, 2 need a look".
/// Each that needs one opens at the page suggested for it, or takes it with Use this page; those found by themselves
/// are listed and need nothing. The card is done once every place is checked.
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

    [RelayCommand]
    Task OpenBookInWindow() => Actions.OpenDocumentInWindowAsync(Item.DocumentId, Item.BookTitle);

    internal void Open(RevisionRowViewModel row) => Actions.OpenPlace(Item, row.Place);

    internal Task OpenInWindowAsync(RevisionRowViewModel row) => Actions.OpenPlaceInWindowAsync(Item, row.Place);

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

    public string OpenInWindowName => $"Open {Heading} in a window";

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
    Task OpenInWindow() => card.OpenInWindowAsync(this);

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

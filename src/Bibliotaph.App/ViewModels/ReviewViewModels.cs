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
/// A card that offers to make something (a collection, a pack, a rule, cards for adventures) rather than asking whether
/// a value is right. Its × closes it with the card's own "don't ask again" answer, so closing it is remembered like
/// that answer is, and a new offer can still come.
/// </summary>
public abstract partial class OfferCardViewModel(IReviewActions actions) : DecidedCardViewModel(actions)
{
    /// <summary>What the card is about, for the × button's spoken name.</summary>
    public abstract string Title { get; }

    protected abstract Task DismissAsync();

    [RelayCommand]
    Task Dismiss() => DismissAsync();
}

/// <summary>
/// One field of one document: what it shows now, what the card suggests, and the evidence, with Accept, Edit and
/// Reject. "Accept all" takes every card that proposes the same values for the same field.
/// </summary>
public sealed partial class ReviewCardViewModel : DecidedCardViewModel
{
    readonly Vocabulary _vocabulary;
    readonly FieldCardText _text;

    public ReviewCardViewModel(ReviewItem item, Vocabulary vocabulary, IReviewActions actions) : base(actions)
    {
        Item = item;
        _vocabulary = vocabulary;
        _text = new FieldCardText(item, vocabulary);
        CurrentText = _text.CurrentText;
        SuggestedText = _text.SuggestedText;
        ProposedText = _text.ProposedText;
        Evidence = _text.Evidence;
        CurrentSource = _text.CurrentSource;
        PageText = _text.PageText;
        Choices = _text.Choices;
        EditText = _text.EditStart;
    }

    public ReviewItem Item { get; }

    public string Title => Item.Title;

    public string FieldLabel => _text.FieldLabel;

    public ReviewKind Kind => Item.Issue.Kind;

    /// <summary>Why the card is here, in a line.</summary>
    public string Reason => _text.Reason;

    public string CurrentText { get; }

    public string CurrentLabel => _text.CurrentLabel;

    public string SuggestedLabel => _text.SuggestedLabel;

    /// <summary>For a conflict, where the current value came from; empty otherwise.</summary>
    public string CurrentSource { get; }

    public string SuggestedText { get; }

    /// <summary>What Accept adds or changes, which Reject turns down.</summary>
    public string ProposedText { get; }

    public bool HasSuggestion => Item.Issue.CanAccept;

    public string Evidence { get; }

    public string PageText { get; }

    public bool HasEvidence => Evidence.Length > 0;

    public bool CanAccept => _text.CanAccept;

    public bool CanAcceptAll => _text.CanAcceptAll;

    public string AcceptAllLabel => _text.AcceptAllLabel;

    public string AcceptAllHint => _text.AcceptAllHint;

    public bool CanReject => _text.CanReject;

    public string RejectLabel => _text.RejectLabel;

    public string RejectHint => _text.RejectHint;

    public string EditLabel => _text.EditLabel;

    public IReadOnlyList<string> Choices { get; }

    public bool UsesChoices => Choices.Count > 0;

    public string EditHint => _text.EditHint;

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

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AcceptAsync(Item), _text.Accepted);

    [RelayCommand]
    Task AcceptAll() => RunAsync(() => Actions.AcceptAllAsync(Item));

    [RelayCommand]
    Task Reject() => DecideAsync(() => Actions.RejectAsync(Item), _text.Rejected);

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
        MarkDone(ReviewText.Edited(Item, typed), undo);
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
        Targets = ReviewText.TermTargets(term, vocabulary);
    }

    public PendingTerm Term { get; }

    public string Label => ReviewText.Term(Term);

    public string Chip => ReviewText.TermChip(Term);

    public string Detail => ReviewText.TermDetail(Term);

    public string Example => ReviewText.TermExample(Term) ?? "";

    public bool HasExample => Term.Example is not null;

    public string AddLabel => ReviewText.TermAdd(Term);

    /// <summary>The terms this one can be filed under: same vocabulary, by label.</summary>
    public IReadOnlyList<Term> Targets { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MapCommand))]
    public partial Term? Target { get; set; }

    [RelayCommand]
    Task Add() => DecideAsync(() => Actions.AddTermAsync(Term), ReviewText.TermAdded(Term));

    [RelayCommand(CanExecute = nameof(CanMap))]
    Task Map() => Target is { } target ? DecideAsync(() => Actions.MapTermAsync(Term, target), ReviewText.TermFiled(Term, target.Label)) : Task.CompletedTask;

    bool CanMap() => Target is not null;

    [RelayCommand]
    Task Reject() => DecideAsync(() => Actions.RejectTermAsync(Term), ReviewText.TermRejected(Term));
}

/// <summary>
/// "Looks like a new version of" a book: a file that shares most of its pages with a book in the library, or its
/// title and publisher, but isn't the same text page for page. This file is current, the book's file is current, or it's
/// a separate book.
/// </summary>
public sealed partial class VersionCardViewModel(VersionItem item, IReviewActions actions) : DecidedCardViewModel(actions)
{
    public VersionItem Item { get; } = item;

    PendingVersion Version => Item.Version;

    VersionCardText Text => new(Item);

    public string Title => Item.Title;

    public string Heading => Text.Heading;

    public string Evidence => Text.Evidence;

    public string Detail => Text.Detail;

    public string Path => Item.Path ?? "";

    public string BookPath => Item.BookPath ?? "";

    [RelayCommand]
    Task MakeCurrent() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.MakeCurrent), Text.MadeCurrent);

    [RelayCommand]
    Task KeepAsCopy() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.KeepAsCopy), Text.KeptAsCopy);

    [RelayCommand]
    Task SeparateBook() => DecideAsync(() => Actions.AnswerVersionAsync(Item, VersionAnswer.SeparateBook), Text.KeptSeparate);

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
public sealed partial class PackCardViewModel(PackProposal proposal, IReadOnlyList<CoverTile> tiles, IReviewActions actions) : OfferCardViewModel(actions)
{
    public PackProposal Proposal { get; } = proposal;

    PackCardText Text => new(Proposal);

    public override string Title => Proposal.Name;

    public string Heading => Text.Heading;

    public string Detail => Text.Detail;

    public string Path => Proposal.Place.FullPath;

    /// <summary>The first images' covers, in file name order.</summary>
    public IReadOnlyList<CoverTile> Tiles { get; } = tiles;

    [RelayCommand]
    Task MakePack() => DecideAsync(() => Actions.AnswerPackAsync(Proposal, PackAnswer.Packed), Text.Packed);

    [RelayCommand]
    Task KeepSeparate() => DecideAsync(() => Actions.AnswerPackAsync(Proposal, PackAnswer.Split), Text.KeptSeparate);

    protected override Task DismissAsync() => KeepSeparate();

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

    ElsewhereCardText Text => new(Item);

    public string Title => Item.FileTitle;

    public string Heading => Text.Heading;

    public string Detail => Text.Detail;

    public string Path => Item.Path ?? "";

    [RelayCommand]
    Task SameBook() => DecideAsync(() => Actions.AnswerElsewhereAsync(Item, ElsewhereAnswer.SameBook), Text.SameBook);

    [RelayCommand]
    Task SeparateBook() => DecideAsync(() => Actions.AnswerElsewhereAsync(Item, ElsewhereAnswer.SeparateBook), Text.KeptSeparate);

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

    UnplacedPartCardText Text => new(Part);

    public string Heading => Text.Heading;

    public static string Detail => UnplacedPartCardText.Detail;

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
        var (from, to) = Text.Start(labels);
        var editor = new PartEditorViewModel(Text.EditorHeading, "Save pages", null, from, to, FindPage, async (_, pages) =>
        {
            var (problem, undo) = await Actions.SetPartPagesAsync(Part, pages);
            if (problem is null) MarkDone(Text.Moved(pages, labels), undo ?? (() => Task.CompletedTask));
            return problem;
        });
        editor.Closed += (_, _) =>
        {
            if (Editor == editor) Editor = null;
        };
        Editor = editor;
    }

    [RelayCommand]
    Task RemovePart() => DecideAsync(() => Actions.RemovePartAsync(Part), Text.Removed);

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

    public string Heading { get; } = CorrectionCardText.Heading;

    public string Detail { get; } = CorrectionCardText.Detail;

    [RelayCommand]
    void FixIt() => Actions.FixCorrection(Item);

    [RelayCommand]
    Task ItsFine() => DecideAsync(() => Actions.DismissCorrectionAsync(Item), new CorrectionCardText(Item).LeftAsIs);

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
public sealed partial class PartProposalCardViewModel : OfferCardViewModel
{
    readonly IReadOnlyList<string?> _labels;

    public PartProposalCardViewModel(PartProposalItem item, IReadOnlyList<string?> labels, IReviewActions actions) : base(actions)
    {
        Item = item;
        _labels = labels;
        Rows = [.. item.Parts.Select(p => new ProposedPartRow(this, p, new PartProposalCardText(item, labels)))];
    }

    public PartProposalItem Item { get; }

    public override string Title => Item.FileTitle;

    PartProposalCardText Text => new(Item, _labels);

    public string Heading => Text.Heading;

    public string Chip => Text.Chip;

    public string Detail => Text.Detail;

    public IReadOnlyList<ProposedPartRow> Rows { get; }

    /// <summary>The titles and pages are text boxes.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>Why Accept couldn't make the cards: pages that don't read, or overlap.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>"Make 3 cards", for the ticked adventures.</summary>
    public string AcceptLabel => PartProposalCardText.AcceptLabel(Rows.Count(r => r.IsChecked));

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
                Problem = PartProposalCardText.Unreadable(row.Title);
                IsEditing = true;
                return;
            }
            chosen.Add(part);
        }
        var (problem, undo) = await Actions.AcceptPartsAsync(Item, chosen);
        Problem = problem;
        if (problem is null) MarkDone(Text.Accepted(chosen.Count), undo ?? (() => Task.CompletedTask));
    });

    [RelayCommand]
    Task NotSplit() => DecideAsync(() => Actions.NotSplitAsync(Item), Text.NotSplit);

    protected override Task DismissAsync() => NotSplit();

    [RelayCommand]
    void OpenFile() => Actions.OpenDocument(Item.DocumentId, Item.FileTitle);

    [RelayCommand]
    Task OpenFileInWindow() => Actions.OpenDocumentInWindowAsync(Item.DocumentId, Item.FileTitle);
}

/// <summary>An adventure on a proposal card: ticked to make its card, and its title and pages as the file numbers them.</summary>
public sealed partial class ProposedPartRow(PartProposalCardViewModel card, ProposedPart part, PartProposalCardText text) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    [ObservableProperty]
    public partial string Title { get; set; } = part.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesText), nameof(Summary))]
    public partial string From { get; set; } = text.Typed(part).From;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesText), nameof(Summary))]
    public partial string To { get; set; } = text.Typed(part).To;

    /// <summary>"pp. 34–52", or what was typed when it doesn't read.</summary>
    public string PagesText => Read()?.Pages.Describe(text.Labels) ?? (To.Trim().Length == 0 ? $"p. {From}" : $"pp. {From}–{To}");

    /// <summary>"Levels 1–3", from its blurb; empty when it gave none.</summary>
    public string Levels => PartProposalCardText.Levels(part);

    /// <summary>"4–6 players", from its blurb; empty when it gave none.</summary>
    public string Players => PartProposalCardText.Players(part);

    /// <summary>"pp. 34–52 · Levels 1–3 · 4 players".</summary>
    public string Summary => string.Join(" · ", new[] { PagesText, Levels, Players }.Where(s => s.Length > 0));

    partial void OnIsCheckedChanged(bool value) => card.Ticked();

    /// <summary>The adventure as the row reads now, or null when its pages don't read as the file's.</summary>
    public ProposedPart? Read() => text.Read(part, Title, From, To);
}

/// <summary>
/// A proposed folder rule: the user set the same value on several books from one folder, so every file there could
/// get it as a suggestion. Use this rule switches it on; No thanks keeps it off, and it isn't proposed again.
/// Settings > Library lists the answered ones with a switch each.
/// </summary>
public sealed partial class FolderRuleCardViewModel(FolderRuleInfo rule, Vocabulary vocabulary, IReviewActions actions) : OfferCardViewModel(actions)
{
    public FolderRuleInfo Rule { get; } = rule;

    /// <summary>The folder's own name, or the library folder's for a rule on the whole of one.</summary>
    readonly FolderRuleCardText _text = new(rule, vocabulary);

    public string FolderName => _text.FolderName;

    public override string Title => _text.Title;

    public string Heading => _text.Heading;

    public string Detail { get; } = FolderRuleCardText.Detail;

    public string Path => FolderRuleText.FullPath(Rule);

    [RelayCommand]
    Task UseRule() => DecideAsync(() => Actions.AnswerRuleAsync(Rule, true), _text.Used);

    [RelayCommand]
    Task NoRule() => DecideAsync(() => Actions.AnswerRuleAsync(Rule, false), _text.NoRule);

    protected override Task DismissAsync() => NoRule();

    [RelayCommand]
    void ShowFolder() => Actions.ShowFolder(Path);
}

/// <summary>
/// A folder that could be a collection: "Make a collection “X” from these 12 books?". Accept makes it, and books that
/// land in the folder later join it until the user adds or removes one by hand; Not now puts the card away until the
/// list is made again; Never isn't asked again.
/// </summary>
public sealed partial class FolderCollectionCardViewModel(FolderCollectionProposal proposal, IReviewActions actions) : OfferCardViewModel(actions)
{
    public FolderCollectionProposal Proposal { get; } = proposal;

    FolderCollectionCardText Text => new(Proposal);

    public override string Title => Proposal.Name;

    public string Heading => Text.Heading;

    public string Detail => Text.Detail;

    public string Path => Proposal.FullPath;

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.Accept), Text.Accepted);

    [RelayCommand]
    Task NotNow() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.NotNow), Text.NotNow);

    [RelayCommand]
    Task Never() => DecideAsync(() => Actions.AnswerFolderAsync(Proposal, CollectionAnswer.Never), Text.Never);

    protected override Task DismissAsync() => Never();

    [RelayCommand]
    void ShowFolder() => Actions.ShowFolder(Path);
}

/// <summary>
/// Books that could join a collection: "Add these 4 books to “X”?", because they share what most of its books share.
/// Accept adds them; Not now puts the card away until the list is made again; Never isn't asked again for these books.
/// </summary>
public sealed partial class CollectionSuggestionCardViewModel(CollectionSuggestion suggestion, Vocabulary vocabulary, IReviewActions actions)
    : OfferCardViewModel(actions)
{
    public CollectionSuggestion Suggestion { get; } = suggestion;

    public override string Title => Suggestion.Name;

    readonly CollectionSuggestionCardText _text = new(suggestion, vocabulary);

    public IReadOnlyList<SuggestedBook> Books => Suggestion.Books;

    public string Heading => _text.Heading;

    /// <summary>What most of the collection's books share, which these share too.</summary>
    public string Detail => _text.Detail;

    [RelayCommand]
    Task Accept() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.Accept), _text.Accepted);

    [RelayCommand]
    Task NotNow() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.NotNow), _text.NotNow);

    [RelayCommand]
    Task Never() => DecideAsync(() => Actions.AnswerSuggestionAsync(Suggestion, CollectionAnswer.Never), _text.Never);

    protected override Task DismissAsync() => Never();

    [RelayCommand]
    void OpenBook(SuggestedBook book)
    {
        if (book.DocumentId is { } id) Actions.OpenDocument(id, book.Title);
    }

    [RelayCommand]
    Task OpenBookInWindow(SuggestedBook book) => book.DocumentId is { } id ? Actions.OpenDocumentInWindowAsync(id, book.Title) : Task.CompletedTask;
}

/// <summary>
/// A new version of a book whose places need a look: "New version of X: 12 places found their page, 2 need a look".
/// Each that needs one opens at the page suggested for it, or takes it with Use this page; those found by themselves
/// are listed and need nothing. The card is done once every place is checked.
/// </summary>
public sealed partial class RevisionCardViewModel : DecidedCardViewModel
{
    public RevisionCardViewModel(RevisionItem item, IReviewActions actions) : base(actions)
    {
        Item = item;
        Rows = [.. RevisionCardText.Rows(item).Select(p => new RevisionRowViewModel(this, p))];
        Found = RevisionCardText.Found(item);
        FoundMore = RevisionCardText.FoundMore(item);
    }

    public RevisionItem Item { get; }

    public string Title => Item.BookTitle;

    public string Heading => RevisionCardText.Heading(Item.BookTitle, Item.Found, Rows.Count(r => !r.IsChecked));

    public string Path => Item.Path ?? "";

    public string Detail => RevisionCardText.Detail(Item);

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
        MarkDone(RevisionCardText.AllChecked(Item.BookTitle), async () =>
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

    public string Heading => RevisionCardText.Describe(Place);

    /// <summary>"Was p. 12 · opens at p. 12 here, to be checked".</summary>
    public string Where => RevisionCardText.Where(Place, IsChecked);

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

using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// The words Needs review shows, in the Windows app and on the web: the page and its tabs, the field and new-term
/// cards, what each decision says, and the files needing attention.
/// </summary>
public static class ReviewText
{
    public const string Heading = "A little clarification.";

    public const string Reassurance = "Your corrections are preserved when books are reindexed.";

    public const string Updated = "The suggestions have changed since this list was made.";

    public const string UndoFailed = "That couldn't be undone. The log has the details.";

    public const string RetryAllNote = "Gives every file below another go, apart from locked and protected ones, which trying again can't help.";

    /// <summary>Field suggestions shown at a time; more on request, so thousands of suggestions don't build thousands of cards.</summary>
    public const int PageSize = 60;

    public static string Subtitle(int remaining, int files) => (remaining, files) switch
    {
        (0, 0) => "Nothing needs a second look. Everything is ready to use.",
        (1, _) => "One suggestion worth a second look. The rest is ready to use.",
        (0, _) => "No suggestions to look at, but some files need attention.",
        _ => $"{N(remaining)} suggestions worth a second look. The rest is ready to use.",
    };

    public static string SuggestionsTab(int remaining) => $"Metadata suggestions  {N(remaining)}";

    public static string FilesTab(int files) => $"Files needing attention · {N(files)}";

    /// <summary>An entry in the Show menu, with how many of its cards are waiting.</summary>
    public static string ShowLabel(string name, int count) => $"{name} · {N(count)}";

    public static string MoreLabel(int hidden) => $"Show {N(Math.Min(PageSize, hidden))} more ({N(hidden)} not shown)";

    public static string AcceptedAll(int count) => count == 1 ? "Accepted one suggestion." : $"Accepted {N(count)} suggestions.";

    /// <summary>The Show menu's kinds of card, in the order the page lists them; field suggestions follow, by field.</summary>
    public static IReadOnlyList<(string Key, string Name)> Kinds { get; } =
    [
        ("terms", "New terms"), ("overlaps", "Terms that may be the same"), ("versions", "New versions"), ("packs", "Image packs"), ("elsewhere", "Owned elsewhere"),
        ("revisions", "Places to check"), ("parts", "Adventures"), ("corrections", "Corrections"), ("rules", "Folder rules"),
        ("folders", "Collections from folders"), ("suggestions", "Books for collections"),
    ];

    /// <summary>The Show menu's key for a field's suggestions.</summary>
    public static string FieldKey(ReviewItem item) => "field:" + item.Issue.Field.Key;

    public static string Term(PendingTerm term) => term.Term.Label;

    static string TermNoun(PendingTerm term) => term.Field.Label.ToLower(CultureInfo.CurrentCulture);

    public static string TermChip(PendingTerm term) => $"New {TermNoun(term)}";

    public static string TermDetail(PendingTerm term) => term.Documents == 1 ? "Suggested for one book." : $"Suggested for {N(term.Documents)} books.";

    public static string? TermExample(PendingTerm term) => term.Example is { } quote ? $"“{quote}”" : null;

    public static string TermAdd(PendingTerm term) => $"Add as a new {TermNoun(term)}";

    public static string TermAdded(PendingTerm term) => $"Added {Term(term)} as a new {TermNoun(term)}.";

    public static string TermFiled(PendingTerm term, string target) => $"Filed {Term(term)} under {target}.";

    public static string TermRejected(PendingTerm term) => $"Rejected {Term(term)}.";

    public const string TermRejectHint = "Not a useful name; it won't be suggested again";

    public const string TermTargetHint = "Another name for an existing term";

    public const string TermFileUnder = "File under it";

    /// <summary>The terms a new one can be filed under: same vocabulary, by label.</summary>
    public static IReadOnlyList<Term> TermTargets(PendingTerm term, Vocabulary vocabulary) =>
        [.. vocabulary.InVocabulary(term.Term.Vocabulary).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>"Greyhawk · Grayhawk": the terms of a group that may be the same thing.</summary>
    public static string OverlapTitle(OverlapProposal proposal) => string.Join(" · ", proposal.Terms.Select(t => t.Label));

    /// <summary>"Same setting?"</summary>
    public static string OverlapChip(OverlapProposal proposal) => $"Same {VocabularyNoun(proposal.Vocabulary)}?";

    /// <summary>What one term of a vocabulary is: "game system", "theme", "publisher".</summary>
    public static string VocabularyNoun(string vocabulary)
    {
        var label = (MetadataFields.ForVocabulary(vocabulary)?.Label ?? vocabulary).ToLower(CultureInfo.CurrentCulture);
        return label.EndsWith('s') ? label[..^1] : label;
    }

    /// <summary>Why the group was proposed, and by whom: the names alone, or the AI.</summary>
    public static string OverlapDetail(OverlapProposal proposal) =>
        proposal.FromAi ? $"The AI says they are the same thing: {proposal.Reason.TrimEnd('.')}." : $"{proposal.Reason}.";

    /// <summary>"Combined 2 terms into Greyhawk."</summary>
    public static string OverlapCombined(OverlapProposal proposal, string kept) => $"Combined {N(proposal.Terms.Count)} terms into {kept}.";

    /// <summary>What an edit on a field card says once saved.</summary>
    public static string Edited(ReviewItem item, string typed) =>
        MetadataValues.Split(item.Issue.Field, typed).Count == 0
            ? $"{item.Issue.Field.Label} cleared."
            : $"{item.Issue.Field.Label} is now {MetadataText.Tidy(typed)}.";

    /// <summary>
    /// A failed or blocked stage in words: a bug says so, and keeps what to paste into a report. Trying again can't help
    /// a locked file or one with DRM.
    /// </summary>
    public static AttentionRow Row(AttentionItem item, IStageCatalog stages)
    {
        var step = stages.Find(item.Stage)?.Names.Step ?? item.Stage.ToString();
        var reason = item.Reason ?? "something went wrong";
        if (reason.StartsWith(IndexingService.UnexpectedError, StringComparison.Ordinal))
            return new AttentionRow(item.DocumentId, item.Title, $"{step}: Bibliotaph hit a bug ({reason[IndexingService.UnexpectedError.Length..]})",
                Error: $"{item.Stage}: {reason}");
        return new AttentionRow(item.DocumentId, item.Title, $"{step}: {reason}", TextAccessReasons.Of(item.Status, item.Reason));
    }

    public static AttentionRow Unreadable(string fileName, string reason) => new(null, fileName, $"Couldn't be read: {reason}");

    public static AttentionRow Problem(string fileName, string problem, string where) => new(null, fileName, $"Not read: {problem} ({where})");

    /// <summary>Rows with the same reason and access go together, in the order the first of each was listed.</summary>
    public static IReadOnlyList<IReadOnlyList<AttentionRow>> Group(IEnumerable<AttentionRow> rows) =>
        [.. rows.GroupBy(r => (r.Detail, r.Access)).Select(g => (IReadOnlyList<AttentionRow>)[.. g])];

    public static string GroupHeading(IReadOnlyList<AttentionRow> rows) => rows.Count == 1 ? rows[0].Title : $"{N(rows.Count)} files";

    public static string ShowFiles(int count, bool shown) => shown ? "Hide the files" : $"Show the {N(count)} files";

    /// <summary>
    /// What to paste into a bug report: the version, the step and the error, and how many files it happened to. No titles
    /// or paths, since a report may be public.
    /// </summary>
    public static string BugDetails(string version, IReadOnlyList<AttentionRow> rows) =>
        string.Join(Environment.NewLine, $"Bibliotaph {version}", rows[0].Error, $"Files: {N(rows.Count)}");

    public const string CopyDetails = "Copy details";

    public const string Copied = "Copied";

    static string N(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// A file Bibliotaph couldn't fully process, and why, in words the user can act on. Trying again can't help a locked
/// file or one with DRM, so those offer opening it to unlock, or in another app.
/// </summary>
public sealed record AttentionRow(long? DocumentId, string Title, string Detail, TextAccess Access = TextAccess.Readable, string? Error = null)
{
    public bool CanRetry => DocumentId is not null && Access == TextAccess.Readable;

    public bool CanUnlock => DocumentId is not null && Access == TextAccess.Locked;

    public bool CanOpenElsewhere => DocumentId is not null && Access == TextAccess.Protected;

    /// <summary>A stage threw something nothing expected: a bug to report, with <see cref="Error"/> as what to paste.</summary>
    public bool IsBug => Error is not null;
}

/// <summary>
/// A field card's words: what it shows now, what it suggests, the evidence, and what Accept, Edit and Reject say and
/// do. "Accept all" takes every card that proposes the same values for the same field.
/// </summary>
public sealed record FieldCardText(ReviewItem Item, Vocabulary Vocabulary)
{
    ReviewIssue Issue => Item.Issue;

    public string FieldLabel => Issue.Field.Label;

    /// <summary>Why the card is here, in a line.</summary>
    public string Reason => Issue.Kind switch
    {
        ReviewKind.Conflict => "Sources disagree.",
        ReviewKind.CopiesDisagree => "You set different values on two copies of this book.",
        ReviewKind.MissingTitle => "This looks like a file's name rather than the book's title.",
        _ => "Suggested; nobody has confirmed it.",
    };

    public string CurrentText => Describe(Issue.Current, "Not set");

    public string CurrentLabel => Issue.Kind == ReviewKind.CopiesDisagree ? "This card" : "Current";

    public string SuggestedLabel => Issue.Kind == ReviewKind.CopiesDisagree ? "The other copy" : "Suggested";

    /// <summary>Sources that disagree: where the value showing now came from, too. Empty otherwise.</summary>
    public string CurrentSource => Issue.Kind == ReviewKind.Conflict && Issue.Current.Count > 0 ? FieldText.Source(Issue.Current[0].Source) + "." : "";

    public string SuggestedText => Describe(Issue.Suggested, "");

    /// <summary>What Accept adds or changes, which Reject turns down.</summary>
    public string ProposedText => Describe(Issue.Proposed, "");

    public string Evidence => Issue.Kind == ReviewKind.CopiesDisagree ? "You set this on the other copy's card before the two were joined."
        : Issue.Evidence is { } claim ? FieldText.Source(claim, pages: false) + "." : "";

    public string PageText => Issue.Evidence is { Pages.Count: > 0 } withPages
        ? (withPages.Pages.Count == 1 ? "Page " : "Pages ") + string.Join(", ", withPages.Pages.Select(p => (p + 1).ToString(CultureInfo.CurrentCulture))) + "."
        : "";

    public bool CanAccept => Issue.CanAccept;

    public bool CanAcceptAll => CanAccept && Item.GroupSize > 1;

    public string AcceptAllLabel => $"Accept all {Item.GroupSize.ToString("N0", CultureInfo.CurrentCulture)}";

    public string AcceptAllHint => $"Accept every card that suggests {ProposedText} for {FieldLabel}";

    /// <summary>Reject keeps what the field shows; a missing title has nothing to turn down, so it just keeps it.</summary>
    public bool CanReject => Issue.Kind != ReviewKind.MissingTitle || Issue.Current.Count > 0;

    // It sits under what the book has now, so it says what it keeps rather than what it turns down.
    public string RejectLabel => Issue.Kind switch
    {
        ReviewKind.MissingTitle => "Keep as is",
        ReviewKind.CopiesDisagree => "Keep this card's",
        _ => Issue.Current.Count > 0 ? "Keep current" : "Leave empty",
    };

    public string RejectHint => Issue.Kind switch
    {
        ReviewKind.MissingTitle => "Keep this title; it won't be asked about again",
        ReviewKind.Conflict or ReviewKind.CopiesDisagree => $"Keep {CurrentText}; {ProposedText} won't be suggested again",
        _ => $"{ProposedText} won't be suggested again",
    };

    public string EditLabel => Issue.Kind == ReviewKind.MissingTitle ? "Type a title" : "Edit";

    /// <summary>What the editor starts with: the suggestion, or nothing for a title still to be typed.</summary>
    public string EditStart => Issue.Kind == ReviewKind.MissingTitle ? "" : SuggestedText;

    public string EditHint => Issue.Field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Players => "4 or 3-5",
        FieldKind.Year => "2019",
        _ when Issue.Field.Multiple => "Separate values with commas",
        _ => "",
    };

    public IReadOnlyList<string> Choices => FieldText.Choices(Issue.Field, Vocabulary);

    public string Accepted => $"{FieldLabel} is now {SuggestedText}.";

    public string Rejected => Issue.Kind switch
    {
        ReviewKind.MissingTitle => "Kept the title.",
        ReviewKind.Conflict or ReviewKind.CopiesDisagree => $"Kept {CurrentText}.",
        _ => $"Rejected {ProposedText}.",
    };

    string Describe(IReadOnlyList<EffectiveValue> values, string none) =>
        values.Count == 0 ? none : string.Join(", ", values.Select(v => FieldText.Display(Issue.Field, v.Value, Vocabulary)));
}

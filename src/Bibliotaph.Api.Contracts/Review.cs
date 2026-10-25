using System.Globalization;

namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Needs review's Metadata suggestions tab as it was when the page loaded: every field card and new-term card, and the
/// Show menu's kinds and fields in the order it lists them. The page keeps them while the user works through them, so
/// a decided card can still be undone, and counts what's waiting itself. <see cref="Count"/> is every card Needs
/// review had then, as the sidebar counts them, so the page can tell when the library's suggestions have moved on.
/// <see cref="Choices"/> are each term field's vocabulary, by field key, for Edit to pick from; sent once, not per card.
/// </summary>
public sealed record ReviewSuggestions(IReadOnlyList<ReviewTermCard> Terms, IReadOnlyList<ReviewFieldCard> Fields, IReadOnlyList<ReviewShowKind> Show, int Count,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Choices);

/// <summary>An entry in the Show menu other than Everything: a kind of card, or the field suggestions are for.</summary>
public sealed record ReviewShowKind(string Key, string Name);

/// <summary>
/// One field of one book: what it shows now, what the card suggests and the evidence, with Accept, Edit and Reject.
/// <see cref="Id"/> names the card to the server, and <see cref="GroupKey"/> what it proposes, so a decision isn't
/// made on a suggestion that changed since; cards with the same group key are what Accept all takes together.
/// <see cref="Show"/> is its key in the Show menu.
/// </summary>
public sealed record ReviewFieldCard(string Id, string GroupKey, string Show, long Book, string Title, string FieldKey, string FieldLabel,
    string Reason, string CurrentLabel, string CurrentText, string CurrentSource, string SuggestedLabel, string SuggestedText, string Evidence,
    string PageText, bool CanAccept, bool CanAcceptAll, string AcceptAllLabel, string AcceptAllHint, bool CanReject, string RejectLabel,
    string RejectHint, string EditLabel, string EditStart, string EditHint, bool Multiple);

/// <summary>A name a classifier used that the vocabulary doesn't know, once for all the books that wait on it.</summary>
public sealed record ReviewTermCard(long Id, string Label, string Chip, string Detail, string? Example, string AddLabel, IReadOnlyList<ReviewTermTarget> Targets);

/// <summary>A term a new one can be filed under.</summary>
public sealed record ReviewTermTarget(string Key, string Label);

/// <summary>A field card to decide: its id and what it proposed when the page showed it.</summary>
public sealed record ReviewFieldDecision(string Id, string GroupKey);

/// <summary>Edit on a field card: what was typed instead.</summary>
public sealed record ReviewFieldEdit(string Id, string GroupKey, string Text);

/// <summary>Accept all on a field card, with the cards the page shows, which each get their own Undo.</summary>
public sealed record ReviewAcceptAll(string Id, string GroupKey, IReadOnlyList<string> Shown);

/// <summary>
/// What Accept all did: the shown cards it decided, each with its note and Undo; how many cards it decided in all, so
/// the page can leave out those it hadn't shown yet; and the banner's words with the Undo that takes them all back.
/// </summary>
public sealed record ReviewAcceptedAll(IReadOnlyList<ReviewCardDone> Cards, int Count, string Message, string? Undo);

public sealed record ReviewCardDone(string Id, string Message, string? Undo);

/// <summary>File a new term under one the vocabulary has.</summary>
public sealed record ReviewTermMap(string Target);

/// <summary>
/// Files needing attention: files with the same reason share a card. A bug's card carries what Copy details puts on
/// the clipboard.
/// </summary>
public sealed record ReviewFiles(IReadOnlyList<AttentionCard> Cards, bool CanRetryAll);

/// <summary>A card of one file or many, with Try again when any of them can be tried again.</summary>
public sealed record AttentionCard(string Heading, string Detail, bool CanRetry, string? BugDetails, IReadOnlyList<AttentionFile> Files);

/// <summary>A file on a card, which Try again can give another go when it isn't locked or protected.</summary>
public sealed record AttentionFile(long? DocumentId, string Title, bool CanRetry);

/// <summary>Try again for these files.</summary>
public sealed record ReviewRetry(IReadOnlyList<long> Documents);

/// <summary>
/// The words of Needs review that change as cards are decided, which the page works out itself: the same as the
/// Windows app's, in Processing's ReviewText.
/// </summary>
public static class ReviewWords
{
    public const int PageSize = 60;

    public const string Everything = "Everything";

    public static string Subtitle(int remaining, int files) => (remaining, files) switch
    {
        (0, 0) => "Nothing needs a second look. Everything is ready to use.",
        (1, _) => "One suggestion worth a second look. The rest is ready to use.",
        (0, _) => "No suggestions to look at, but some files need attention.",
        _ => $"{N(remaining)} suggestions worth a second look. The rest is ready to use.",
    };

    public static string SuggestionsTab(int remaining) => $"Metadata suggestions  {N(remaining)}";

    public static string FilesTab(int files) => $"Files needing attention · {N(files)}";

    public static string ShowLabel(string name, int count) => $"{name} · {N(count)}";

    public static string MoreLabel(int hidden) => $"Show {N(Math.Min(PageSize, hidden))} more ({N(hidden)} not shown)";

    public static string ShowFiles(int count, bool shown) => shown ? "Hide the files" : $"Show the {N(count)} files";

    static string N(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

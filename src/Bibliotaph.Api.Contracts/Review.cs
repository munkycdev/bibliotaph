using System.Globalization;

namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Needs review's Metadata suggestions tab as it was when the page loaded: every field card and new-term card, and the
/// Show menu's kinds and fields in the order it lists them. The page keeps them while the user works through them, so
/// a decided card can still be undone, and counts what's waiting itself. <see cref="Count"/> is every card Needs
/// review had then, as the sidebar counts them, so the page can tell when the library's suggestions have moved on.
/// <see cref="Choices"/> are each term field's vocabulary, by field key, for Edit to pick from; sent once, not per card.
/// <see cref="Offers"/> are the other cards, in the order the page shows them after the new terms, and
/// <see cref="Overlaps"/> the terms that may be the same thing, shown between the two.
/// </summary>
public sealed record ReviewSuggestions(IReadOnlyList<ReviewTermCard> Terms, IReadOnlyList<ReviewFieldCard> Fields, IReadOnlyList<ReviewShowKind> Show, int Count,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Choices, IReadOnlyList<ReviewOfferCard> Offers, IReadOnlyList<ReviewOverlapCard> Overlaps);

/// <summary>
/// Terms of one vocabulary that may be the same thing under two names, found by their names or by the AI: combine
/// them into the one picked from <see cref="Terms"/> (<see cref="KeepId"/> first, the term most books use), or keep
/// them apart for good. <see cref="Stamp"/> is what the card showed, so an answer isn't given to a group that changed.
/// </summary>
public sealed record ReviewOverlapCard(string Id, string Stamp, string Label, string Chip, string Detail, IReadOnlyList<ReviewOverlapTerm> Terms, long KeepId);

/// <summary>One term of an overlap card: "Forgotten Realms · 12 books".</summary>
public sealed record ReviewOverlapTerm(long Id, string Label, string Text);

/// <summary>Combine the group's other terms into <see cref="KeepId"/>.</summary>
public sealed record ReviewOverlapCombine(string Stamp, long KeepId);

public sealed record ReviewOverlapKeepApart(string Stamp);

/// <summary>
/// A card that asks one question about a book, a folder or a collection, answered with one of its buttons: a new
/// version, an image pack, a book owned elsewhere, places to check in a new version, a book cataloged wrong, a folder
/// rule, a collection from a folder, or books for a collection. <see cref="Kind"/> is its key in the Show menu.
/// <see cref="Stamp"/> is what the card said, so an answer isn't given to a card that changed since the page showed
/// it. <see cref="Dismiss"/> is the choice the × gives, on the cards that have one. <see cref="Book"/> is the card's
/// title link, to the book's details; <see cref="Links"/> any other books it names. <see cref="Tiles"/> are covers to
/// show what a pack holds, <see cref="Places"/> a new version's places to check and <see cref="Found"/> those found
/// by themselves. <see cref="Fix"/> is the book whose details Fix it opens for Cataloged wrong…. <see cref="Proposed"/>
/// are the adventures a file looks like it holds, to tick and edit; <see cref="Pages"/> where Set its pages starts for
/// an adventure whose pages weren't found.
/// </summary>
public sealed record ReviewOfferCard(string Id, string Kind, string Stamp, string Title, string Chip, string Heading, string? Path, string? Evidence,
    string Detail, IReadOnlyList<ReviewChoice> Choices, string? Dismiss, ReviewLink? Book, IReadOnlyList<ReviewLink> Links,
    IReadOnlyList<string> Tiles, IReadOnlyList<ReviewPlace> Places, int FoundCount, IReadOnlyList<string> Found, string FoundMore, long? Fix,
    IReadOnlyList<ReviewProposedPart>? Proposed = null, ReviewPartPages? Pages = null);

/// <summary>
/// An adventure a file looks like it holds: its title and pages as the file numbers them (<see cref="To"/> empty for
/// one page), <see cref="PagesText"/> as they read, and the levels and party its blurb gave.
/// </summary>
public sealed record ReviewProposedPart(string Title, string From, string To, string PagesText, string Levels, string Players);

/// <summary>An adventure's first and last pages as typed; the last empty for one page.</summary>
public sealed record ReviewPartPages(string From, string To);

/// <summary>Accept on a proposal: the ticked adventures, by their place in the card's list, as typed.</summary>
public sealed record ReviewPartsAccept(string Id, string Stamp, IReadOnlyList<ReviewPartRow> Parts);

public sealed record ReviewPartRow(int Index, string Title, string From, string To);

/// <summary>Set its pages, for an adventure whose pages weren't found.</summary>
public sealed record ReviewPartPagesSet(string Id, string Stamp, string From, string To);

/// <summary>A button on an offer card; <see cref="Primary"/> is the card's main answer.</summary>
public sealed record ReviewChoice(string Key, string Label, string Hint, bool Primary);

/// <summary>A book a card names, which opens its details; <see cref="Hint"/> is its tooltip.</summary>
public sealed record ReviewLink(long EntryId, string Label, string Hint);

/// <summary>
/// A place in a new version to check: its words, where it opens before and after Use this page, and whether it was
/// checked already.
/// </summary>
public sealed record ReviewPlace(string Key, string Heading, string Where, string CheckedWhere, bool Checked);

/// <summary>An answer to an offer card, given to the card as the page showed it.</summary>
public sealed record ReviewOfferAnswer(string Id, string Stamp, string Choice);

/// <summary>Use this page for one place on a new version's card.</summary>
public sealed record ReviewPlaceUse(string Id, string Stamp, string Place);

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

/// <summary>
/// A file on a card, which Try again can give another go when it isn't locked or protected. A locked one opens in the
/// reader to be unlocked; one with protection Bibliotaph can't open is downloaded for another app. Either opens on its
/// card (<paramref name="EntryId"/>).
/// </summary>
public sealed record AttentionFile(long? DocumentId, string Title, bool CanRetry, bool CanUnlock = false, bool CanOpenElsewhere = false,
    long? EntryId = null);

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

    public const string DismissHint = "Close. Bibliotaph won't suggest this again, and Undo brings it back.";

    public const string FoundHeading = "Found their page, nothing to do:";

    public const string UseHint = "This place opens at the page suggested from now on";

    public static string AllChecked(string bookTitle) => $"Every place in the new version of {bookTitle} is checked.";

    public const string PagesHint = "Type pages as the file numbers them, or PDF page numbers.";

    public const string AcceptPartsHint = "Give each ticked adventure a card of its own. The file isn't changed.";

    public const string EditPartsHint = "Change the adventures' titles and pages";

    public const string NotSplitHint = "It's one book. Bibliotaph won't ask about this file again.";

    /// <summary>"Make 3 cards", for the ticked adventures.</summary>
    public static string AcceptParts(int ticked) => ticked == 1 ? "Make 1 card" : $"Make {N(ticked)} cards";

    /// <summary>A proposed adventure's pages as typed, before the server reads them.</summary>
    public static string TypedPages(string from, string to) => to.Trim().Length == 0 ? $"p. {from}" : $"pp. {from}–{to}";

    /// <summary>A new version's card, counting the places that still need a look as they're checked.</summary>
    public static string RevisionHeading(string bookTitle, int found, int look)
    {
        var foundText = found == 1 ? "1 place found its page" : $"{N(found)} places found their page";
        var lookText = look == 1 ? "1 needs a look" : $"{N(look)} need a look";
        return $"New version of {bookTitle}: {foundText}, {lookText}";
    }

    static string N(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>Settings › Preferences: whether every suggestion waits for a second look, or only those Bibliotaph is unsure of.</summary>
public sealed record ReviewMode(bool All);

using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A book a companion can go with, as the picker lists it: its title and its byline.</summary>
public sealed record SourceBookChoice(EntryId EntryId, string Title, string Detail);

/// <summary>
/// The book a companion goes with, as its details show it: the title, how Bibliotaph knew ("It sits beside the book in
/// “Maps”", "You chose it"), and whether the user set or kept it.
/// </summary>
public sealed record SourceBookLink(EntryId Book, string Title, string How, bool Confirmed);

/// <summary>A card that goes with a book, as the book's details list it.</summary>
public sealed record CompanionView(EntryId EntryId, string Title, string How, bool Confirmed);

/// <summary>
/// "Goes with" in a card's details: the book it goes with, the books proposed for it and waiting in Needs review, the
/// cards that go with it when it is the book, and whether it can go with one at all.
/// </summary>
public sealed record SourceBookDetails(SourceBookLink? GoesWith, IReadOnlyList<SourceBookChoice> Proposed, IReadOnlyList<CompanionView> Companions, bool CanGoWith)
{
    public static SourceBookDetails None { get; } = new(null, [], [], false);
}

/// <summary>What linking or unlinking did, in words, with what undoes it and the cards that couldn't take part.</summary>
public sealed record SourceBookChange(string Message, IReadOnlyList<EntryId> Changed, IReadOnlyList<EntryId> Refused, CompanionUndo Undo);

/// <summary>
/// The user's side of linking maps, handouts, character sheets and packs to the book they go with: the details' Goes
/// with row and its book picker, Select's Edit metadata for many cards at once, and Undo. Every change is projected at
/// once, the companions and the books before and after, so the companions show the book's values and the books count
/// them.
/// </summary>
public sealed class SourceBooksService(CompanionStore store, EntryStore entries, LibraryQueries library, IndexQueries queries, MetadataProjector projector,
    ILogger<SourceBooksService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<SourceBooksService>.Instance;

    /// <summary>Raised after links changed, with the companions, on the caller's thread: Needs review and the Summary follow.</summary>
    public event EventHandler<IReadOnlyCollection<EntryId>>? Changed;

    /// <summary>What the details show for <paramref name="entryId"/>.</summary>
    public async Task<SourceBookDetails> GetDetailsAsync(EntryId entryId, CancellationToken ct = default)
    {
        var kind = await entries.GetKindAsync(entryId, ct);
        if (kind is null) return SourceBookDetails.None;
        var all = await store.GetAllAsync(entryId, ct);
        var companions = await store.GetCompanionsAsync(entryId, ct);
        var live = all.FirstOrDefault(l => l.InEffect);
        var proposed = all.Where(l => l.State == CompanionState.Proposed).OrderByDescending(l => l.Evidence?.Score ?? 0).ToList();
        var titles = await queries.GetCardTitlesAsync([.. all.Select(l => l.Book), .. companions.Select(c => c.Companion)], ct);
        string Title(EntryId id) => titles.TryGetValue(id, out var t) ? t.Title : "Untitled";
        var goesWith = live is null ? null : new SourceBookLink(live.Book, Title(live.Book), SourceBookText.How(live), live.State == CompanionState.Confirmed);
        var canGoWith = companions.Count == 0 && kind is EntryKind.Whole or EntryKind.Pack && !await IsInPackAsync(entryId, ct);
        return new SourceBookDetails(goesWith,
            [.. proposed.Select(p => new SourceBookChoice(p.Book, Title(p.Book), p.Evidence?.Describe() ?? ""))],
            [.. companions.Select(c => new CompanionView(c.Companion, Title(c.Companion), SourceBookText.How(c), c.State == CompanionState.Confirmed))
                .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)],
            canGoWith);
    }

    /// <summary>A file in a pack is a member, not a card; its pack is what goes with a book.</summary>
    async Task<bool> IsInPackAsync(EntryId entryId, CancellationToken ct) =>
        await entries.GetCurrentDocumentAsync(entryId, ct) is { } documentId && await entries.GetCardAsync(documentId, ct) is { } card && card != entryId;

    /// <summary>
    /// The picker: the books whose title or details have <paramref name="text"/>, best first, or the books opened
    /// lately while nothing is typed. Never a pack, a card that goes with a book itself, or any of <paramref name="excluding"/>.
    /// </summary>
    public async Task<IReadOnlyList<SourceBookChoice>> FindBooksAsync(string text, IReadOnlyCollection<EntryId> excluding, int limit = 20, CancellationToken ct = default)
    {
        var typed = text.Trim().Length > 0;
        var filter = new LibraryFilter(Sort: typed ? LibrarySort.Relevance : LibrarySort.RecentlyOpened, HideCompanions: true);
        var plan = typed ? SearchPlan.From(SearchQuery.Parse(text)) : new SearchPlan();
        var found = await library.SearchDocumentsAsync(plan, filter, limit + excluding.Count + 20, ct);
        var skip = excluding.ToHashSet();
        return [.. found.Where(e => !e.IsPack && !skip.Contains(e.EntryId)).DistinctBy(e => e.EntryId).Take(limit)
            .Select(e => new SourceBookChoice(e.EntryId, e.Title, CardText.Byline(e)))];
    }

    /// <summary>"Goes with": each of <paramref name="companions"/> goes with <paramref name="book"/>, set by the user.</summary>
    public async Task<SourceBookChange> LinkAsync(IReadOnlyCollection<EntryId> companions, EntryId book, CancellationToken ct = default)
    {
        var change = await store.LinkAsync(companions, book, ct);
        _log.LogInformation("Linked {Count} cards to book {Book}; {Refused} couldn't go with it", change.Changed.Count, book, change.Refused.Count);
        await ProjectAsync(change.Undo, [book], ct);
        var title = (await queries.GetCardTitlesAsync([book], ct)).TryGetValue(book, out var t) ? t.Title : "the book";
        return new SourceBookChange(SourceBookText.Linked(change.Changed.Count, title, change.Refused.Count), change.Changed, change.Refused, change.Undo);
    }

    /// <summary>
    /// Takes the cards away from the book they go with. <paramref name="refuse"/> is "Not from this book": the rules
    /// won't suggest it again. Without, they may suggest one again.
    /// </summary>
    public async Task<SourceBookChange> UnlinkAsync(IReadOnlyCollection<EntryId> companions, bool refuse, CancellationToken ct = default)
    {
        var change = await store.UnlinkAsync(companions, refuse, ct);
        await ProjectAsync(change.Undo, [], ct);
        return new SourceBookChange(SourceBookText.Unlinked(change.Changed.Count, refuse), change.Changed, [], change.Undo);
    }

    /// <summary>"Keep": the suggested book becomes the user's choice.</summary>
    public async Task<SourceBookChange> ConfirmAsync(IReadOnlyCollection<EntryId> companions, CancellationToken ct = default)
    {
        var change = await store.ConfirmAsync(companions, ct);
        await ProjectAsync(change.Undo, [], ct);
        return new SourceBookChange(SourceBookText.Kept(change.Changed.Count), change.Changed, [], change.Undo);
    }

    /// <summary>
    /// The answer to a "Goes with a book?" card: <paramref name="book"/>, or none of those proposed when it is null.
    /// </summary>
    public async Task<SourceBookChange> AnswerAsync(EntryId companion, EntryId? book, CancellationToken ct = default) =>
        book is { } chosen ? await LinkAsync([companion], chosen, ct) : await UnlinkAsync([companion], refuse: true, ct);

    /// <summary>Puts the links back as they were before a change.</summary>
    public async Task UndoAsync(CompanionUndo undo, CancellationToken ct = default)
    {
        var after = await store.GetLiveAsync(undo.Companions, ct);
        await store.RestoreAsync(undo, ct);
        await ProjectAsync(undo, [.. after.Values.Select(l => l.Book)], ct);
    }

    async Task ProjectAsync(CompanionUndo undo, IReadOnlyCollection<EntryId> books, CancellationToken ct)
    {
        if (undo.Companions.Count == 0) return;
        var touched = new HashSet<EntryId>(undo.Companions);
        touched.UnionWith(books);
        touched.UnionWith(undo.Before.Where(b => b.InEffect).Select(b => b.Book));
        await projector.ProjectAsync(touched, ct);
        Changed?.Invoke(this, undo.Companions);
    }
}

/// <summary>The words for links between companions and books, shared by the Windows app and the web client.</summary>
public static class SourceBookText
{
    /// <summary>How the link was made: the rule's evidence, or that the user chose it.</summary>
    public static string How(CompanionLinkInfo link) =>
        link.State == CompanionState.Confirmed
            ? link.Evidence is { } evidence ? $"{evidence.Describe()}. You kept it." : "You chose it."
            : link.Evidence?.Describe() is { Length: > 0 } how ? $"{how}." : "Suggested.";

    /// <summary>"Maps and handouts (3)", over the cards that go with a book.</summary>
    public static string CompanionsHeading(int count) => count == 0 ? "MAPS AND HANDOUTS" : $"MAPS AND HANDOUTS ({count})";

    public static string Linked(int count, string title, int refused) =>
        (count == 0 ? "Nothing changed." : count == 1 ? $"It goes with “{title}” now." : $"{count} cards go with “{title}” now.")
        + (refused == 0 ? "" : refused == 1 ? " One card can't go with it: a pack's file, an adventure, the book itself or a book others go with."
            : $" {refused} cards can't go with it: packs' files, adventures, the book itself or books others go with.");

    public static string Unlinked(int count, bool refuse) =>
        count == 0 ? "Nothing changed."
        : refuse ? count == 1 ? "It won't be suggested for that book again." : $"{count} cards won't be suggested for their books again."
        : count == 1 ? "It doesn't go with a book now." : $"{count} cards don't go with a book now.";

    public static string Kept(int count) => count <= 1 ? "Kept." : $"Kept for {count} cards.";
}

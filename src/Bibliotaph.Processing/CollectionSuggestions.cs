using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>A book a suggestion would add, with the title its card shows and the document that opens.</summary>
public sealed record SuggestedBook(EntryId EntryId, string Title, long? DocumentId);

/// <summary>
/// "Add these 4 books to “X”?": books outside a collection that share what most of its books share. <see cref="Series"/>
/// is the series most of its books are in, as written; <see cref="Setting"/> and <see cref="System"/> the setting and
/// game system most are for, as term keys. Either the series or the setting and system may be null.
/// </summary>
public sealed record CollectionSuggestion(long CollectionId, string Name, IReadOnlyList<SuggestedBook> Books, string? Series = null, string? Setting = null,
    string? System = null)
{
    public IReadOnlyList<EntryId> EntryIds => [.. Books.Select(b => b.EntryId)];
}

/// <summary>
/// Books for collections. For each collection with <see cref="MinimumBooks"/> books added to it or more, finds what
/// most of them share (<see cref="MostShared"/>): the same series, or the same setting and game system. Books outside
/// it, and outside the collections inside it, that share that too are proposed in Needs review. Accept adds them, and a
/// collection that follows its folder keeps following it; Never is remembered for that collection and each of those
/// books, so they aren't suggested for it again while new books still can be; Not now puts the card away until the
/// list is made again.
/// </summary>
public sealed class CollectionSuggestions(CollectionStore collectionStore, CollectionsService collections, MetadataStore metadata, LibraryStore library,
    IndexQueries queries)
{
    /// <summary>
    /// What "most" of a collection's books means: a value held by at least <see cref="SharePercent"/> percent of the
    /// books added to it, and by at least <see cref="MinimumBooks"/> of them. So three of five books in one series is
    /// enough and three of six isn't, and two books never are, however small the collection.
    /// </summary>
    public const int SharePercent = 60;

    /// <inheritdoc cref="SharePercent"/>
    public const int MinimumBooks = 3;

    readonly Lock _lock = new();
    readonly HashSet<string> _notNow = [with(StringComparer.Ordinal)];

    /// <summary>
    /// The value most of these books share, from each book's values in comparison form, by the rule in
    /// <see cref="SharePercent"/>; the one most books hold when several do. Null when none does.
    /// </summary>
    public static string? MostShared(IReadOnlyList<IEnumerable<string>> books)
    {
        // Rounded up, in whole numbers: 60% of 5 books is 3, of 6 books 4.
        var needed = Math.Max(MinimumBooks, ((SharePercent * books.Count) + 99) / 100);
        return books.SelectMany(b => b.Distinct(StringComparer.Ordinal))
            .CountBy(v => v, StringComparer.Ordinal)
            .Where(c => c.Value >= needed)
            .OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => c.Key)
            .FirstOrDefault();
    }

    /// <summary>How many suggestions <see cref="GetSuggestionsAsync"/> lists, for the sidebar.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default) => (await GetSuggestionsAsync(ct)).Count;

    /// <summary>The suggestions, by collection name, each with its books by title.</summary>
    public async Task<IReadOnlyList<CollectionSuggestion>> GetSuggestionsAsync(CancellationToken ct = default)
    {
        var items = await collectionStore.GetItemsAsync(ct);
        var big = items.Where(i => i.Value.Count >= MinimumBooks).ToList();
        if (big.Count == 0) return [];

        var known = await metadata.GetManyAsync([.. big.SelectMany(m => m.Value).Distinct()], ct);
        var shared = new List<Shared>();
        foreach (var (id, books) in big)
        {
            var computed = books.Select(b => known.TryGetValue(b, out var m) ? m.Compute() : EffectiveMetadata.Empty).ToList();
            var series = MostShared([.. computed.Select(c => Normalized(c, MetadataFields.Series))]);
            var setting = MostShared([.. computed.Select(c => Normalized(c, MetadataFields.Settings))]);
            var system = MostShared([.. computed.Select(c => Normalized(c, MetadataFields.System))]);
            if (setting is null || system is null) (setting, system) = (null, null);
            if (series is null && setting is null) continue;
            // The series as the collection's books write it, for the card.
            var written = series is null ? null
                : computed.SelectMany(c => c[MetadataFields.Series].Values).First(v => v.Normalized == series).Value;
            shared.Add(new Shared(id, series, written, setting, system));
        }
        if (shared.Count == 0) return [];

        // Books with one of those values, a rough cut read in full and checked below.
        var candidates = (await metadata.FindAsync(MetadataFields.Series, [.. shared.Where(s => s.Series is not null).Select(s => s.Series!).Distinct()], ct))
            .Union(await metadata.FindAsync(MetadataFields.Settings, [.. shared.Where(s => s.Setting is not null).Select(s => s.Setting!).Distinct()], ct))
            .Intersect(await library.GetVisibleEntryIdsAsync(ct: ct))
            .ToList();
        if (candidates.Count == 0) return [];
        var effective = (await metadata.GetManyAsync(candidates, ct)).ToDictionary(m => m.Key, m => m.Value.Compute());
        var declined = await collectionStore.GetDeclinedAsync(ct);
        var all = await collectionStore.ListAsync(ct);
        var parents = all.ToDictionary(c => c.Id, c => c.ParentId);
        var names = all.ToDictionary(c => c.Id, c => c.Name);
        HashSet<string> later;
        lock (_lock) later = [.. _notNow];

        var found = new List<(Shared Shared, List<EntryId> Books)>();
        foreach (var s in shared)
        {
            var subtree = Subtree(parents, s.Id);
            var inside = items.Where(i => subtree.Contains(i.Key)).SelectMany(i => i.Value).ToHashSet();
            var books = effective
                .Where(e => !inside.Contains(e.Key) && !declined.Contains((s.Id, e.Key)) && s.Matches(e.Value))
                .Select(e => e.Key).ToList();
            if (books.Count > 0 && !later.Contains(Key(s.Id, books))) found.Add((s, books));
        }
        if (found.Count == 0) return [];
        var cards = await queries.GetCardTitlesAsync([.. found.SelectMany(f => f.Books).Distinct()], ct);
        return [.. found
            .Select(f => new CollectionSuggestion(f.Shared.Id, names[f.Shared.Id],
                [.. f.Books.Select(b => cards.TryGetValue(b, out var card) ? new SuggestedBook(b, card.Title, card.DocumentId) : new SuggestedBook(b, $"Book {b}", null))
                    .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)],
                f.Shared.Written, f.Shared.Setting, f.Shared.System))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.CollectionId)];
    }

    /// <summary>
    /// Answers a card: Accept adds its books, Never declines them for that collection, Not now puts the card away.
    /// Returns what undoes the answer.
    /// </summary>
    public async Task<Func<Task>> AnswerAsync(CollectionSuggestion suggestion, CollectionAnswer answer, CancellationToken ct = default)
    {
        switch (answer)
        {
            case CollectionAnswer.Accept:
                var added = await collections.JoinAsync(suggestion.CollectionId, suggestion.EntryIds, ct);
                return () => collections.LeaveAsync(suggestion.CollectionId, added, CancellationToken.None);
            case CollectionAnswer.Never:
                var declined = await collectionStore.DeclineAsync(suggestion.CollectionId, suggestion.EntryIds, ct);
                return () => collectionStore.UndeclineAsync(suggestion.CollectionId, declined, CancellationToken.None);
            default:
                var key = Key(suggestion.CollectionId, suggestion.EntryIds);
                lock (_lock) _notNow.Add(key);
                return () =>
                {
                    lock (_lock) _notNow.Remove(key);
                    return Task.CompletedTask;
                };
        }
    }

    /// <summary>Making the list again brings back the cards put away with Not now.</summary>
    public void ClearNotNow()
    {
        lock (_lock) _notNow.Clear();
    }

    /// <summary>A card put away with Not now stays away for that collection and those books; one with another book is new.</summary>
    static string Key(long collectionId, IEnumerable<EntryId> books) =>
        collectionId.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(',', books.Select(b => b.Value).Order().Select(v => v.ToString(CultureInfo.InvariantCulture)));

    static IEnumerable<string> Normalized(EffectiveMetadata metadata, MetadataField field) => metadata[field].Values.Select(v => v.Normalized);

    /// <summary>A collection and every collection under it.</summary>
    static HashSet<long> Subtree(Dictionary<long, long?> parents, long root)
    {
        var result = new HashSet<long> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (id, parent) in parents)
                if (parent is { } p && result.Contains(p) && result.Add(id)) grew = true;
        }
        return result;
    }

    /// <summary>What most of a collection's books share: its series, or its setting and system, in comparison form.</summary>
    sealed record Shared(long Id, string? Series, string? Written, string? Setting, string? System)
    {
        public bool Matches(EffectiveMetadata book) =>
            (Series is not null && Normalized(book, MetadataFields.Series).Contains(Series, StringComparer.Ordinal))
            || (Setting is not null && Normalized(book, MetadataFields.Settings).Contains(Setting, StringComparer.Ordinal)
                && Normalized(book, MetadataFields.System).Contains(System!, StringComparer.Ordinal));
    }
}

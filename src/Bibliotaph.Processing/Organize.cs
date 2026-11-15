using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>What an Organize drop did, in words, and what undoes it, or null when nothing changed.</summary>
public sealed record OrganizeChange(string Message, Func<CancellationToken, Task>? Undo)
{
    public static OrganizeChange Nothing(string message) => new(message, null);
}

/// <summary>
/// The changes Organize mode's drops make that no Select-bar action already does, in the Windows app and on the web:
/// cards going with a book, coming away from it, keeping the books the rules suggested, and files joining a pack by
/// its own name. Same book and a new pack go through their dialogs as the bar's buttons do.
/// </summary>
public sealed class OrganizeService(SourceBooksService sourceBooks, PackService packs, LibraryStore library, LibraryQueries queries)
{
    /// <summary>The cards dropped on <paramref name="book"/> go with it.</summary>
    public async Task<OrganizeChange> GoWithAsync(IReadOnlyCollection<EntryId> cards, EntryId book, CancellationToken ct = default)
    {
        var change = await sourceBooks.LinkAsync(cards, book, ct);
        return Of(change);
    }

    /// <summary>
    /// Dropped on "Not with a book": the cards no longer go with their books, and the rules won't suggest those books
    /// for them again, as the details' Not from this book does.
    /// </summary>
    public async Task<OrganizeChange> NotWithABookAsync(IReadOnlyCollection<EntryId> cards, CancellationToken ct = default)
    {
        var change = await sourceBooks.UnlinkAsync(cards, refuse: true, ct);
        return Of(change);
    }

    /// <summary>Keep these: the books the rules suggested for the cards become the user's own choice.</summary>
    public async Task<OrganizeChange> KeepAsync(IReadOnlyCollection<EntryId> cards, CancellationToken ct = default)
    {
        var change = await sourceBooks.ConfirmAsync(cards, ct);
        return Of(change);
    }

    /// <summary>
    /// The files dropped on <paramref name="pack"/> join it, and it keeps its name, so no dialog asks for one. Nothing
    /// changes when they can't go together any more.
    /// </summary>
    public async Task<OrganizeChange> JoinPackAsync(IReadOnlyCollection<EntryId> cards, EntryId pack, CancellationToken ct = default)
    {
        var entries = await queries.GetEntriesAsync([.. cards.Append(pack).Distinct()], ct);
        if (entries.Count != cards.Append(pack).Distinct().Count() || !SelectionRules.CanPack(entries)) return OrganizeChange.Nothing(PackTogetherPlan.Gone);
        var plan = await PackTogetherPlan.LoadAsync(library, entries, ct);
        if (await packs.PackTogetherAsync([.. entries.Select(e => e.EntryId)], plan.Name, ct) is not { } result) return OrganizeChange.Nothing(PackTogetherPlan.Gone);
        return new OrganizeChange(plan.Done(plan.Name), c => packs.UndoPackTogetherAsync(result, c));
    }

    OrganizeChange Of(SourceBookChange change) =>
        new(change.Message, change.Changed.Count == 0 ? null : c => sourceBooks.UndoAsync(change.Undo, c));

    /// <summary>The words for a change that couldn't be undone.</summary>
    public const string UndoFailed = "That couldn't be undone. Change it in the card's details instead.";
}

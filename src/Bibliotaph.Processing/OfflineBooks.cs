using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// A file kept offline for preparation: the document, the card it shows under, whether that card is a favorite, and
/// the binders it's in.
/// </summary>
public sealed record PreparedBook(long DocumentId, EntryId EntryId, bool Favorite, IReadOnlyList<string> Binders)
{
    /// <summary>Why it's kept, to follow "because it's": "a favorite", "in the binder “Night one”", "a favorite and in 2 binders".</summary>
    public string Why
    {
        get
        {
            var binders = Binders.Count switch
            {
                0 => null,
                1 => $"in the binder “{Binders[0]}”",
                _ => $"in {Binders.Count} binders",
            };
            return (Favorite, binders) switch
            {
                (true, null) => "a favorite",
                (true, _) => $"a favorite and {binders}",
                _ => binders ?? "",
            };
        }
    }
}

/// <summary>
/// The files that favorites and binders need, which a device keeps offline (<see cref="OfflineKeeper"/> on a PC or Mac,
/// the browser on the web): every file of a favorite card (each of a pack's), and the file each binder item opens,
/// whole books and page ranges alike. A page of a book owned online isn't a file, and a book owned elsewhere has none.
/// </summary>
public sealed class OfflineBooks(FavoriteStore favorites, EntryStore entries, SessionsService sessions)
{
    public async Task<IReadOnlyList<PreparedBook>> ListAsync(CancellationToken ct = default)
    {
        var books = new Dictionary<long, (EntryId Entry, bool Favorite, List<string> Binders)>();
        void Add(long documentId, EntryId entry, string? binder)
        {
            if (!books.TryGetValue(documentId, out var book)) books[documentId] = book = (entry, false, []);
            if (binder is null) books[documentId] = book with { Favorite = true };
            else if (!book.Binders.Contains(binder, StringComparer.Ordinal)) book.Binders.Add(binder);
        }

        var hearts = await favorites.GetAsync(null, ct);
        foreach (var card in await entries.GetCurrentAsync(hearts, ct))
            foreach (var document in DocumentsOf(card)) Add(document, card.EntryId, null);

        foreach (var pack in await sessions.ListAsync(ct))
        {
            if (await sessions.GetAsync(pack.Id, ct) is not { } contents) continue;
            var items = contents.Items.Where(i => i.Link is null).ToList();
            // A whole book is all of its files, which a pack's card has several of; a page range is the file it opens in.
            var wholes = items.Where(i => i.Range is null).Select(i => i.EntryId).Distinct().ToList();
            foreach (var card in await entries.GetCurrentAsync(wholes, ct))
                foreach (var document in DocumentsOf(card)) Add(document, card.EntryId, pack.Title);
            var ranged = items.Where(i => i.Range is not null).ToList();
            if (ranged.Count == 0) continue;
            var targets = await sessions.ResolveAsync(ranged, ct);
            foreach (var item in ranged)
                if (targets.GetValueOrDefault(item.Id) is { DocumentId: { } document }) Add(document, item.EntryId, pack.Title);
        }
        return [.. books.OrderBy(b => b.Key).Select(b => new PreparedBook(b.Key, b.Value.Entry, b.Value.Favorite, b.Value.Binders))];
    }

    static IEnumerable<long> DocumentsOf(EntryDocument card) =>
        card.Members is { Count: > 0 } members ? members.Select(m => m.DocumentId) : card.DocumentId is { } document ? [document] : [];
}

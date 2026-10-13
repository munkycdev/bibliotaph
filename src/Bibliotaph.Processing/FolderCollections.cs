using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// A folder Needs review asks about: "Make a collection “X” from these 12 books?". <see cref="Books"/> are the books
/// directly in it; <see cref="ParentName"/> is the collection made from a folder above it, which the new one would go
/// inside.
/// </summary>
public sealed record FolderCollectionProposal(FolderKey Folder, string RootPath, IReadOnlyList<EntryId> Books, string? ParentName = null)
{
    /// <summary>The folder's own name, which the collection takes.</summary>
    public string Name => Folder.Name;

    public string FullPath => Path.Combine(RootPath, Folder.Folder);
}

/// <summary>How the user answered a Needs review card about a collection.</summary>
public enum CollectionAnswer
{
    Accept,

    /// <summary>Not now: the card waits until the list is made again.</summary>
    NotNow,

    /// <summary>Never: the card isn't asked again.</summary>
    Never,
}

/// <summary>
/// Collections from folders. A folder in a library folder with <see cref="Threshold"/> books or more directly in it is
/// proposed in Needs review as a collection named after it, unless its name only names vocabulary terms ("D&amp;D 5e",
/// "Adventures"), which the fields already sort by, or its books are all in one collection already. A collection made
/// from a folder inside another folder made into one goes inside that collection. Accepted, the collection follows its
/// folder: books that land in the folder later join it (<see cref="FollowAsync"/>), until the user adds or removes a
/// book by hand. Never is remembered with the folder; Not now only puts the card away until the list is made again.
/// </summary>
public sealed class FolderCollections(
    FolderCollectionStore store,
    LibraryStore library,
    CollectionStore collectionStore,
    CollectionsService collections,
    VocabularyStore vocabularies,
    PackService? packs = null)
{
    /// <summary>Books directly in a folder before it is proposed, so a book with its two handouts doesn't produce a card.</summary>
    public const int Threshold = 3;

    readonly Lock _lock = new();
    readonly HashSet<FolderKey> _notNow = [];

    /// <summary>How many folders <see cref="GetProposalsAsync"/> lists, for the sidebar.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default) => (await GetProposalsAsync(ct)).Count;

    /// <summary>The folders to ask about, by library folder and then path, so a folder comes before those inside it.</summary>
    public async Task<IReadOnlyList<FolderCollectionProposal>> GetProposalsAsync(CancellationToken ct = default)
    {
        var answered = await store.ListAsync(ct);
        var asked = answered.Select(a => a.Folder).ToHashSet();
        HashSet<FolderKey> later;
        lock (_lock) later = [.. _notNow];
        var vocabulary = await vocabularies.GetAsync(ct);
        // A folder of images waiting to be answered as a pack is asked about once that is settled.
        var waitingPacks = packs is null ? [] : (await packs.GetProposalsAsync(ct)).Select(p => p.Place).ToList();
        var items = await collectionStore.GetItemsAsync(ct);
        var names = (await collectionStore.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var proposals = new List<FolderCollectionProposal>();
        foreach (var (folder, rootPath, books) in await library.GetBooksByFolderAsync(ct))
        {
            if (folder.Folder.Length == 0 || books.Count < Threshold || asked.Contains(folder) || later.Contains(folder)) continue;
            if (NamesTerms(folder.Name, vocabulary)) continue;
            if (waitingPacks.Any(p => string.Equals(p.RootPath, rootPath, StringComparison.OrdinalIgnoreCase) && new FolderKey(folder.RootId, p.FolderPath) == folder)) continue;
            if (items.Values.Any(held => books.All(held.Contains))) continue;
            var parent = ParentOf(folder, answered, names.ContainsKey);
            proposals.Add(new FolderCollectionProposal(folder, rootPath, books, parent is { } id ? names[id] : null));
        }
        return [.. proposals.OrderBy(p => p.RootPath, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Folder.Folder.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether a folder's name is only vocabulary terms: a system, an edition, a type, a publisher and so on, or a run
    /// of them. Those folders sort books by what the fields already say, so a collection would add nothing.
    /// </summary>
    public static bool NamesTerms(string name, Vocabulary vocabulary) =>
        vocabulary.Terms.Select(t => t.Vocabulary).Distinct(StringComparer.Ordinal).Any(v => vocabulary.Resolve(v, name) is not null)
        || vocabulary.NamesOnlyTerms(name);

    /// <summary>
    /// Answers a card. Accept makes the collection, with the folder's books as they are now, inside the collection made
    /// from the nearest folder above it, and takes in the collections made from folders inside it that sat beside it.
    /// Returns what undoes the answer, or null when the folder was answered meanwhile or has no books left.
    /// </summary>
    public async Task<Func<Task>?> AnswerAsync(FolderCollectionProposal proposal, CollectionAnswer answer, CancellationToken ct = default)
    {
        if (answer == CollectionAnswer.NotNow)
        {
            lock (_lock) _notNow.Add(proposal.Folder);
            return () =>
            {
                lock (_lock) _notNow.Remove(proposal.Folder);
                return Task.CompletedTask;
            };
        }
        var answered = await store.ListAsync(ct);
        if (answered.Any(a => a.Folder == proposal.Folder)) return null;
        if (answer == CollectionAnswer.Never)
        {
            var declined = await store.AnswerAsync(proposal.Folder, null, ct);
            return () => store.ForgetAsync(declined, CancellationToken.None);
        }

        var books = await library.GetEntriesInFolderAsync(proposal.Folder, ct);
        if (books.Count == 0) return null;
        var existing = (await collectionStore.ListAsync(ct)).ToDictionary(c => c.Id);
        var parent = ParentOf(proposal.Folder, answered, existing.ContainsKey);
        var made = await collections.CreateAsync(proposal.Name, parent, ct: ct);
        var row = await store.AnswerAsync(proposal.Folder, made.Id, ct);
        await collections.JoinAsync(made.Id, books, ct);
        FolderCollectionInfo[] withThis = [.. answered, new FolderCollectionInfo(row, proposal.Folder, FolderCollectionState.Linked, made.Id)];
        foreach (var inner in answered)
            if (inner.CollectionId is { } innerId && existing.TryGetValue(innerId, out var info) && info.ParentId == parent
                && ParentOf(inner.Folder, withThis, id => id == made.Id || existing.ContainsKey(id)) == made.Id)
                await collections.MoveAsync(innerId, made.Id, ct);
        // Deleting it moves those inner collections back up a level, where they were.
        return async () =>
        {
            await store.ForgetAsync(row, CancellationToken.None);
            await collections.DeleteAsync(made.Id, CancellationToken.None);
        };
    }

    /// <summary>Making the list again brings back the cards put away with Not now.</summary>
    public void ClearNotNow()
    {
        lock (_lock) _notNow.Clear();
    }

    /// <summary>
    /// Books that have landed in a followed folder join its collection. Runs after files are hashed, so a new book or
    /// one moved in joins once its card is made. Returns how many joined.
    /// </summary>
    public async Task<int> FollowAsync(CancellationToken ct = default)
    {
        var links = (await store.ListAsync(ct)).Where(l => l.Follows).ToList();
        if (links.Count == 0) return 0;
        var items = await collectionStore.GetItemsAsync(ct);
        var joined = 0;
        foreach (var link in links)
        {
            var collectionId = link.CollectionId!.Value;
            var held = items.GetValueOrDefault(collectionId);
            var books = (await library.GetEntriesInFolderAsync(link.Folder, ct)).Where(b => held is null || !held.Contains(b)).ToList();
            joined += (await collections.JoinAsync(collectionId, books, ct)).Count;
        }
        return joined;
    }

    /// <summary>
    /// The collection a folder's collection goes inside: the one made from the nearest folder above it, if it is
    /// still there (<paramref name="exists"/>), whether or not it still follows its folder.
    /// </summary>
    static long? ParentOf(FolderKey folder, IEnumerable<FolderCollectionInfo> answered, Func<long, bool> exists) =>
        answered
            .Where(a => a.CollectionId is { } id && exists(id) && a.Folder != folder && a.Folder.Holds(folder.RootId, folder.Folder))
            .OrderByDescending(a => a.Folder.Folder.Length)
            .Select(a => a.CollectionId)
            .FirstOrDefault();
}

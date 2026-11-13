using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// A card as the rules see it: its kind and format, its pages, its title, its types (keys), the document it shows,
/// and the folders its files are in.
/// </summary>
public sealed record SourceCard(EntryId EntryId, EntryKind Kind, string Format, int? Pages, string Title, IReadOnlyCollection<string> Types, long? DocumentId,
    IReadOnlyList<FolderKey> Folders)
{
    /// <summary>
    /// Whether it may go with a book: a pack, a picture, or a PDF that isn't a book (a map, a handout, a character
    /// sheet, or a few pages that say nothing of what they are).
    /// </summary>
    public bool IsCompanion => Kind == EntryKind.Pack
        || (Kind == EntryKind.Whole && (SourceFormats.IsPicture(Format) || (Format == SourceFormats.Pdf && BookLookups.IsNotABook(Types, [Title], Pages))));

    /// <summary>Whether a companion may go with it: a book's own file, an adventure inside one, or a book owned elsewhere.</summary>
    public bool IsBook => !IsCompanion && Kind is EntryKind.Whole or EntryKind.Part or EntryKind.Elsewhere;
}

/// <summary>
/// The rules that find which book a companion goes with, from what the library already knows, strongest first: its
/// pages are pages of the book; it came in a ZIP with the book, the only book in it; it sits beside the book, the only
/// book in its folder, or in a folder of extras ("Maps", "Handouts") under it; its name carries the book's title,
/// product code or initials; the AI, reading its pages, quoted a title or series that is the book's. The first three
/// name one book and link without asking; a name, the AI, or a folder of two or three books only propose, and a
/// companion with more than one candidate is asked about.
/// </summary>
public static class SourceBookRules
{
    /// <summary>How many books a folder may hold for each to be proposed for the companions beside them.</summary>
    public const int FewBooks = 3;

    /// <summary>Folder names that hold a book's extras, as compared (<see cref="BookRecords.Simplify"/>), word by word.</summary>
    static readonly HashSet<string> ExtrasWords = new(StringComparer.Ordinal)
    {
        "map", "maps", "handout", "handouts", "extra", "extras", "image", "images", "art", "artwork", "pregen", "pregens", "pregenerated", "sheet",
        "sheets", "token", "tokens", "bonus", "vtt", "asset", "assets", "battlemap", "battlemaps", "printable", "printables", "card", "cards", "player",
        "players", "foundry", "roll20", "jpg", "png", "hi", "res", "lo", "dungeondraft", "posters", "poster",
    };

    /// <summary>Words a title's initials leave out when they are written short ("LMoP", or "LMP").</summary>
    static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "of", "the", "a", "an", "and", "in", "on", "to", "for", "at" };

    /// <summary>Whether a folder's name says it holds a book's extras: "Maps", "Player Handouts", "VTT assets".</summary>
    public static bool IsExtrasFolder(string name)
    {
        var words = BookRecords.Simplify(ArchivePaths.FolderName(name)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => ExtrasWords.Contains(w) || w.All(char.IsDigit));
    }

    /// <summary>
    /// What the rules find for the companions among <paramref name="cards"/>: <paramref name="shared"/> are the
    /// companions' pages found in other documents, and <paramref name="codes"/> each document's product codes. Cards in
    /// <paramref name="skip"/> are neither companions nor books (they go with a book by the user's choice, or books go
    /// with them). <paramref name="aiNames"/> are the titles and series the AI read in companions' pages, each with the
    /// quote it gave. Returns the companions considered and what was found for them.
    /// </summary>
    public static (IReadOnlyList<EntryId> Considered, IReadOnlyList<FoundSourceBook> Found) Find(IReadOnlyList<SourceCard> cards, IReadOnlyList<SharedPages> shared,
        IReadOnlyDictionary<long, IReadOnlyList<string>> codes, IReadOnlySet<EntryId> skip,
        IReadOnlyDictionary<EntryId, IReadOnlyList<(string Value, string? Quote)>>? aiNames = null)
    {
        var companions = cards.Where(c => c.IsCompanion && !skip.Contains(c.EntryId)).ToList();
        var books = cards.Where(c => c.IsBook && !skip.Contains(c.EntryId)).ToList();
        var found = new Dictionary<(EntryId Companion, EntryId Book), FoundSourceBook>();
        void Add(SourceCard companion, SourceCard book, bool strong, SourceBookEvidence evidence)
        {
            var key = (companion.EntryId, book.EntryId);
            if (!found.TryGetValue(key, out var was) || was.Evidence.Score < evidence.Score)
                found[key] = new FoundSourceBook(companion.EntryId, book.EntryId, strong || (was?.Strong ?? false), evidence);
            else if (strong && !was.Strong) found[key] = was with { Strong = true };
        }

        // Its pages are the book's: most of the pages that say anything, and the book is the longer.
        var byDocument = cards.Where(c => c.DocumentId is not null).GroupBy(c => c.DocumentId!.Value).ToDictionary(g => g.Key, g => g.First());
        var bookIds = books.Select(b => b.EntryId).ToHashSet();
        foreach (var pages in shared)
        {
            if (!byDocument.TryGetValue(pages.DocumentId, out var companion) || !companion.IsCompanion || skip.Contains(companion.EntryId)) continue;
            if (!byDocument.TryGetValue(pages.OtherDocumentId, out var book) || !bookIds.Contains(book.EntryId)) continue;
            if (pages.Shared * 2 < pages.Pages || (book.Pages is { } length && companion.Pages is { } own && length <= own)) continue;
            Add(companion, book, true, new SourceBookEvidence(SourceBookSignal.SharedPages, Pages(pages.Shared), 1.0));
        }

        // Its folder, or the ZIP it came in.
        var booksIn = books.SelectMany(b => b.Folders.Distinct().Select(f => (Folder: f, Book: b))).GroupBy(x => x.Folder)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Book).DistinctBy(b => b.EntryId).ToList());
        foreach (var companion in companions)
        {
            foreach (var folder in companion.Folders.Distinct())
            {
                var zip = InZip(folder);
                if (booksIn.TryGetValue(folder, out var beside))
                {
                    // Loose in the library folder itself, beside its one book, says less than a folder of its own does.
                    if (beside.Count == 1)
                        Add(companion, beside[0], folder.Folder.Length > 0, zip is { } z && z == folder
                            ? new SourceBookEvidence(SourceBookSignal.SameZip, ArchivePaths.FolderName(folder.Name), 0.95)
                            : new SourceBookEvidence(SourceBookSignal.SameFolder, Shown(folder), 0.9));
                    else if (beside.Count <= FewBooks)
                        foreach (var book in beside) Add(companion, book, false, new SourceBookEvidence(SourceBookSignal.SameFolder, Shown(folder), 0.3));
                    continue;
                }
                // A folder of extras under the book's, or a folder inside the ZIP the book came in.
                if (folder.Folder.Length == 0) continue;
                var parent = folder.Parent;
                if (booksIn.TryGetValue(parent, out var above) && above.Count == 1)
                {
                    if (zip is { } inZip && inZip != folder && inZip.Holds(parent.RootId, folder.Folder))
                        Add(companion, above[0], true, new SourceBookEvidence(SourceBookSignal.SameZip, ArchivePaths.FolderName(inZip.Name), 0.9));
                    else if (IsExtrasFolder(folder.Name))
                        Add(companion, above[0], true, new SourceBookEvidence(SourceBookSignal.SameFolder, Shown(parent), 0.85));
                }
                // Inside a ZIP, the book may be anywhere in it.
                if (zip is { } root && root != folder && root != parent)
                {
                    var inside = booksIn.Where(b => root == b.Key || root.Holds(b.Key.RootId, b.Key.Folder + "\\x")).SelectMany(b => b.Value)
                        .DistinctBy(b => b.EntryId).ToList();
                    if (inside.Count == 1)
                        Add(companion, inside[0], true, new SourceBookEvidence(SourceBookSignal.SameZip, ArchivePaths.FolderName(root.Name), 0.9));
                }
            }
        }

        // Its name: the book's title, product code or initials.
        var titled = books.Select(b => (Book: b, Title: Titles(b.Title))).Where(b => b.Title.Count > 0).ToList();
        var byWord = new Dictionary<string, List<(SourceCard Book, IReadOnlyList<string> Titles)>>(StringComparer.Ordinal);
        foreach (var (book, titles) in titled)
            foreach (var word in titles.Select(t => Rarest(t)).Distinct())
            {
                if (!byWord.TryGetValue(word, out var list)) byWord[word] = list = [];
                list.Add((book, titles));
            }
        var byCode = books.Where(b => b.DocumentId is { } d && codes.ContainsKey(d))
            .SelectMany(b => codes[b.DocumentId!.Value].Where(c => c.Length >= 5).Select(c => (Code: c, Book: b)))
            .GroupBy(x => x.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Book).DistinctBy(b => b.EntryId).ToList(), StringComparer.Ordinal);
        var byInitials = books.SelectMany(b => Initials(b.Title).Select(i => (Initials: i, Book: b)))
            .GroupBy(x => x.Initials, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Book).DistinctBy(b => b.EntryId).ToList(), StringComparer.Ordinal);
        // The books whose title a name carries, with the longest title found.
        IEnumerable<(SourceCard Book, string Title)> Carried(string text)
        {
            var name = BookRecords.Simplify(text);
            if (name.Length == 0) yield break;
            var padded = $" {name} ";
            var seen = new HashSet<EntryId>();
            foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct())
            {
                if (!byWord.TryGetValue(word, out var candidates)) continue;
                foreach (var (book, titles) in candidates)
                    if (titles.Where(t => padded.Contains($" {t} ", StringComparison.Ordinal)).MaxBy(t => t.Length) is { } title && seen.Add(book.EntryId))
                        yield return (book, title);
            }
        }
        foreach (var companion in companions)
        {
            foreach (var (value, quote) in aiNames?.GetValueOrDefault(companion.EntryId) ?? [])
                foreach (var (book, title) in Carried(value))
                    Add(companion, book, false, new SourceBookEvidence(SourceBookSignal.Ai, quote is { Length: > 0 } q ? q : value, 0.55 + Math.Min(title.Length, 40) / 400.0));
            var name = BookRecords.Simplify(companion.Title);
            if (name.Length == 0) continue;
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var (book, title) in Carried(companion.Title))
                Add(companion, book, false, new SourceBookEvidence(SourceBookSignal.Name, book.Title, 0.6 + Math.Min(title.Length, 40) / 400.0));
            var squeezed = new string([.. companion.Title.Where(char.IsLetterOrDigit)]).ToUpperInvariant();
            foreach (var (code, coded) in byCode)
                if (squeezed.Contains(code, StringComparison.Ordinal))
                    foreach (var book in coded) Add(companion, book, false, new SourceBookEvidence(SourceBookSignal.Name, code, 0.7));
            foreach (var word in words.Where(w => w.Length >= 3).Distinct())
                if (byInitials.TryGetValue(word, out var initialled) && initialled.Count <= FewBooks)
                    foreach (var book in initialled) Add(companion, book, false, new SourceBookEvidence(SourceBookSignal.Name, word.ToUpperInvariant(), 0.4));
        }

        // A strong signal for one book isn't put in doubt by a guess at another: only another title or code is asked about.
        var kept = found.Values.GroupBy(f => f.Companion).SelectMany(g =>
        {
            var strong = g.Where(f => f.Strong).ToList();
            if (strong.Count == 0) return g;
            return g.Where(f => f.Strong || (f.Evidence.Signal is SourceBookSignal.Name or SourceBookSignal.Ai && f.Evidence.Score >= 0.6));
        });
        return ([.. companions.Select(c => c.EntryId)], [.. kept]);
    }

    /// <summary>The ZIP a folder is in, or is: the folder up to and including the part that ends in .zip.</summary>
    public static FolderKey? InZip(FolderKey folder)
    {
        var parts = folder.Folder.Split(['\\', '/']);
        for (var i = 0; i < parts.Length; i++)
            if (SourceFormats.IsArchive(parts[i])) return new FolderKey(folder.RootId, string.Join('\\', parts[..(i + 1)]));
        return null;
    }

    static string Shown(FolderKey folder) => folder.Folder.Length == 0 ? "" : ArchivePaths.FolderName(folder.Name);

    static string Pages(long shared) => shared == 1 ? "1 page" : $"{shared} pages";

    /// <summary>
    /// The titles a book's name may be written by, as compared: the whole title, and the part before a colon or dash. One
    /// short word isn't enough to tell a book by.
    /// </summary>
    static List<string> Titles(string title)
    {
        var all = new[] { title, title.Split([':', '–', '—', '(']).First(), title.Split(" - ").First() }
            .Select(BookRecords.Simplify).Where(t => t.Length >= 6 && (t.Contains(' ', StringComparison.Ordinal) || t.Length >= 8)).Distinct().ToList();
        return all;
    }

    /// <summary>The longest word of a title, which finds the titles a name may carry without comparing every book.</summary>
    static string Rarest(string title) => title.Split(' ').Where(w => !Small.Contains(w)).DefaultIfEmpty(title).MaxBy(w => w.Length)!;

    /// <summary>A title's initials, every word's ("lmop") and leaving out the small ones ("lmp"), at three letters or more.</summary>
    static IEnumerable<string> Initials(string title)
    {
        var words = BookRecords.Simplify(title.Split([':', '–', '—', '(']).First()).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.All(char.IsDigit)).ToList();
        if (words.Count < 3) yield break;
        var all = new string([.. words.Select(w => w[0])]);
        var big = new string([.. words.Where(w => !Small.Contains(w)).Select(w => w[0])]);
        if (all.Length >= 3) yield return all;
        if (big.Length >= 3 && big != all) yield return big;
    }
}

/// <summary>
/// Runs <see cref="SourceBookRules"/> over the whole library in the background: a few minutes after starting, and again
/// whenever the library's files or cards have changed since the last pass. What it finds replaces what it found
/// before, never the user's own links or refusals; a link that stands without asking is projected at once, so the
/// companion takes its book's values, and the rest wait in Needs review.
/// </summary>
public sealed class SourceBookFinder(CompanionStore store, LibraryStore library, LibraryQueries cards, IndexQueries queries, MetadataProjector projector,
    MetadataStore metadata, TimeProvider? clock = null, ILogger<SourceBookFinder>? log = null) : IHostedService, IDisposable
{
    /// <summary>How long after starting it waits, so the app's own start-up work goes first.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often it looks for changes since the last pass.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly CancellationTokenSource _stopping = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    Task? _running;
    string? _seen;

    /// <summary>Raised, on any thread, after a pass changed which books companions go with or are proposed for.</summary>
    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _running = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_running is { } running)
        {
            try
            {
                await running.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _gate.Dispose();
    }

    async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartDelay, _clock, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(force: false, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _log.LogError(ex, "Finding the books companions go with failed");
                }
                await Task.Delay(Every, _clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One pass over the library, unless nothing has changed since the last (or <paramref name="force"/>). Returns
    /// whether any companion's link or proposals changed.
    /// </summary>
    public async Task<bool> RunAsync(bool force = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var shown = await library.GetVisibleEntryIdsAsync(ct: ct);
            var folders = await store.GetCardFoldersAsync(ct: ct);
            var seen = $"{shown.Count}:{shown.Sum(e => e.Value)}:{folders.Count}:{folders.Sum(f => f.Folder.RootId + f.Folder.Folder.Length)}";
            if (!force && seen == _seen) return false;
            var listed = await cards.ListAsync(new LibraryFilter(Scope: shown), ct: ct);
            var types = await queries.GetFacetValuesAsync("type", ct);
            var byCard = folders.GroupBy(f => f.Card).ToDictionary(g => g.Key, g => (IReadOnlyList<FolderKey>)[.. g.Select(f => f.Folder)]);
            List<SourceCard> all = [.. listed.DistinctBy(e => e.EntryId).Select(e => new SourceCard(e.EntryId, e.EntryKind, e.Format, e.PageCount, e.Title,
                types.GetValueOrDefault(e.EntryId) ?? [], e.DocumentId == 0 ? null : e.DocumentId, byCard.GetValueOrDefault(e.EntryId) ?? []))];
            var live = await store.GetLiveAsync(ct: ct);
            // A card the user linked, or one others go with, keeps out of the rules either way.
            var skip = live.Values.Where(l => l.State == CompanionState.Confirmed).Select(l => l.Companion)
                .Concat(live.Values.Select(l => l.Book)).ToHashSet();
            var companionDocuments = all.Where(c => c.IsCompanion && c.Kind == EntryKind.Whole && c.Format == SourceFormats.Pdf && c.DocumentId is not null)
                .Select(c => c.DocumentId!.Value).ToList();
            var shared = await queries.GetSharedPagesAsync(companionDocuments, ct);
            var codes = await queries.GetProductCodesAsync(ct);
            // What the AI read in a companion's pages: a title or series that may be its book's.
            var read = await metadata.GetManyAsync([.. all.Where(c => c.IsCompanion && c.Format == SourceFormats.Pdf).Select(c => c.EntryId)], ct);
            var aiNames = read.ToDictionary(r => r.Key, r => (IReadOnlyList<(string, string?)>)[.. r.Value.Claims
                .Where(c => c.Origin == AssertionOrigin.Ai && c.State is AssertionState.Provisional or AssertionState.Confirmed && (c.Field == MetadataFields.Title.Key || c.Field == MetadataFields.Series.Key))
                .Select(c => (c.Value, c.Quote))]);
            var (considered, found) = SourceBookRules.Find(all, shared, codes, skip, aiNames);
            var proposedBefore = (await store.GetProposedAsync(ct)).Select(p => (p.Companion, p.Book)).ToHashSet();
            var changed = await store.ReplaceFoundAsync(considered, found, ct);
            _seen = seen;
            var proposedAfter = (await store.GetProposedAsync(ct)).Select(p => (p.Companion, p.Book)).ToHashSet();
            if (changed.Count > 0)
            {
                var after = await store.GetLiveAsync(changed, ct);
                var books = live.Values.Where(l => changed.Contains(l.Companion)).Select(l => l.Book).Concat(after.Values.Select(l => l.Book));
                _log.LogInformation("Linked or unlinked {Count} maps and handouts to their books", changed.Count);
                await projector.ProjectAsync([.. changed, .. books.Distinct()], ct);
            }
            var any = changed.Count > 0 || !proposedBefore.SetEquals(proposedAfter);
            if (any) Changed?.Invoke(this, EventArgs.Empty);
            return any;
        }
        finally
        {
            _gate.Release();
        }
    }
}

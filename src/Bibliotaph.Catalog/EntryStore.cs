using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// An entry and the document its card shows: the current source, which opens and supplies cover and pages, and how
/// many copies (whole-document sources) the entry has. A pack shows its first image, and has a
/// <see cref="Name"/> from its folder or ZIP and its <see cref="Members"/> in file name order. A book owned elsewhere
/// has no document and no copies.
/// </summary>
public sealed record EntryDocument(EntryId EntryId, long? DocumentId, EntryKind Kind, int Copies = 1)
{
    public string? Name { get; init; }

    public IReadOnlyList<PackMember>? Members { get; init; }

    /// <summary>When a book owned elsewhere was added; a file's card goes by its document's.</summary>
    public DateTime? AddedUtc { get; init; }
}

/// <summary>An image in a pack: its own entry, the document it shows, and its file name.</summary>
public sealed record PackMember(EntryId EntryId, long DocumentId, string Name);

/// <summary>A document, its content hash, and the whole-document entry it backs.</summary>
public sealed record DocumentEntry(long DocumentId, string ContentHash, EntryId EntryId);

/// <summary>
/// One copy of an entry's book, for the inspector's Copies list. <see cref="IsCurrent"/> is the copy chosen to open;
/// <see cref="IsShown"/> the one that does, which differs only while the current copy has no file.
/// </summary>
public sealed record EntryCopy(long DocumentId, string ContentHash, int? PageCount, bool IsCurrent, bool IsShown, bool HasFile, bool Joined, DateTime AddedUtc);

/// <summary>A copy that joined another entry: the entry it joined and the one it left, which Undo brings back.</summary>
public sealed record CopyJoin(EntryId EntryId, EntryId JoinedEntryId);

/// <summary>
/// A book owned elsewhere that the user removed, with what was set on it, so Undo can put it back
/// (<see cref="EntryStore.RestoreElsewhereAsync"/>).
/// </summary>
public sealed record RemovedEntry(EntryId EntryId, DateTime CreatedUtc, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Rejection> Rejections,
    bool Favorite = false);

/// <summary>
/// Entries in catalog.db (catalog entry design): which document each library card shows, and which card a document
/// belongs to. Processing works on documents and the user's decisions on entries; this is where one turns into the other.
/// </summary>
public sealed class EntryStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The whole-document entry <paramref name="documentId"/> backs, or null for a document the catalog doesn't know.</summary>
    public async Task<DocumentEntry?> GetEntryAsync(long documentId, CancellationToken ct = default) =>
        (await GetEntriesAsync([documentId], ct)).TryGetValue(documentId, out var entry) ? entry : null;

    /// <summary>
    /// The library card a document shows under: its whole-document entry, or for an image in a pack, the pack. Null
    /// for a document the catalog doesn't know.
    /// </summary>
    public async Task<EntryId?> GetCardAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var card = await db.EntrySources.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.FirstPdfPage == null)
            .Select(s => new { s.EntryId, s.Entry.ParentEntryId })
            .FirstOrDefaultAsync(ct);
        return card is null ? null : new EntryId(card.ParentEntryId ?? card.EntryId);
    }

    /// <summary>The whole-document entry each of <paramref name="documentIds"/> backs.</summary>
    public async Task<IReadOnlyDictionary<long, DocumentEntry>> GetEntriesAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new Dictionary<long, DocumentEntry>();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = documentIds.Distinct().ToList();
        var rows = await db.EntrySources.AsNoTracking()
            .Where(s => ids.Contains(s.DocumentId) && s.FirstPdfPage == null)
            .Select(s => new { s.DocumentId, s.Document.ContentHash, s.EntryId })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.DocumentId, r => new DocumentEntry(r.DocumentId, r.ContentHash, new EntryId(r.EntryId)));
    }

    /// <summary>
    /// The entries <paramref name="documentId"/> is a copy of, with the document each one's card shows. An image in a
    /// pack shows on the pack's card instead.
    /// </summary>
    public async Task<IReadOnlyList<EntryDocument>> GetShownByAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entries = await db.EntrySources.AsNoTracking().Where(s => s.DocumentId == documentId)
            .Select(s => s.Entry.ParentEntryId != null && s.Entry.ParentEntry!.Kind == EntryKind.Pack ? s.Entry.ParentEntryId.Value : s.EntryId)
            .Distinct().ToListAsync(ct);
        return entries.Count == 0 ? [] : await GetCurrentAsync([.. entries.Select(e => new EntryId(e))], ct);
    }

    /// <summary>The document <paramref name="entryId"/>'s card shows, or null for an entry with no file.</summary>
    public async Task<long?> GetCurrentDocumentAsync(EntryId entryId, CancellationToken ct = default) =>
        (await GetCurrentAsync([entryId], ct)) is [var current, ..] ? current.DocumentId : null;

    /// <summary>The kind of <paramref name="entryId"/>, or null for an entry the catalog doesn't have.</summary>
    public async Task<EntryKind?> GetKindAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Entries.AsNoTracking().Where(e => e.Id == entryId.Value).Select(e => (EntryKind?)e.Kind).SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// The document each entry's card shows, for <paramref name="entryIds"/> or, when it is null, every entry with
    /// one. That is the current copy, unless none of its files is left and another copy's is (choice 6): then the
    /// newest copy with a file. An entry with no file (owned elsewhere, or joined to another) isn't listed, nor is an
    /// image in a pack: the pack is, showing its first image that has a file, with all of them as its members. A book
    /// owned elsewhere is listed with no document (F5 plan, choice 2).
    /// </summary>
    public async Task<IReadOnlyList<EntryDocument>> GetCurrentAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        if (entryIds is { Count: 0 }) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = entryIds?.Select(e => e.Value).Distinct().ToList();
        var sources = db.EntrySources.AsNoTracking().Where(s => s.Entry.ParentEntryId == null || s.Entry.ParentEntry!.Kind != EntryKind.Pack);
        if (ids is not null) sources = sources.Where(s => ids.Contains(s.EntryId));
        var rows = await Sources(sources).ToListAsync(ct);
        var current = rows.GroupBy(r => r.EntryId).Select(g =>
        {
            var shown = Shown([.. g]);
            return new EntryDocument(new EntryId(g.Key), shown.DocumentId, shown.Kind, g.Count(s => s.Whole));
        }).ToList();

        var packs = db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Pack);
        if (ids is not null) packs = packs.Where(e => ids.Contains(e.Id));
        var packIds = await packs.Select(e => e.Id).ToListAsync(ct);
        if (packIds.Count > 0) current.AddRange(await GetPacksAsync(db, packIds, ct));

        var elsewhere = db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null && !e.Sources.Any());
        if (ids is not null) elsewhere = elsewhere.Where(e => ids.Contains(e.Id));
        current.AddRange((await elsewhere.Select(e => new { e.Id, e.CreatedUtc }).ToListAsync(ct))
            .Select(e => new EntryDocument(new EntryId(e.Id), null, EntryKind.Elsewhere, Copies: 0) { AddedUtc = e.CreatedUtc }));
        return current;
    }

    /// <summary>Makes a card for a book the user owns elsewhere (F5 plan, choice 2): an entry with no sources, for their own values.</summary>
    public async Task<EntryId> AddElsewhereAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entry = db.Entries.Add(new Entry { Kind = EntryKind.Elsewhere, CreatedUtc = _clock.GetUtcNow().UtcDateTime }).Entity;
        await db.SaveChangesAsync(ct);
        return new EntryId(entry.Id);
    }

    /// <summary>
    /// "Remove from library" (choice 5): deletes a book owned elsewhere and everything set on it, returning that for
    /// Undo. Null for any other card: a file's card goes when its file does.
    /// </summary>
    public async Task<RemovedEntry?> RemoveElsewhereAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entry = await db.Entries.Include(e => e.Assertions).Include(e => e.Rejections)
            .SingleOrDefaultAsync(e => e.Id == entryId.Value && e.Kind == EntryKind.Elsewhere && !e.Sources.Any(), ct);
        if (entry is null) return null;
        var removed = new RemovedEntry(entryId, entry.CreatedUtc,
            [.. entry.Assertions.Select(Detached)], [.. entry.Rejections.Select(r => new Rejection { Field = r.Field, NormalizedValue = r.NormalizedValue, CreatedUtc = r.CreatedUtc })],
            await db.Favorites.AnyAsync(f => f.EntryId == entryId.Value, ct));
        db.Entries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return removed;
    }

    /// <summary>Undoes <see cref="RemoveElsewhereAsync"/>: the book comes back, with what was set on it, as a new card.</summary>
    public async Task<EntryId> RestoreElsewhereAsync(RemovedEntry removed, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entry = new Entry { Kind = EntryKind.Elsewhere, CreatedUtc = removed.CreatedUtc };
        entry.Assertions.AddRange(removed.Assertions.Select(Detached));
        entry.Rejections.AddRange(removed.Rejections.Select(r => new Rejection { Field = r.Field, NormalizedValue = r.NormalizedValue, CreatedUtc = r.CreatedUtc }));
        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
        if (removed.Favorite)
        {
            db.Favorites.Add(new Favorite { EntryId = entry.Id, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
        }
        return new EntryId(entry.Id);
    }

    static Assertion Detached(Assertion a) => new()
    {
        Field = a.Field,
        ValueJson = a.ValueJson,
        NormalizedValue = a.NormalizedValue,
        Origin = a.Origin,
        ContentHash = a.ContentHash,
        EvidencePagesJson = a.EvidencePagesJson,
        EvidenceQuote = a.EvidenceQuote,
        FromSampling = a.FromSampling,
        RunId = a.RunId,
        State = a.State,
        CreatedUtc = a.CreatedUtc,
        DecidedUtc = a.DecidedUtc,
    };

    /// <summary>
    /// "Same book" on an owned-elsewhere card (F5 plan, choice 6): the file's card joins the book owned elsewhere,
    /// which keeps everything set on it and becomes a whole entry that opens the file. What the file's card brings
    /// moves across as a copy's does (<see cref="JoinAsCopyAsync"/>). Null when either has gone or they are one card.
    /// </summary>
    public async Task<CopyJoin?> JoinElsewhereAsync(EntryId elsewhereId, long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entry = await db.Entries.SingleOrDefaultAsync(e => e.Id == elsewhereId.Value && e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null, ct);
        var joining = await db.EntrySources.Where(s => s.DocumentId == documentId && s.FirstPdfPage == null).Select(s => (long?)s.EntryId).FirstOrDefaultAsync(ct);
        if (entry is null || joining is null || joining == entry.Id) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var moved = await MoveAsync(db, joining.Value, entry.Id, ct);
        foreach (var source in db.EntrySources.Local.Where(s => s.EntryId == entry.Id && s.FirstPdfPage == null))
            source.IsCurrent = source.DocumentId == documentId;
        entry.Kind = EntryKind.Whole;
        // The join records the file as matching itself: there was no copy on the book's card to match.
        AddJoin(db, entry.Id, joining.Value, documentId, documentId, moved);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CopyJoin(elsewhereId, new EntryId(joining.Value));
    }

    /// <summary>
    /// Undoes <see cref="JoinElsewhereAsync"/>: the file goes back to its own card with what it brought, and the book is
    /// owned elsewhere again. Returns the file's card, or null when the file didn't join that book this way.
    /// </summary>
    public async Task<EntryId?> UndoJoinElsewhereAsync(EntryId entryId, long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var join = await db.EntryJoins.Where(j => j.EntryId == entryId.Value && j.DocumentId == documentId && j.MatchedDocumentId == documentId)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        if (join is null) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sources = await db.EntrySources.Where(s => s.EntryId == entryId.Value).ToListAsync(ct);
        await RestoreAsync(db, entryId.Value, join, sources, ct);
        await db.SaveChangesAsync(ct);
        if (!await db.EntrySources.AnyAsync(s => s.EntryId == entryId.Value, ct))
            (await db.Entries.SingleAsync(e => e.Id == entryId.Value, ct)).Kind = EntryKind.Elsewhere;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new EntryId(join.JoinedEntryId);
    }

    /// <summary>Each pack with an image that has a file: its first image, its name, and its images in file name order.</summary>
    static async Task<IEnumerable<EntryDocument>> GetPacksAsync(CatalogDbContext db, List<long> packIds, CancellationToken ct)
    {
        var rows = await Sources(db.EntrySources.AsNoTracking().Where(s => s.Entry.ParentEntryId != null && packIds.Contains(s.Entry.ParentEntryId.Value)))
            .ToListAsync(ct);
        var shown = rows.GroupBy(r => r.EntryId).Select(g => Shown([.. g])).Where(s => s.HasFile).ToList();
        var documentIds = shown.Select(s => s.DocumentId).Distinct().ToList();
        var paths = (await db.FileLocations.AsNoTracking()
                .Where(l => l.DocumentId != null && documentIds.Contains(l.DocumentId.Value) && l.State != FileLocationState.Missing)
                .Select(l => new { DocumentId = l.DocumentId!.Value, l.RelativePath })
                .ToListAsync(ct))
            .GroupBy(l => l.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.RelativePath).Min(StringComparer.Ordinal)!);
        var names = await db.PackDecisions.AsNoTracking().Where(d => packIds.Contains(d.EntryId))
            .ToDictionaryAsync(d => d.EntryId, d => PackStore.Name(d.FolderPath, d.IsArchive), ct);
        return shown.GroupBy(s => s.ParentEntryId!.Value).Select(g =>
        {
            List<PackMember> members = [.. g.Select(s => new PackMember(new EntryId(s.EntryId), s.DocumentId,
                    paths.TryGetValue(s.DocumentId, out var path) ? PackStore.FileName(path) : ""))
                .OrderBy(m => m.Name, NaturalOrder.Instance).ThenBy(m => m.EntryId.Value)];
            return new EntryDocument(new EntryId(g.Key), members[0].DocumentId, EntryKind.Pack, Copies: 1)
            {
                Name = names.GetValueOrDefault(g.Key),
                Members = members,
            };
        });
    }

    /// <summary>The copies of <paramref name="entryId"/>'s book, the current one first, then newest first.</summary>
    public async Task<IReadOnlyList<EntryCopy>> GetCopiesAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await Sources(db.EntrySources.AsNoTracking().Where(s => s.EntryId == entryId.Value && s.FirstPdfPage == null)).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var shown = Shown(rows).DocumentId;
        var joined = await db.EntryJoins.AsNoTracking().Where(j => j.EntryId == entryId.Value).Select(j => j.DocumentId).ToListAsync(ct);
        return [.. rows.OrderByDescending(r => r.IsCurrent).ThenByDescending(r => r.DocumentId)
            .Select(r => new EntryCopy(r.DocumentId, r.ContentHash, r.PageCount, r.IsCurrent, r.DocumentId == shown, r.HasFile, joined.Contains(r.DocumentId), r.AddedUtc))];
    }

    /// <summary>Makes <paramref name="documentId"/> the copy <paramref name="entryId"/> opens. False if it isn't one of its copies.</summary>
    public async Task<bool> MakeCurrentAsync(EntryId entryId, long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sources = await db.EntrySources.Where(s => s.EntryId == entryId.Value && s.FirstPdfPage == null).ToListAsync(ct);
        if (sources.All(s => s.DocumentId != documentId)) return false;
        await SetCurrentAsync(db, sources, documentId, ct);
        return true;
    }

    /// <summary>Whether the user said these two files are not the same book.</summary>
    public async Task<bool> IsNotSameBookAsync(string firstHash, string secondHash, CancellationToken ct = default)
    {
        var (a, b) = Pair(firstHash, secondHash);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.CopyDecisions.AnyAsync(d => d.FirstHash == a && d.SecondHash == b && d.Answer == CopyAnswer.NotSameBook, ct);
    }

    /// <summary>
    /// Joins two documents found to hold the same book (choice 5). The newer document's entry joins the older one's:
    /// its copies become copies there, without becoming current; its suggestions and runs move across; its rejections
    /// move unless the other card has them; and a value the user set that disagrees with one set on the other card,
    /// for a field with one value, is set aside for a "Copies disagree" card (choice 7). Returns null when they are already one card, or
    /// the user said they are not the same book.
    /// </summary>
    public async Task<CopyJoin?> JoinAsCopyAsync(long documentId, long matchedDocumentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pair = await db.EntrySources
            .Where(s => (s.DocumentId == documentId || s.DocumentId == matchedDocumentId) && s.FirstPdfPage == null)
            .Select(s => new { s.DocumentId, s.EntryId, s.Document.ContentHash })
            .ToListAsync(ct);
        var mine = pair.FirstOrDefault(p => p.DocumentId == documentId);
        var theirs = pair.FirstOrDefault(p => p.DocumentId == matchedDocumentId);
        if (mine is null || theirs is null || mine.EntryId == theirs.EntryId) return null;
        if (await IsNotSameBookAsync(mine.ContentHash, theirs.ContentHash, ct)) return null;

        var (older, newer) = documentId < matchedDocumentId ? (mine, theirs) : (theirs, mine);
        var target = older.EntryId;
        var joining = newer.EntryId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var moved = await MoveAsync(db, joining, target, ct);
        foreach (var source in db.EntrySources.Local.Where(s => s.EntryId == target && moved.Sources.Any(m => m.Id == s.Id)))
            source.IsCurrent = false;
        AddJoin(db, target, joining, newer.DocumentId, older.DocumentId, moved);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CopyJoin(new EntryId(target), new EntryId(joining));
    }

    /// <summary>
    /// Moves one card onto another: its sources, its suggestions and runs, its rejections unless the other card has
    /// them, and a value the user set that disagrees with one set on the other card, for a field with one value, set
    /// aside for a "Copies disagree" card (choice 7). The card that joined keeps its id, marked as merged. Returns what
    /// moved, which the caller records; the caller saves.
    /// </summary>
    static async Task<MovedRows> MoveAsync(CatalogDbContext db, long joining, long target, CancellationToken ct)
    {
        var sources = await db.EntrySources.Where(s => s.EntryId == joining).ToListAsync(ct);
        var moved = new MovedRows { Sources = [.. sources.Select(s => new MovedSource(s.Id, s.IsCurrent))] };
        foreach (var source in sources) source.EntryId = target;

        var settled = await db.Assertions.AsNoTracking()
            .Where(a => a.EntryId == target && a.State == AssertionState.Confirmed)
            .Select(a => new { a.Field, a.NormalizedValue })
            .ToListAsync(ct);
        foreach (var assertion in await db.Assertions.Where(a => a.EntryId == joining).ToListAsync(ct))
        {
            if (assertion.State == AssertionState.Confirmed && MetadataFields.Find(assertion.Field) is { Multiple: false }
                && settled.Any(s => s.Field == assertion.Field && s.NormalizedValue != assertion.NormalizedValue))
            {
                assertion.State = AssertionState.SetAside;
                moved.SetAside.Add(assertion.Id);
            }
            assertion.EntryId = target;
            moved.Assertions.Add(assertion.Id);
        }

        var rejected = await db.Rejections.AsNoTracking().Where(r => r.EntryId == target).Select(r => new { r.Field, r.NormalizedValue }).ToListAsync(ct);
        foreach (var rejection in await db.Rejections.Where(r => r.EntryId == joining).ToListAsync(ct))
        {
            if (rejected.Any(r => r.Field == rejection.Field && r.NormalizedValue == rejection.NormalizedValue)) continue;
            rejection.EntryId = target;
            moved.Rejections.Add(rejection.Id);
        }

        foreach (var run in await db.ClassificationRuns.Where(r => r.EntryId == joining).ToListAsync(ct))
        {
            run.EntryId = target;
            moved.Runs.Add(run.Id);
        }

        // A heart and a reading position move only to a card without one; otherwise they stay behind on the card that
        // joined, which shows nowhere, until Not the same book brings it back (slice 3 plan, choice 19).
        if (await db.Favorites.SingleOrDefaultAsync(f => f.EntryId == joining, ct) is { } favorite
            && !await db.Favorites.AnyAsync(f => f.EntryId == target, ct))
        {
            db.Favorites.Remove(favorite);
            db.Favorites.Add(new Favorite { EntryId = target, CreatedUtc = favorite.CreatedUtc });
            moved.Favorite = true;
        }
        if (await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == joining, ct) is { } reading
            && !await db.ReadingStates.AnyAsync(r => r.EntryId == target, ct))
        {
            db.ReadingStates.Remove(reading);
            db.ReadingStates.Add(new ReadingState { EntryId = target, DocumentId = reading.DocumentId, PageIndex = reading.PageIndex, OpenedUtc = reading.OpenedUtc });
            moved.Reading = true;
        }

        (await db.Entries.SingleAsync(e => e.Id == joining, ct)).MergedIntoEntryId = target;
        return moved;
    }

    void AddJoin(CatalogDbContext db, long target, long joining, long documentId, long matchedDocumentId, MovedRows moved) =>
        db.EntryJoins.Add(new EntryJoin
        {
            EntryId = target,
            JoinedEntryId = joining,
            DocumentId = documentId,
            MatchedDocumentId = matchedDocumentId,
            MovedJson = JsonSerializer.Serialize(moved),
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        });

    /// <summary>
    /// Puts back what a join moved: the sources go back to the card that joined, as current as they were, with the
    /// assertions, rejections and runs they brought, and that card shows again. The caller saves.
    /// </summary>
    static async Task RestoreAsync(CatalogDbContext db, long entryId, EntryJoin join, List<EntrySource> sources, CancellationToken ct)
    {
        var card = join.JoinedEntryId;
        var moved = JsonSerializer.Deserialize<MovedRows>(join.MovedJson) ?? new MovedRows();
        var sourceIds = moved.Sources.Select(s => s.Id).ToList();
        // Cleared first and saved: the unique index allows one current source per entry at any moment.
        foreach (var source in sources.Where(s => sourceIds.Contains(s.Id))) source.IsCurrent = false;
        await db.SaveChangesAsync(ct);
        foreach (var source in sources.Where(s => sourceIds.Contains(s.Id)))
        {
            source.EntryId = card;
            source.IsCurrent = moved.Sources.Single(s => s.Id == source.Id).WasCurrent;
        }
        if (!sources.Any(s => s.EntryId == card && s.IsCurrent) && sources.FirstOrDefault(s => s.DocumentId == join.DocumentId && s.FirstPdfPage == null) is { } leaving)
            leaving.IsCurrent = true;
        foreach (var assertion in await db.Assertions.Where(a => a.EntryId == entryId && moved.Assertions.Contains(a.Id)).ToListAsync(ct))
        {
            assertion.EntryId = card;
            // Back to what it was on its own card, even after the user settled the disagreement on this one.
            if (moved.SetAside.Contains(assertion.Id) && assertion.State is AssertionState.SetAside or AssertionState.Superseded)
                assertion.State = AssertionState.Confirmed;
        }
        foreach (var rejection in await db.Rejections.Where(r => r.EntryId == entryId && moved.Rejections.Contains(r.Id)).ToListAsync(ct))
            rejection.EntryId = card;
        foreach (var run in await db.ClassificationRuns.Where(r => r.EntryId == entryId && moved.Runs.Contains(r.Id)).ToListAsync(ct))
            run.EntryId = card;
        // What the card brought goes back with it, as it is now: unmarked since, it stays unmarked.
        if (moved.Favorite && await db.Favorites.SingleOrDefaultAsync(f => f.EntryId == entryId, ct) is { } favorite)
        {
            db.Favorites.Remove(favorite);
            db.Favorites.Add(new Favorite { EntryId = card, CreatedUtc = favorite.CreatedUtc });
        }
        if (moved.Reading && await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == entryId, ct) is { } reading)
        {
            db.ReadingStates.Remove(reading);
            db.ReadingStates.Add(new ReadingState { EntryId = card, DocumentId = reading.DocumentId, PageIndex = reading.PageIndex, OpenedUtc = reading.OpenedUtc });
        }
        (await db.Entries.SingleAsync(e => e.Id == card, ct)).MergedIntoEntryId = null;
        db.EntryJoins.Remove(join);
    }

    /// <summary>
    /// "Not the same book": takes <paramref name="documentId"/> out of <paramref name="entryId"/> onto a card of its own,
    /// and remembers that it isn't the same book as the copies it leaves. A copy that joined automatically goes back to
    /// the card it had, with what it brought (choice 8); any other copy gets a new card. Returns the copy's card, or
    /// null if it isn't a copy of that entry or is its only one. Without <paramref name="remember"/>, as when a
    /// "new version?" answer is undone, nothing is remembered.
    /// </summary>
    public async Task<EntryId?> SplitCopyAsync(EntryId entryId, long documentId, bool remember = true, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sources = await db.EntrySources.Include(s => s.Document)
            .Where(s => s.EntryId == entryId.Value && s.FirstPdfPage == null).ToListAsync(ct);
        var leaving = sources.FirstOrDefault(s => s.DocumentId == documentId);
        if (leaving is null || sources.Count < 2) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var join = await db.EntryJoins.Where(j => j.EntryId == entryId.Value && j.DocumentId == documentId)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
        long card;
        if (join is not null)
        {
            card = join.JoinedEntryId;
            await RestoreAsync(db, entryId.Value, join, sources, ct);
        }
        else
        {
            var entry = db.Entries.Add(new Entry { Kind = EntryKind.Whole, CreatedUtc = _clock.GetUtcNow().UtcDateTime }).Entity;
            await db.SaveChangesAsync(ct);
            card = entry.Id;
            leaving.EntryId = card;
            leaving.IsCurrent = true;
        }

        // Saved before the card that stays gets a current copy: the unique index allows one per entry at any moment.
        await db.SaveChangesAsync(ct);
        var staying = sources.Where(s => s.EntryId == entryId.Value).ToList();
        if (!staying.Any(s => s.IsCurrent)) staying.MaxBy(s => s.DocumentId)!.IsCurrent = true;
        if (remember)
            foreach (var other in staying)
                foreach (var mine in sources.Where(s => s.EntryId == card))
                    await AddNotSameBookAsync(db, mine.Document.ContentHash, other.Document.ContentHash, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new EntryId(card);
    }

    /// <summary>
    /// Records content at a path that held another document as a new version of the same book (A08): the new
    /// document joins that document's entry and becomes its current copy. Returns the entry, or null when the earlier
    /// document has no entry. The caller saves, inside a transaction.
    /// </summary>
    internal static async Task<long?> AddVersionAsync(CatalogDbContext db, Document document, long previousDocumentId, CancellationToken ct)
    {
        var entryId = await db.EntrySources.Where(s => s.DocumentId == previousDocumentId && s.FirstPdfPage == null)
            .Select(s => (long?)s.EntryId).FirstOrDefaultAsync(ct);
        if (entryId is null) return null;
        foreach (var source in await db.EntrySources.Where(s => s.EntryId == entryId && s.IsCurrent).ToListAsync(ct))
            source.IsCurrent = false;
        await db.SaveChangesAsync(ct); // the unique index allows one current source per entry at any moment
        db.EntrySources.Add(new EntrySource { EntryId = entryId.Value, Document = document, IsCurrent = true });
        return entryId;
    }

    /// <summary>Remembers that two files are not the same book, so Match never joins them or asks about them again.</summary>
    public async Task RememberNotSameBookAsync(string firstHash, string secondHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await AddNotSameBookAsync(db, firstHash, secondHash, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Forgets <see cref="RememberNotSameBookAsync"/>, as Undo does.</summary>
    public async Task ForgetNotSameBookAsync(string firstHash, string secondHash, CancellationToken ct = default)
    {
        var (a, b) = Pair(firstHash, secondHash);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.CopyDecisions.Where(d => d.FirstHash == a && d.SecondHash == b).ExecuteDeleteAsync(ct);
    }

    async Task AddNotSameBookAsync(CatalogDbContext db, string firstHash, string secondHash, CancellationToken ct)
    {
        var (a, b) = Pair(firstHash, secondHash);
        if (await db.CopyDecisions.AnyAsync(d => d.FirstHash == a && d.SecondHash == b, ct) || db.CopyDecisions.Local.Any(d => d.FirstHash == a && d.SecondHash == b)) return;
        db.CopyDecisions.Add(new CopyDecision { FirstHash = a, SecondHash = b, Answer = CopyAnswer.NotSameBook, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
    }

    static async Task SetCurrentAsync(CatalogDbContext db, List<EntrySource> sources, long documentId, CancellationToken ct)
    {
        // Clear first: the unique index allows one current source per entry at any moment.
        foreach (var source in sources) source.IsCurrent = false;
        await db.SaveChangesAsync(ct);
        sources.Single(s => s.DocumentId == documentId).IsCurrent = true;
        await db.SaveChangesAsync(ct);
    }

    static (string, string) Pair(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

    sealed record SourceRow(long EntryId, long DocumentId, EntryKind Kind, bool IsCurrent, bool Whole, bool HasFile, string ContentHash, int? PageCount, DateTime AddedUtc,
        long? ParentEntryId);

    static IQueryable<SourceRow> Sources(IQueryable<EntrySource> sources) => sources.Select(s => new SourceRow(
        s.EntryId, s.DocumentId, s.Entry.Kind, s.IsCurrent, s.FirstPdfPage == null,
        s.Document.Locations.Any(l => l.State != FileLocationState.Missing && l.SourceRoot.Availability != SourceRootAvailability.RemovedByUser),
        s.Document.ContentHash, s.Document.PageCount, s.Document.CreatedUtc, s.Entry.ParentEntryId));

    /// <summary>The current copy, unless it has no file and another copy has: then the newest one that has.</summary>
    static SourceRow Shown(List<SourceRow> sources)
    {
        var current = sources.FirstOrDefault(s => s.IsCurrent) ?? sources.MaxBy(s => s.DocumentId)!;
        return current.HasFile ? current : sources.Where(s => s.HasFile).MaxBy(s => s.DocumentId) ?? current;
    }

    sealed class MovedRows
    {
        public List<MovedSource> Sources { get; set; } = [];
        public List<long> Assertions { get; set; } = [];
        public List<long> SetAside { get; set; } = [];
        public List<long> Rejections { get; set; } = [];
        public List<string> Runs { get; set; } = [];
        /// <summary>The card that joined brought its heart (slice 3).</summary>
        public bool Favorite { get; set; }
        /// <summary>The card that joined brought its reading position (slice 3).</summary>
        public bool Reading { get; set; }
    }

    sealed record MovedSource(long Id, bool WasCurrent);
}

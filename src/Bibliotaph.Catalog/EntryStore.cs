using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// An entry and the document its card shows: the current source, which opens and supplies cover and pages, and how
/// many copies (whole-document sources) the entry has.
/// </summary>
public sealed record EntryDocument(EntryId EntryId, long DocumentId, EntryKind Kind, int Copies = 1);

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
/// Entries in catalog.db (catalog entry design): which document each library card shows, and which card a document
/// belongs to. Processing works on documents and the user's decisions on entries; this is where one turns into the other.
/// </summary>
public sealed class EntryStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The whole-document entry <paramref name="documentId"/> backs, or null for a document the catalog doesn't know.</summary>
    public async Task<DocumentEntry?> GetEntryAsync(long documentId, CancellationToken ct = default) =>
        (await GetEntriesAsync([documentId], ct)).TryGetValue(documentId, out var entry) ? entry : null;

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

    /// <summary>The entries <paramref name="documentId"/> is a copy of, with the document each one's card shows.</summary>
    public async Task<IReadOnlyList<EntryDocument>> GetShownByAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entries = await db.EntrySources.AsNoTracking().Where(s => s.DocumentId == documentId).Select(s => s.EntryId).Distinct().ToListAsync(ct);
        return entries.Count == 0 ? [] : await GetCurrentAsync([.. entries.Select(e => new EntryId(e))], ct);
    }

    /// <summary>The document <paramref name="entryId"/>'s card shows, or null for an entry with no file.</summary>
    public async Task<long?> GetCurrentDocumentAsync(EntryId entryId, CancellationToken ct = default) =>
        (await GetCurrentAsync([entryId], ct)) is [var current, ..] ? current.DocumentId : null;

    /// <summary>
    /// The document each entry's card shows, for <paramref name="entryIds"/> or, when it is null, every entry with
    /// one. That is the current copy, unless none of its files is left and another copy's is (choice 6): then the
    /// newest copy with a file. An entry with no file (owned elsewhere, or joined to another) isn't listed.
    /// </summary>
    public async Task<IReadOnlyList<EntryDocument>> GetCurrentAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        if (entryIds is { Count: 0 }) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sources = db.EntrySources.AsNoTracking();
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).Distinct().ToList();
            sources = sources.Where(s => ids.Contains(s.EntryId));
        }
        var rows = await Sources(sources).ToListAsync(ct);
        return [.. rows.GroupBy(r => r.EntryId).Select(g =>
        {
            var shown = Shown([.. g]);
            return new EntryDocument(new EntryId(g.Key), shown.DocumentId, shown.Kind, g.Count(s => s.Whole));
        })];
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
    /// for a field with one value, is kept as a suggestion (choice 7). Returns null when they are already one card, or
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

        var sources = await db.EntrySources.Where(s => s.EntryId == joining).ToListAsync(ct);
        var moved = new MovedRows { Sources = [.. sources.Select(s => new MovedSource(s.Id, s.IsCurrent))] };
        foreach (var source in sources)
        {
            source.EntryId = target;
            source.IsCurrent = false;
        }

        var settled = await db.Assertions.AsNoTracking()
            .Where(a => a.EntryId == target && a.State == AssertionState.Confirmed)
            .Select(a => new { a.Field, a.NormalizedValue })
            .ToListAsync(ct);
        foreach (var assertion in await db.Assertions.Where(a => a.EntryId == joining).ToListAsync(ct))
        {
            if (assertion.State == AssertionState.Confirmed && MetadataFields.Find(assertion.Field) is { Multiple: false }
                && settled.Any(s => s.Field == assertion.Field && s.NormalizedValue != assertion.NormalizedValue))
            {
                assertion.State = AssertionState.Provisional;
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

        var entry = await db.Entries.SingleAsync(e => e.Id == joining, ct);
        entry.MergedIntoEntryId = target;
        db.EntryJoins.Add(new EntryJoin
        {
            EntryId = target,
            JoinedEntryId = joining,
            DocumentId = newer.DocumentId,
            MatchedDocumentId = older.DocumentId,
            MovedJson = JsonSerializer.Serialize(moved),
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CopyJoin(new EntryId(target), new EntryId(joining));
    }

    /// <summary>
    /// "Not the same book": takes <paramref name="documentId"/> out of <paramref name="entryId"/> onto a card of its own,
    /// and remembers that it isn't the same book as the copies it leaves. A copy that joined automatically goes back to
    /// the card it had, with what it brought (choice 8); any other copy gets a new card. Returns the copy's card, or
    /// null if it isn't a copy of that entry or is its only one.
    /// </summary>
    public async Task<EntryId?> SplitCopyAsync(EntryId entryId, long documentId, CancellationToken ct = default)
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
            var moved = JsonSerializer.Deserialize<MovedRows>(join.MovedJson) ?? new MovedRows();
            var sourceIds = moved.Sources.Select(s => s.Id).ToList();
            foreach (var source in sources.Where(s => sourceIds.Contains(s.Id)))
            {
                source.EntryId = card;
                source.IsCurrent = moved.Sources.Single(s => s.Id == source.Id).WasCurrent;
            }
            if (!sources.Any(s => s.EntryId == card && s.IsCurrent)) leaving.IsCurrent = true;
            foreach (var assertion in await db.Assertions.Where(a => a.EntryId == entryId.Value && moved.Assertions.Contains(a.Id)).ToListAsync(ct))
            {
                assertion.EntryId = card;
                if (moved.SetAside.Contains(assertion.Id) && assertion.State == AssertionState.Provisional) assertion.State = AssertionState.Confirmed;
            }
            foreach (var rejection in await db.Rejections.Where(r => r.EntryId == entryId.Value && moved.Rejections.Contains(r.Id)).ToListAsync(ct))
                rejection.EntryId = card;
            foreach (var run in await db.ClassificationRuns.Where(r => r.EntryId == entryId.Value && moved.Runs.Contains(r.Id)).ToListAsync(ct))
                run.EntryId = card;
            (await db.Entries.SingleAsync(e => e.Id == card, ct)).MergedIntoEntryId = null;
            db.EntryJoins.Remove(join);
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
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var other in staying)
        {
            foreach (var mine in sources.Where(s => s.EntryId == card))
            {
                var (a, b) = Pair(mine.Document.ContentHash, other.Document.ContentHash);
                if (!await db.CopyDecisions.AnyAsync(d => d.FirstHash == a && d.SecondHash == b, ct) && !db.CopyDecisions.Local.Any(d => d.FirstHash == a && d.SecondHash == b))
                    db.CopyDecisions.Add(new CopyDecision { FirstHash = a, SecondHash = b, Answer = CopyAnswer.NotSameBook, CreatedUtc = now });
            }
        }
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

    sealed record SourceRow(long EntryId, long DocumentId, EntryKind Kind, bool IsCurrent, bool Whole, bool HasFile, string ContentHash, int? PageCount, DateTime AddedUtc);

    static IQueryable<SourceRow> Sources(IQueryable<EntrySource> sources) => sources.Select(s => new SourceRow(
        s.EntryId, s.DocumentId, s.Entry.Kind, s.IsCurrent, s.FirstPdfPage == null,
        s.Document.Locations.Any(l => l.State != FileLocationState.Missing && l.SourceRoot.Availability != SourceRootAvailability.RemovedByUser),
        s.Document.ContentHash, s.Document.PageCount, s.Document.CreatedUtc));

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
    }

    sealed record MovedSource(long Id, bool WasCurrent);
}

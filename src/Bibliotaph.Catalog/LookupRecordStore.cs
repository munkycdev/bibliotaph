using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// A "Same book?" card waiting in Needs review: <see cref="Record"/>, a catalogue's record for the ISBN the book
/// prints, whose title isn't printed in it. <see cref="Hashes"/> are the book's files the record came for, one row
/// each (<see cref="Ids"/>), so a book with two copies is asked about once.
/// </summary>
public sealed record PendingLookup(IReadOnlyList<long> Ids, IReadOnlyList<string> Hashes, EntryId EntryId, long DocumentId, BookRecord Record, DateTime CreatedUtc);

/// <summary>
/// An answer to a "Same book?" card, with what undoing it needs: <see cref="Others"/> are the rows of the catalogue's
/// other records for the book that Same book answered too, as another book's.
/// </summary>
public sealed record LookupDecision(PendingLookup Match, LookupAnswer Answer, IReadOnlyList<long>? Others = null);

/// <summary>
/// What a lookup got for a file: the records it uses (their titles printed in the book, or said to be the book's), those
/// waiting for the user's answer, and those the user said are another book's.
/// </summary>
public sealed record SavedLookup(IReadOnlyList<BookRecord> Used, IReadOnlyList<BookRecord> Waiting, IReadOnlyList<BookRecord> Refused);

/// <summary>
/// The catalogues' records for each file's ISBNs, as its last lookup got them, and what the user said about those whose
/// title isn't printed in the book. The records a file uses become its looked-up suggestions
/// (<see cref="AssertionOrigin.Lookup"/>), here rather than in Look up, so an answer in Needs review, or from another
/// PC, fills the fields at once, without asking the catalogue again. A record the user gives by its page's address
/// (<see cref="LinkAsync"/>) is the book's as one answered Same book is, though no lookup finds it.
/// </summary>
public sealed class LookupRecordStore(IDbContextFactory<CatalogDbContext> contexts, MetadataStore metadata, VocabularyStore vocabularies, SettingsStore settings,
    TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Raised with the content hashes of the files the user has just said a record is or isn't of, by its page's
    /// address, here or on another PC, from any thread: Look up runs for them again, to read a record this PC hasn't
    /// and to say what the book uses now.
    /// </summary>
    public event EventHandler<IReadOnlyCollection<string>>? Linked;

    /// <summary>Before any record a lookup gives: the one the user gave.</summary>
    const int First = -1;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string Write(BookRecord record) => JsonSerializer.Serialize(record, Json);

    static BookRecord? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<BookRecord>(json, Json);

    /// <summary>
    /// Keeps the records a lookup of <paramref name="contentHash"/> got, in the order it gave them, with whether each
    /// one's title is printed in the book. A record not given again goes unless the user answered about it. None of
    /// this is the user's work, so library sync isn't told; their answers are.
    /// </summary>
    public Task<SavedLookup> SaveAsync(string contentHash, IReadOnlyList<BookRecord> records, Func<BookRecord, bool> printed, CancellationToken ct = default) =>
        SaveAsync(contentHash, records, printed, lookedUp: true, ct);

    /// <summary>
    /// Keeps records read by their ids for a file that wasn't looked up, as one that prints nothing to look up but
    /// whose record the user gave: the records an earlier lookup got stay as they are.
    /// </summary>
    public Task<SavedLookup> KeepAsync(string contentHash, IReadOnlyList<BookRecord> records, CancellationToken ct = default) =>
        SaveAsync(contentHash, records, _ => false, lookedUp: false, ct);

    async Task<SavedLookup> SaveAsync(string contentHash, IReadOnlyList<BookRecord> records, Func<BookRecord, bool> printed, bool lookedUp, CancellationToken ct)
    {
        using var quiet = CatalogWrites.Quiet();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.Where(r => r.ContentHash == contentHash).ToListAsync(ct);
        var given = records.DistinctBy(r => (r.Source, r.Key)).ToList();
        if (lookedUp)
            foreach (var row in rows.Where(r => r.Answer is null && !given.Any(g => g.Source == r.Source && g.Key == r.RecordKey)))
                db.LookupRecords.Remove(row);
        // A record the user said is the book's stays its record though this lookup didn't find it, as one given by its page's address.
        List<BookRecord> used = [.. rows.Where(r => r.Answer == LookupAnswer.SameBook && !given.Any(g => g.Source == r.Source && g.Key == r.RecordKey))
            .OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => Read(r.RecordJson)).OfType<BookRecord>()];
        var waiting = new List<BookRecord>();
        var refused = new List<BookRecord>();
        for (var i = 0; i < given.Count; i++)
        {
            var record = given[i];
            var row = rows.FirstOrDefault(r => r.Source == record.Source && r.RecordKey == record.Key);
            if (row is null)
            {
                row = new LookupRecord { ContentHash = contentHash, Source = record.Source, RecordKey = record.Key, CreatedUtc = _clock.GetUtcNow().UtcDateTime };
                db.LookupRecords.Add(row);
            }
            row.Position = lookedUp ? i : First;
            row.RecordJson = Write(record);
            row.Printed = printed(record);
            (row.Answer == LookupAnswer.NotThisBook ? refused : row.Printed || row.Answer == LookupAnswer.SameBook ? used : waiting).Add(record);
        }
        await db.SaveChangesAsync(ct);
        return new SavedLookup(used, waiting, refused);
    }

    /// <summary>
    /// Makes <paramref name="contentHash"/>'s looked-up suggestions on <paramref name="entryId"/> those of the records it
    /// uses: each field from the catalogue the user prefers that gives it, and the terms the records file the book
    /// under. A new name is proposed as the AI's are: it waits in Needs review, or joins the vocabulary at once where
    /// Settings › Vocabulary says so; one the user rejected is left out. With no record to use, the suggestions go.
    /// Returns whether the suggestions changed, and whether the vocabulary did.
    /// </summary>
    public async Task<(bool Changed, bool VocabularyChanged)> ApplyAsync(EntryId entryId, string contentHash, CancellationToken ct = default)
    {
        List<BookRecord> used;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            used = [.. (await db.LookupRecords.AsNoTracking()
                    .Where(r => r.ContentHash == contentHash && r.RecordJson != null && r.Answer != LookupAnswer.NotThisBook && (r.Printed || r.Answer == LookupAnswer.SameBook))
                    .OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.RecordJson).ToListAsync(ct))
                .Select(Read).OfType<BookRecord>()];
        }
        var run = string.Join('+', used.Select(r => $"{r.Source.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)}:{r.Key}"));
        var (terms, grew) = await TermProposalsAsync(used, ct);
        var changed = await metadata.ApplyRunAsync(entryId, contentHash, run, AssertionOrigin.Lookup, [.. BookRecords.Proposals(used), .. terms], ct);
        return (changed, grew);
    }

    /// <summary>
    /// Makes <paramref name="record"/> the book's in its catalogue, as the user says by giving its page there: for each
    /// of the book's files it is used before any other, whatever the file prints, and the catalogue's other records
    /// for the file are of another book. An answer as Same book is, so it goes to the user's other PCs. Returns the
    /// files' content hashes, none for a book with no file of its own; the caller applies their records again
    /// (<see cref="ApplyAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<string>> LinkAsync(EntryId entryId, BookRecord record, CancellationToken ct = default)
    {
        List<string> hashes;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            hashes = await WholeFilesAsync(db, entryId, ct);
            if (hashes.Count == 0) return [];
            var rows = await db.LookupRecords.Where(r => hashes.Contains(r.ContentHash) && r.Source == record.Source).ToListAsync(ct);
            foreach (var hash in hashes)
            {
                foreach (var other in rows.Where(r => r.ContentHash == hash && r.RecordKey != record.Key)) other.Answer = LookupAnswer.NotThisBook;
                var row = rows.FirstOrDefault(r => r.ContentHash == hash && r.RecordKey == record.Key);
                if (row is null)
                {
                    row = new LookupRecord { ContentHash = hash, Source = record.Source, RecordKey = record.Key, CreatedUtc = _clock.GetUtcNow().UtcDateTime };
                    db.LookupRecords.Add(row);
                }
                row.Position = First;
                row.RecordJson = Write(record);
                row.Answer = LookupAnswer.SameBook;
            }
            await db.SaveChangesAsync(ct);
        }
        Linked?.Invoke(this, hashes);
        return hashes;
    }

    /// <summary>
    /// Says <paramref name="source"/>'s record <paramref name="recordKey"/> isn't the book's, for each of its files, as
    /// Not this book says on a card: it's never used for the book again unless the user gives its page. Returns the
    /// content hashes of the files it was a record for; the caller applies their records again.
    /// </summary>
    public async Task<IReadOnlyList<string>> RefuseAsync(EntryId entryId, string source, string recordKey, CancellationToken ct = default)
    {
        List<string> hashes;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var files = await WholeFilesAsync(db, entryId, ct);
            var rows = await db.LookupRecords.Where(r => files.Contains(r.ContentHash) && r.Source == source && r.RecordKey == recordKey).ToListAsync(ct);
            if (rows.Count == 0) return [];
            foreach (var row in rows) row.Answer = LookupAnswer.NotThisBook;
            await db.SaveChangesAsync(ct);
            hashes = [.. rows.Select(r => r.ContentHash)];
        }
        Linked?.Invoke(this, hashes);
        return hashes;
    }

    /// <summary>
    /// What a book's records are kept by: the content hashes of the files that are the whole of it, not an adventure
    /// inside one, or for a book owned elsewhere, which has no file, the name it has on every PC (<see cref="KeyOf"/>).
    /// </summary>
    static async Task<List<string>> WholeFilesAsync(CatalogDbContext db, EntryId entryId, CancellationToken ct)
    {
        var hashes = await db.EntrySources.AsNoTracking().Where(s => s.EntryId == entryId.Value && s.FirstPdfPage == null && s.Entry.MergedIntoEntryId == null)
            .Select(s => s.Document.ContentHash).Distinct().ToListAsync(ct);
        if (hashes.Count > 0) return hashes;
        var elsewhere = await db.Entries.AsNoTracking().Where(e => e.Id == entryId.Value && e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null)
            .Select(e => (Guid?)e.SyncId).FirstOrDefaultAsync(ct);
        return elsewhere is { } id ? [KeyOf(id)] : [];
    }

    /// <summary>What the records of a book owned elsewhere are kept by, in a file's content hash's place: its sync id, the same on every PC.</summary>
    public static string KeyOf(Guid syncId) => syncId.ToString("N");

    /// <summary>The books owned elsewhere, each with what its records are kept by.</summary>
    public async Task<IReadOnlyList<(EntryId EntryId, string Key)>> GetFilelessAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await FilelessAsync(db, ct);
    }

    static async Task<List<(EntryId EntryId, string Key)>> FilelessAsync(CatalogDbContext db, CancellationToken ct) =>
        [.. (await db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null).OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.SyncId }).ToListAsync(ct)).Select(e => (new EntryId(e.Id), KeyOf(e.SyncId)))];

    /// <summary>
    /// The record of <paramref name="source"/>'s the book uses, the first of its files', for its details to name and
    /// link to; null when it uses none.
    /// </summary>
    public async Task<BookRecord?> GetUsedAsync(EntryId entryId, string source, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = await WholeFilesAsync(db, entryId, ct);
        if (hashes.Count == 0) return null;
        return Read(await db.LookupRecords.AsNoTracking()
            .Where(r => hashes.Contains(r.ContentHash) && r.Source == source && r.RecordJson != null && r.Answer != LookupAnswer.NotThisBook
                && (r.Printed || r.Answer == LookupAnswer.SameBook))
            .OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.RecordJson).FirstOrDefaultAsync(ct));
    }

    /// <summary>
    /// The records the user said are the file's, each by its catalogue and its id there, with whether this PC has read
    /// it: one it hasn't is an answer from another PC about a record no lookup here gave, as one given there by its
    /// page's address.
    /// </summary>
    public async Task<IReadOnlyList<(string Source, string Key, bool Read)>> GetKeptAsync(string contentHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.AsNoTracking().Where(r => r.ContentHash == contentHash && r.Answer == LookupAnswer.SameBook)
            .OrderBy(r => r.Id).Select(r => new { r.Source, r.RecordKey, Read = r.RecordJson != null }).ToListAsync(ct);
        return [.. rows.Select(r => (r.Source, r.RecordKey, r.Read))];
    }

    /// <summary>The records the user said aren't the file's, each by its catalogue and its id there: not worth reading again.</summary>
    public async Task<IReadOnlyList<(string Source, string Key)>> GetRefusedAsync(string contentHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.AsNoTracking().Where(r => r.ContentHash == contentHash && r.Answer == LookupAnswer.NotThisBook)
            .Select(r => new { r.Source, r.RecordKey }).ToListAsync(ct);
        return [.. rows.Select(r => (r.Source, r.RecordKey))];
    }

    /// <summary>
    /// The catalogues that found each of <paramref name="entryIds"/> (every entry's when null): those with a record one
    /// of its files uses, for the Library's Found online filter. An adventure inside a file is found as its file is.
    /// </summary>
    public async Task<IReadOnlyList<(EntryId EntryId, string Source)>> GetFoundByAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sources = db.EntrySources.AsNoTracking().Where(s => s.Entry.MergedIntoEntryId == null);
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            sources = sources.Where(s => ids.Contains(s.EntryId));
        }
        var used = db.LookupRecords.AsNoTracking()
            .Where(r => r.RecordJson != null && r.Answer != LookupAnswer.NotThisBook && (r.Printed || r.Answer == LookupAnswer.SameBook));
        var found = await sources.Join(used, s => s.Document.ContentHash, r => r.ContentHash, (s, r) => new { s.EntryId, r.Source })
            .Distinct().ToListAsync(ct);
        List<(EntryId, string)> all = [.. found.Select(f => (new EntryId(f.EntryId), f.Source))];
        // A book owned elsewhere is found as a file is, by the records kept under its own name.
        var fileless = (await FilelessAsync(db, ct)).Where(f => entryIds is null || entryIds.Contains(f.EntryId)).ToList();
        if (fileless.Count > 0)
        {
            var keys = fileless.Select(f => f.Key).ToList();
            var theirs = await used.Where(r => keys.Contains(r.ContentHash)).Select(r => new { r.ContentHash, r.Source }).Distinct().ToListAsync(ct);
            all.AddRange(theirs.Select(r => (fileless.First(f => f.Key == r.ContentHash).EntryId, r.Source)));
        }
        return all;
    }

    /// <summary>
    /// The records the files with these content hashes use, printed in them or answered Same book: what a catalogue
    /// calls the books beside one, whose line it may name ("Line: Book 2"), and the games it files them under.
    /// </summary>
    public async Task<IReadOnlyList<BookRecord>> GetUsedAsync(IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        if (contentHashes.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = contentHashes.ToList();
        var json = await db.LookupRecords.AsNoTracking()
            .Where(r => hashes.Contains(r.ContentHash) && r.RecordJson != null && r.Answer != LookupAnswer.NotThisBook && (r.Printed || r.Answer == LookupAnswer.SameBook))
            .OrderBy(r => r.Id).Select(r => r.RecordJson).ToListAsync(ct);
        return [.. json.Select(Read).OfType<BookRecord>()];
    }

    async Task<(List<MetadataProposal> Proposals, bool Grew)> TermProposalsAsync(List<BookRecord> records, CancellationToken ct)
    {
        var proposals = new List<MetadataProposal>();
        var grew = false;
        if (records.Count == 0) return (proposals, grew);
        foreach (var term in BookRecords.Terms(records, await vocabularies.GetAsync(ct)))
        {
            var key = term.Term?.Key;
            if (key is null)
            {
                var vocabulary = term.Field.Vocabulary!;
                var (proposed, state, added) = await vocabularies.ProposeTermAsync(vocabulary, term.Label, await settings.AddsAiTermsAsync(vocabulary, ct), ct);
                if (state == TermState.Rejected) continue;
                grew |= added;
                key = proposed.Key;
            }
            proposals.Add(new MetadataProposal(term.Field, key, AssertionOrigin.Lookup, term.Quote));
        }
        return (proposals, grew);
    }

    /// <summary>
    /// The "Same book?" cards waiting, oldest first: a record whose title isn't printed in the book, for a file still
    /// in the library, one card for each book and record.
    /// </summary>
    public async Task<IReadOnlyList<PendingLookup>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.AsNoTracking().Where(r => r.Answer == null && !r.Printed && r.RecordJson != null)
            .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var hashes = rows.Select(r => r.ContentHash).Distinct().ToList();
        var files = new Dictionary<string, (long DocumentId, long EntryId)>(StringComparer.Ordinal);
        foreach (var chunk in hashes.Chunk(500))
            foreach (var file in await db.EntrySources.AsNoTracking()
                .Where(s => s.FirstPdfPage == null && s.Entry.MergedIntoEntryId == null && chunk.Contains(s.Document.ContentHash))
                .Select(s => new { s.Document.ContentHash, s.DocumentId, s.EntryId }).ToListAsync(ct))
                files[file.ContentHash] = (file.DocumentId, file.EntryId);
        // A book owned elsewhere has no file to open: its card shows the record alone.
        foreach (var (entryId, key) in await FilelessAsync(db, ct)) files.TryAdd(key, (0, entryId.Value));
        return [.. rows.Where(r => files.ContainsKey(r.ContentHash))
            .GroupBy(r => (files[r.ContentHash].EntryId, r.Source, r.RecordKey))
            .Select(g => (Rows: g.ToList(), Record: Read(g.First().RecordJson)))
            .Where(g => g.Record is not null)
            .Select(g => new PendingLookup([.. g.Rows.Select(r => r.Id)], [.. g.Rows.Select(r => r.ContentHash)], new EntryId(files[g.Rows[0].ContentHash].EntryId),
                files[g.Rows[0].ContentHash].DocumentId, g.Record!, g.Rows[0].CreatedUtc))];
    }

    /// <summary>
    /// Answers a card, for each of the book's files the record came for. Same book also says the catalogue's other
    /// records still waiting for those files are another book's, as when Needs review asks which of two or three items
    /// a book is. Returns what undoes it, or null when the card isn't waiting any more. The caller applies the files'
    /// records again (<see cref="ApplyAsync"/>).
    /// </summary>
    public async Task<LookupDecision?> AnswerAsync(PendingLookup match, LookupAnswer answer, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.Where(r => match.Ids.Contains(r.Id) && r.Answer == null).ToListAsync(ct);
        if (rows.Count == 0) return null;
        foreach (var row in rows) row.Answer = answer;
        List<LookupRecord> others = [];
        if (answer == LookupAnswer.SameBook)
        {
            var hashes = rows.Select(r => r.ContentHash).ToList();
            others = await db.LookupRecords
                .Where(r => hashes.Contains(r.ContentHash) && r.Source == match.Record.Source && r.RecordKey != match.Record.Key && r.Answer == null && !r.Printed)
                .ToListAsync(ct);
            foreach (var other in others) other.Answer = LookupAnswer.NotThisBook;
        }
        await db.SaveChangesAsync(ct);
        return new LookupDecision(match with { Ids = [.. rows.Select(r => r.Id)], Hashes = [.. rows.Select(r => r.ContentHash)] }, answer, [.. others.Select(r => r.Id)]);
    }

    /// <summary>Undoes an answer: the card waits again, and so do the cards it answered with it.</summary>
    public async Task UndoAsync(LookupDecision decision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        List<long> ids = [.. decision.Match.Ids, .. decision.Others ?? []];
        await db.LookupRecords.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Answer, (LookupAnswer?)null), ct);
    }

    /// <summary>
    /// Takes another PC's answer about a record for a file (null when it was undone there), and applies the file's
    /// records again when this PC has them. Returns whether anything changed.
    /// </summary>
    public async Task<bool> ApplySyncedAsync(string contentHash, string source, string recordKey, LookupAnswer? answer, CancellationToken ct = default)
    {
        long? entryId;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var row = await db.LookupRecords.SingleOrDefaultAsync(r => r.ContentHash == contentHash && r.Source == source && r.RecordKey == recordKey, ct);
            if (row?.Answer == answer) return false;
            if (row is null)
            {
                // Kept for when this PC looks the file up, so it isn't asked here too.
                db.LookupRecords.Add(new LookupRecord
                {
                    ContentHash = contentHash, Source = source, RecordKey = recordKey, Position = int.MaxValue, Answer = answer, CreatedUtc = _clock.GetUtcNow().UtcDateTime,
                });
                await db.SaveChangesAsync(ct);
                // No lookup here may ever give a record given there by its page's address, so it's read by its id.
                if (answer == LookupAnswer.SameBook) Linked?.Invoke(this, [contentHash]);
                return true;
            }
            row.Answer = answer;
            await db.SaveChangesAsync(ct);
            if (row.RecordJson is null)
            {
                if (answer == LookupAnswer.SameBook) Linked?.Invoke(this, [contentHash]);
                return true;
            }
            entryId = await db.EntrySources.Where(s => s.FirstPdfPage == null && s.Entry.MergedIntoEntryId == null && s.Document.ContentHash == contentHash)
                .Select(s => (long?)s.EntryId).FirstOrDefaultAsync(ct)
                ?? (await FilelessAsync(db, ct)).Where(f => f.Key == contentHash).Select(f => (long?)f.EntryId.Value).FirstOrDefault();
        }
        if (entryId is { } id) await ApplyAsync(new EntryId(id), contentHash, ct);
        return true;
    }
}

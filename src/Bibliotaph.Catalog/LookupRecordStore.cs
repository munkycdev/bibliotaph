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

/// <summary>An answer to a "Same book?" card, with what undoing it needs.</summary>
public sealed record LookupDecision(PendingLookup Match, LookupAnswer Answer);

/// <summary>
/// What a lookup got for a file: the records it uses (their titles printed in the book, or said to be the book's), those
/// waiting for the user's answer, and those the user said are another book's.
/// </summary>
public sealed record SavedLookup(IReadOnlyList<BookRecord> Used, IReadOnlyList<BookRecord> Waiting, IReadOnlyList<BookRecord> Refused);

/// <summary>
/// The catalogues' records for each file's ISBNs, as its last lookup got them, and what the user said about those whose
/// title isn't printed in the book. The records a file uses become its looked-up suggestions
/// (<see cref="AssertionOrigin.Lookup"/>), here rather than in Look up, so an answer in Needs review, or from another
/// PC, fills the fields at once, without asking the catalogue again.
/// </summary>
public sealed class LookupRecordStore(IDbContextFactory<CatalogDbContext> contexts, MetadataStore metadata, VocabularyStore vocabularies, SettingsStore settings,
    TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string Write(BookRecord record) => JsonSerializer.Serialize(record, Json);

    static BookRecord? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<BookRecord>(json, Json);

    /// <summary>
    /// Keeps the records a lookup of <paramref name="contentHash"/> got, in the order it gave them, with whether each
    /// one's title is printed in the book. A record not given again goes unless the user answered about it. None of
    /// this is the user's work, so library sync isn't told; their answers are.
    /// </summary>
    public async Task<SavedLookup> SaveAsync(string contentHash, IReadOnlyList<BookRecord> records, Func<BookRecord, bool> printed, CancellationToken ct = default)
    {
        using var quiet = CatalogWrites.Quiet();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.Where(r => r.ContentHash == contentHash).ToListAsync(ct);
        var given = records.DistinctBy(r => (r.Source, r.Key)).ToList();
        foreach (var row in rows.Where(r => r.Answer is null && !given.Any(g => g.Source == r.Source && g.Key == r.RecordKey)))
            db.LookupRecords.Remove(row);
        var used = new List<BookRecord>();
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
            row.Position = i;
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
        return [.. found.Select(f => (new EntryId(f.EntryId), f.Source))];
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
        return [.. rows.Where(r => files.ContainsKey(r.ContentHash))
            .GroupBy(r => (files[r.ContentHash].EntryId, r.Source, r.RecordKey))
            .Select(g => (Rows: g.ToList(), Record: Read(g.First().RecordJson)))
            .Where(g => g.Record is not null)
            .Select(g => new PendingLookup([.. g.Rows.Select(r => r.Id)], [.. g.Rows.Select(r => r.ContentHash)], new EntryId(files[g.Rows[0].ContentHash].EntryId),
                files[g.Rows[0].ContentHash].DocumentId, g.Record!, g.Rows[0].CreatedUtc))];
    }

    /// <summary>
    /// Answers a card, for each of the book's files the record came for. Returns what undoes it, or null when the card
    /// isn't waiting any more. The caller applies the files' records again (<see cref="ApplyAsync"/>).
    /// </summary>
    public async Task<LookupDecision?> AnswerAsync(PendingLookup match, LookupAnswer answer, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LookupRecords.Where(r => match.Ids.Contains(r.Id) && r.Answer == null).ToListAsync(ct);
        if (rows.Count == 0) return null;
        foreach (var row in rows) row.Answer = answer;
        await db.SaveChangesAsync(ct);
        return new LookupDecision(match with { Ids = [.. rows.Select(r => r.Id)], Hashes = [.. rows.Select(r => r.ContentHash)] }, answer);
    }

    /// <summary>Undoes an answer: the card waits again.</summary>
    public async Task UndoAsync(LookupDecision decision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.LookupRecords.Where(r => decision.Match.Ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Answer, (LookupAnswer?)null), ct);
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
                return true;
            }
            row.Answer = answer;
            await db.SaveChangesAsync(ct);
            if (row.RecordJson is null) return true;
            entryId = await db.EntrySources.Where(s => s.FirstPdfPage == null && s.Entry.MergedIntoEntryId == null && s.Document.ContentHash == contentHash)
                .Select(s => (long?)s.EntryId).FirstOrDefaultAsync(ct);
        }
        if (entryId is { } id) await ApplyAsync(new EntryId(id), contentHash, ct);
        return true;
    }
}

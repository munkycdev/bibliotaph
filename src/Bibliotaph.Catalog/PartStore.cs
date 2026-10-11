using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// An adventure's pages in one copy of the file it is in, with the fingerprints of its first and last pages there, so
/// they can be found in another copy or a new version (slice 5 plan, choice 12). The current one is in the copy the
/// file opens.
/// </summary>
public sealed record PartSource(long DocumentId, PartPages Pages, string? FirstFingerprint = null, string? LastFingerprint = null, bool IsCurrent = false);

/// <summary>An adventure inside a file (slice 5): its own entry, the file's entry, and its pages in each copy of the file that has them.</summary>
public sealed record PartEntry(EntryId EntryId, EntryId ParentEntryId, IReadOnlyList<PartSource> Sources)
{
    /// <summary>Its pages in <paramref name="documentId"/>, or null when they weren't found in that copy.</summary>
    public PartSource? On(long documentId) => Sources.FirstOrDefault(s => s.DocumentId == documentId);
}

/// <summary>An adventure the user removed from a file, with what was set on it, so Undo can put it back.</summary>
public sealed record RemovedPart(RemovedEntry Entry, EntryId ParentEntryId, IReadOnlyList<PartSource> Sources);

/// <summary>
/// The adventures inside compilation files (slice 5 plan, choices 1 to 3 and 12; catalog entry design, choice 9). A
/// part is an entry of kind <see cref="EntryKind.Part"/> pointing at the file's entry, with one source per copy of the
/// file: that copy's first and last PDF page. The file never changes. Parts in one copy never share a page.
/// </summary>
public sealed class PartStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>The type a part is when it is marked (choice 4: "Index as its own adventure").</summary>
    public const string AdventureType = "adventure";

    /// <summary>The type suggested for a file once adventures are marked in it (choice 8).</summary>
    public const string AnthologyType = "anthology";

    /// <summary>The quote on the Anthology suggestion: where it came from.</summary>
    public const string AnthologyQuote = "It holds adventures of its own";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly EntryStore _entries = new(contexts, clock);

    /// <summary>The adventures marked in <paramref name="parentId"/>'s file, in the order they were added.</summary>
    public async Task<IReadOnlyList<PartEntry>> GetPartsAsync(EntryId parentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await LoadAsync(db.Entries.Where(e => e.ParentEntryId == parentId.Value), ct);
    }

    /// <summary>The adventure <paramref name="partId"/>, or null when it isn't one.</summary>
    public async Task<PartEntry?> GetPartAsync(EntryId partId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await LoadAsync(db.Entries.Where(e => e.Id == partId.Value), ct) is [var part, ..] ? part : null;
    }

    /// <summary>The adventures with pages in <paramref name="documentId"/>, and those pages, for their covers.</summary>
    public async Task<IReadOnlyList<(EntryId EntryId, PartPages Pages)>> GetPagesInAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.EntrySources.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.FirstPdfPage != null && s.Entry.Kind == EntryKind.Part)
            .Select(s => new { s.EntryId, First = s.FirstPdfPage!.Value, Last = s.LastPdfPage!.Value })
            .ToListAsync(ct);
        return [.. rows.OrderBy(r => r.First).Select(r => (new EntryId(r.EntryId), new PartPages(r.First, r.Last)))];
    }

    /// <summary>The files with adventures marked in them, for following them into copies and versions.</summary>
    public async Task<IReadOnlyList<EntryId>> GetParentsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Part && e.ParentEntryId != null)
            .Select(e => e.ParentEntryId!.Value).Distinct().ToListAsync(ct)).Select(id => new EntryId(id))];
    }

    /// <summary>
    /// Marks pages of a copy of a file as an adventure of its own (choice 4): a new entry with the user's title,
    /// typed Adventure, with these pages as its current source. The file is suggested as an Anthology (choice 8).
    /// Null when the file's entry can't hold parts (it isn't a file's own card, or the copy isn't one of its copies),
    /// or the pages overlap an adventure already marked in that copy.
    /// </summary>
    public Task<EntryId?> AddAsync(EntryId parentId, PartSource source, string title, CancellationToken ct = default) =>
        AddAsync(parentId, source, title, [], null, ct);

    /// <summary>
    /// <see cref="AddAsync(EntryId, PartSource, string, CancellationToken)"/>, with values a proposal read for the
    /// adventure, such as the levels its blurb gives (slice 5 plan, choice 5): suggestions of their own origin, read
    /// from the copy with <paramref name="contentHash"/>, which the user can keep or reject.
    /// </summary>
    public async Task<EntryId?> AddAsync(EntryId parentId, PartSource source, string title, IReadOnlyList<MetadataProposal> suggestions, string? contentHash,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await CanHoldPartsAsync(db, parentId, source.DocumentId, ct)) return null;
        if (await OverlapsAsync(db, source, null, ct)) return null;
        var now = _clock.GetUtcNow().UtcDateTime;
        var part = new Entry { Kind = EntryKind.Part, ParentEntryId = parentId.Value, CreatedUtc = now };
        part.Sources.Add(ToRow(source with { IsCurrent = true }));
        part.Assertions.Add(UserValue(MetadataFields.Title, title.Trim(), now));
        part.Assertions.Add(UserValue(MetadataFields.Types, AdventureType, now));
        foreach (var suggestion in suggestions.Where(s => s.Origin != AssertionOrigin.User && s.Normalized.Length > 0))
        {
            part.Assertions.Add(new Assertion
            {
                Field = suggestion.Field.Key,
                ValueJson = JsonSerializer.Serialize(suggestion.Value),
                NormalizedValue = suggestion.Normalized,
                Origin = suggestion.Origin,
                ContentHash = contentHash,
                EvidencePagesJson = suggestion.Pages is { Count: > 0 } pages ? JsonSerializer.Serialize(pages) : null,
                EvidenceQuote = suggestion.Quote,
                State = AssertionState.Provisional,
                CreatedUtc = now,
            });
        }
        db.Entries.Add(part);
        await SuggestAnthologyAsync(db, parentId, now, ct);
        await db.SaveChangesAsync(ct);
        return new EntryId(part.Id);
    }

    /// <summary>
    /// Replaces an adventure's pages in every copy with <paramref name="sources"/> ("Edit range", or what following
    /// the file into its copies found). The one in <paramref name="currentDocumentId"/> becomes current. False when the
    /// part is gone, a source isn't in a copy of its file, or pages overlap another adventure in that copy.
    /// </summary>
    public async Task<bool> SetSourcesAsync(EntryId partId, IReadOnlyList<PartSource> sources, long? currentDocumentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var part = await db.Entries.Include(e => e.Sources).SingleOrDefaultAsync(e => e.Id == partId.Value && e.Kind == EntryKind.Part, ct);
        if (part?.ParentEntryId is not { } parent) return false;
        var copies = await db.EntrySources.Where(s => s.EntryId == parent && s.FirstPdfPage == null).Select(s => s.DocumentId).ToListAsync(ct);
        if (sources.Any(s => !copies.Contains(s.DocumentId)) || sources.DistinctBy(s => s.DocumentId).Count() != sources.Count) return false;
        foreach (var source in sources)
            if (await OverlapsAsync(db, source, part.Id, ct)) return false;
        db.EntrySources.RemoveRange(part.Sources);
        // Saved first: the unique index allows one current source per entry at any moment.
        await db.SaveChangesAsync(ct);
        db.EntrySources.AddRange(sources.Select(s => ToRow(s with { IsCurrent = s.DocumentId == currentDocumentId }, part.Id)));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Makes the adventure's pages in <paramref name="documentId"/> its current source, as its file opens that copy
    /// now. False when it has no pages there.
    /// </summary>
    public async Task<bool> MakeCurrentAsync(EntryId partId, long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sources = await db.EntrySources.Where(s => s.EntryId == partId.Value).ToListAsync(ct);
        if (sources.All(s => s.DocumentId != documentId)) return false;
        if (sources.Single(s => s.DocumentId == documentId).IsCurrent) return true;
        foreach (var source in sources) source.IsCurrent = false;
        await db.SaveChangesAsync(ct);
        sources.Single(s => s.DocumentId == documentId).IsCurrent = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// "Remove adventure": the part's card goes, with everything set on it, and the file is whole again. The pages
    /// were never taken out of the file, so nothing else changes. When it was the file's last adventure, the Anthology
    /// suggestion goes too unless the user kept it. Returns what Undo needs, or null when it isn't a part.
    /// </summary>
    public async Task<RemovedPart?> RemoveAsync(EntryId partId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var part = await db.Entries.Include(e => e.Assertions).Include(e => e.Rejections).Include(e => e.Sources)
            .SingleOrDefaultAsync(e => e.Id == partId.Value && e.Kind == EntryKind.Part, ct);
        if (part?.ParentEntryId is not { } parent) return null;
        var removed = new RemovedPart(await EntryStore.CaptureAsync(db, part, ct), new EntryId(parent), [.. part.Sources.Select(ToSource)]);
        db.Entries.Remove(part);
        await db.SaveChangesAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.ParentEntryId == parent && e.Kind == EntryKind.Part, ct))
            await db.Assertions.Where(a => a.EntryId == parent && a.Field == MetadataFields.Types.Key && a.NormalizedValue == AnthologyType
                && a.Origin == AssertionOrigin.Rule && a.ContentHash == null && a.State == AssertionState.Provisional).ExecuteDeleteAsync(ct);
        return removed;
    }

    /// <summary>
    /// Undoes <see cref="RemoveAsync"/>: the adventure comes back with what was set on it, as a new card, in the copies
    /// of its file that are still there and where its pages are still free. Null when its file has gone.
    /// </summary>
    public async Task<EntryId?> RestoreAsync(RemovedPart removed, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var parent = removed.ParentEntryId.Value;
        if (!await db.Entries.AnyAsync(e => e.Id == parent && e.MergedIntoEntryId == null, ct)) return null;
        var copies = await db.EntrySources.Where(s => s.EntryId == parent && s.FirstPdfPage == null).Select(s => s.DocumentId).ToListAsync(ct);
        var part = new Entry { Kind = EntryKind.Part, ParentEntryId = parent, CreatedUtc = removed.Entry.CreatedUtc };
        foreach (var source in removed.Sources.Where(s => copies.Contains(s.DocumentId)))
            if (!await OverlapsAsync(db, source, null, ct)) part.Sources.Add(ToRow(source));
        await SuggestAnthologyAsync(db, removed.ParentEntryId, _clock.GetUtcNow().UtcDateTime, ct);
        await _entries.RestoreIntoAsync(db, part, removed.Entry, ct);
        return new EntryId(part.Id);
    }

    static async Task<bool> CanHoldPartsAsync(CatalogDbContext db, EntryId parentId, long documentId, CancellationToken ct) =>
        await db.EntrySources.AnyAsync(s => s.EntryId == parentId.Value && s.DocumentId == documentId && s.FirstPdfPage == null
            && s.Entry.Kind == EntryKind.Whole && s.Entry.ParentEntryId == null && s.Entry.MergedIntoEntryId == null, ct);

    /// <summary>Whether <paramref name="source"/>'s pages share one with another adventure's in that copy (choice 3).</summary>
    static async Task<bool> OverlapsAsync(CatalogDbContext db, PartSource source, long? except, CancellationToken ct) =>
        await db.EntrySources.AnyAsync(s => s.DocumentId == source.DocumentId && s.FirstPdfPage != null && s.EntryId != except
            && s.FirstPdfPage <= source.Pages.Last && source.Pages.First <= s.LastPdfPage, ct);

    /// <summary>Suggests Anthology as the file's type (choice 8), unless any source already has, or the user said no.</summary>
    static async Task SuggestAnthologyAsync(CatalogDbContext db, EntryId parentId, DateTime now, CancellationToken ct)
    {
        var id = parentId.Value;
        var field = MetadataFields.Types.Key;
        if (await db.Assertions.AnyAsync(a => a.EntryId == id && a.Field == field && a.NormalizedValue == AnthologyType, ct)
            || await db.Rejections.AnyAsync(r => r.EntryId == id && r.Field == field && r.NormalizedValue == AnthologyType, ct)) return;
        db.Assertions.Add(new Assertion
        {
            EntryId = id,
            Field = field,
            ValueJson = JsonSerializer.Serialize(AnthologyType),
            NormalizedValue = AnthologyType,
            Origin = AssertionOrigin.Rule,
            EvidenceQuote = AnthologyQuote,
            State = AssertionState.Provisional,
            CreatedUtc = now,
        });
    }

    static Assertion UserValue(MetadataField field, string value, DateTime now) => new()
    {
        Field = field.Key,
        ValueJson = JsonSerializer.Serialize(value),
        NormalizedValue = MetadataValues.Normalize(field, value),
        Origin = AssertionOrigin.User,
        State = AssertionState.Confirmed,
        CreatedUtc = now,
        DecidedUtc = now,
    };

    static EntrySource ToRow(PartSource source, long entryId = 0) => new()
    {
        EntryId = entryId,
        DocumentId = source.DocumentId,
        FirstPdfPage = source.Pages.First,
        LastPdfPage = source.Pages.Last,
        FirstFingerprint = source.FirstFingerprint,
        LastFingerprint = source.LastFingerprint,
        IsCurrent = source.IsCurrent,
    };

    static PartSource ToSource(EntrySource s) =>
        new(s.DocumentId, new PartPages(s.FirstPdfPage!.Value, s.LastPdfPage!.Value), s.FirstFingerprint, s.LastFingerprint, s.IsCurrent);

    static async Task<IReadOnlyList<PartEntry>> LoadAsync(IQueryable<Entry> entries, CancellationToken ct)
    {
        var parts = await entries.AsNoTracking().Include(e => e.Sources)
            .Where(e => e.Kind == EntryKind.Part && e.ParentEntryId != null && e.MergedIntoEntryId == null)
            .OrderBy(e => e.Id).ToListAsync(ct);
        return [.. parts.Select(p => new PartEntry(new EntryId(p.Id), new EntryId(p.ParentEntryId!.Value),
            [.. p.Sources.Where(s => s.FirstPdfPage != null).OrderByDescending(s => s.IsCurrent).ThenBy(s => s.DocumentId).Select(ToSource)]))];
    }
}

using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>An entry and the document its card shows: the current source, which opens and supplies cover and pages.</summary>
public sealed record EntryDocument(EntryId EntryId, long DocumentId, EntryKind Kind);

/// <summary>A document, its content hash, and the whole-document entry it backs.</summary>
public sealed record DocumentEntry(long DocumentId, string ContentHash, EntryId EntryId);

/// <summary>
/// Entries in catalog.db (catalog entry design): which document each library card shows, and which card a document
/// belongs to. Processing works on documents and the user's decisions on entries; this is where one turns into the other.
/// </summary>
public sealed class EntryStore(IDbContextFactory<CatalogDbContext> contexts)
{
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

    /// <summary>The entries whose cards show <paramref name="documentId"/>: those it is the current source of.</summary>
    public async Task<IReadOnlyList<EntryDocument>> GetShownByAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.EntrySources.AsNoTracking()
            .Where(s => s.IsCurrent && s.DocumentId == documentId)
            .Select(s => new { s.EntryId, s.Entry.Kind })
            .ToListAsync(ct);
        return [.. rows.Select(r => new EntryDocument(new EntryId(r.EntryId), documentId, r.Kind))];
    }

    /// <summary>The document <paramref name="entryId"/>'s card shows, or null for an entry with no file.</summary>
    public async Task<long?> GetCurrentDocumentAsync(EntryId entryId, CancellationToken ct = default) =>
        (await GetCurrentAsync([entryId], ct)) is [var current, ..] ? current.DocumentId : null;

    /// <summary>
    /// The document each entry's card shows, for <paramref name="entryIds"/> or, when it is null, every entry with
    /// one. An entry with no file (owned elsewhere) isn't listed.
    /// </summary>
    public async Task<IReadOnlyList<EntryDocument>> GetCurrentAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var current = db.EntrySources.AsNoTracking().Where(s => s.IsCurrent);
        if (entryIds is not null)
        {
            if (entryIds.Count == 0) return [];
            var ids = entryIds.Select(e => e.Value).Distinct().ToList();
            current = current.Where(s => ids.Contains(s.EntryId));
        }
        var rows = await current.Select(s => new { s.EntryId, s.DocumentId, s.Entry.Kind }).ToListAsync(ct);
        return [.. rows.Select(r => new EntryDocument(new EntryId(r.EntryId), r.DocumentId, r.Kind))];
    }
}

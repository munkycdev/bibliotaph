using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext() => database.CreateContext();
}

static class TestEntries
{
    /// <summary>
    /// Adds a document with content hash <paramref name="hash"/> (64 of that character) and its whole-document entry.
    /// The entry's id is well above the document's, so a test that passes one for the other fails.
    /// </summary>
    public static async Task<EntryId> AddAsync(CatalogDatabase database, char hash)
    {
        await using var db = database.CreateContext();
        var document = db.Documents.Add(new Document { ContentHash = new string(hash, 64), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
        await db.SaveChangesAsync();
        var entry = db.Entries.Add(new Entry { Id = document.Id + 1000, Kind = EntryKind.Whole, CreatedUtc = DateTime.UtcNow }).Entity;
        db.EntrySources.Add(new EntrySource { Entry = entry, DocumentId = document.Id, IsCurrent = true });
        await db.SaveChangesAsync();
        return new EntryId(entry.Id);
    }
}

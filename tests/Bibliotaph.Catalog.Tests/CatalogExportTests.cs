using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 4j: the JSON export holds every table of the user's work, whatever tables are added later.</summary>
public sealed class CatalogExportTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-export-").FullName;
    CatalogDatabase _db = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _db.MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_JSON_export_holds_every_row_and_column_of_every_table_in_the_catalog()
    {
        await SeedEveryTableAsync(_db);
        await using var db = _db.CreateContext();
        var tables = CatalogExport.Tables(db.Model);
        // A table added later fails here until the seed below gives it a row, so its export is checked too.
        foreach (var table in tables)
            Assert.True(await CountAsync(db, table.GetTableName()!) > 0, $"Add a row to {table.GetTableName()} in {nameof(SeedEveryTableAsync)}.");

        using var stream = new MemoryStream();
        await new CatalogExport(new Factory(_db)).WriteJsonAsync(stream, "0.4.0+test", new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), Ct);
        using var json = JsonDocument.Parse(stream.ToArray());
        var root = json.RootElement;

        Assert.Equal("bibliotaph-catalog", root.GetProperty("format").GetString());
        Assert.Equal(CatalogFile.KnownMigrations[^1], root.GetProperty("schema").GetString());
        Assert.Equal("2026-10-10T12:00:00Z", root.GetProperty("exportedUtc").GetString());
        Assert.Contains("zero-based", root.GetProperty("conventions").GetProperty("pages").GetString(), StringComparison.Ordinal);
        var exported = root.GetProperty("tables");
        Assert.Equal(tables.Select(t => t.GetTableName()).Order(StringComparer.Ordinal), exported.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var table in tables)
        {
            var name = table.GetTableName()!;
            var rows = exported.GetProperty(name).EnumerateArray().ToList();
            Assert.Equal(await CountAsync(db, name), rows.Count);
            var columns = table.GetProperties().Select(p => p.GetColumnName()).Where(c => !CatalogExport.ExcludedColumns.Contains($"{name}.{c}"));
            Assert.All(rows, row => Assert.Equal(columns.Order(StringComparer.Ordinal), row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        }

        // Values read as their kinds: numbers, true and false, UTC times, names; and nothing particular to this computer.
        var file = exported.GetProperty("file_location").EnumerateArray().Single(f => f.GetProperty("relative_path").GetString() == "Book.pdf");
        Assert.Equal(JsonValueKind.Number, file.GetProperty("size_bytes").ValueKind);
        Assert.Equal("Present", file.GetProperty("state").GetString());
        Assert.Equal("2026-10-01T09:30:00Z", file.GetProperty("modified_utc").GetString());
        Assert.False(file.TryGetProperty("ntfs_file_id", out _));
        Assert.True(exported.GetProperty("entry_source")[0].GetProperty("is_current").GetBoolean());
        Assert.Equal("2026-11-07", exported.GetProperty("session_pack")[0].GetProperty("date").GetString());
        Assert.Equal("The owlbear is a red herring.", exported.GetProperty("note")[0].GetProperty("text").GetString());
    }

    static async Task<long> CountAsync(CatalogDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>One row, or a few, in every table of the catalog, joined up as the app would make them.</summary>
    internal static async Task SeedEveryTableAsync(CatalogDatabase database)
    {
        var now = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        await using var db = database.CreateContext();
        var root = new SourceRoot { Path = Path.Combine(Path.GetTempPath(), "RPG Library"), VolumeSerial = "1234ABCD", Availability = SourceRootAvailability.Online, AddedUtc = now };
        var book = new Document { ContentHash = new string('a', 64), Format = "pdf", PageCount = 3, CreatedUtc = now };
        var revision = new Document { ContentHash = new string('b', 64), Format = "pdf", PageCount = 3, CreatedUtc = now };
        db.AddRange(root, book, revision);
        await db.SaveChangesAsync(Ct);

        var zip = new FileLocation { SourceRoot = root, RelativePath = "Tokens.zip", SizeBytes = 10, ModifiedUtc = now, ContentHash = new string('c', 64), LastSeenUtc = now };
        db.FileLocations.AddRange(
            new FileLocation { SourceRoot = root, RelativePath = "Book.pdf", SizeBytes = 1000, ModifiedUtc = now, NtfsFileId = "F:1", ContentHash = book.ContentHash, DocumentId = book.Id, LastSeenUtc = now },
            new FileLocation { SourceRoot = root, RelativePath = "Book v2.pdf", SizeBytes = 1100, ModifiedUtc = now, ContentHash = revision.ContentHash, DocumentId = revision.Id, LastSeenUtc = now },
            zip,
            new FileLocation { SourceRoot = root, RelativePath = Path.Combine("Tokens.zip", "Goblin.png"), SizeBytes = 5, ModifiedUtc = now, Container = zip, EntryPath = "Goblin.png", EntryCrc32 = 42, Problem = "It is damaged.", LastSeenUtc = now });
        var entry = new Entry { Kind = EntryKind.Whole, CreatedUtc = now };
        var joined = new Entry { Kind = EntryKind.Whole, CreatedUtc = now };
        var pack = new Entry { Kind = EntryKind.Pack, CreatedUtc = now };
        db.Entries.AddRange(entry, joined, pack);
        await db.SaveChangesAsync(Ct);
        joined.MergedIntoEntryId = entry.Id;

        db.EntrySources.Add(new EntrySource { EntryId = entry.Id, DocumentId = book.Id, IsCurrent = true });
        db.EntryJoins.Add(new EntryJoin { EntryId = entry.Id, JoinedEntryId = joined.Id, DocumentId = revision.Id, MatchedDocumentId = book.Id, MovedJson = "{}", CreatedUtc = now });
        db.CopyDecisions.Add(new CopyDecision { FirstHash = book.ContentHash, SecondHash = revision.ContentHash, Answer = CopyAnswer.NotSameBook, CreatedUtc = now });
        db.VersionProposals.Add(new VersionProposal { DocumentId = revision.Id, MatchedDocumentId = book.Id, Evidence = VersionEvidence.SharedPages, SharedPages = 2, ComparedPages = 3, CreatedUtc = now });
        db.PackDecisions.Add(new PackDecision { SourceRootId = root.Id, FolderPath = "Tokens.zip", IsArchive = true, Answer = PackAnswer.Packed, EntryId = pack.Id, CreatedUtc = now });
        db.ElsewhereMatches.Add(new ElsewhereMatch { EntryId = entry.Id, DocumentId = revision.Id, Answer = ElsewhereAnswer.SeparateBook, CreatedUtc = now });
        db.Assertions.Add(new Assertion { EntryId = entry.Id, Field = "title", ValueJson = "\"Book\"", NormalizedValue = "book", Origin = AssertionOrigin.User, State = AssertionState.Confirmed, CreatedUtc = now });
        db.Rejections.Add(new Rejection { EntryId = entry.Id, Field = "type", NormalizedValue = "adventure", CreatedUtc = now });
        var pages = new PageRef { DocumentId = book.Id, FirstPdfPage = 0, LastPdfPage = 1, FirstLabel = "i", LastLabel = "ii" };
        var notePages = new PageRef { DocumentId = book.Id, FirstPdfPage = 2, LastPdfPage = 2 };
        db.PageRefs.AddRange(pages, notePages);
        db.Favorites.Add(new Favorite { EntryId = entry.Id, CreatedUtc = now });
        db.ReadingStates.Add(new ReadingState { EntryId = entry.Id, DocumentId = book.Id, PageIndex = 2, OpenedUtc = now });
        var collection = new CollectionNode { Name = "Winter campaign", Pinned = true, CreatedUtc = now, UsedUtc = now };
        db.Collections.Add(collection);
        db.SmartViews.Add(new SmartView { Name = "Unread", Definition = "{\"version\":1}", CreatedUtc = now, UpdatedUtc = now });
        var session = new SessionPack { Title = "Session 1", Date = new DateOnly(2026, 11, 7), CreatedUtc = now, TouchedUtc = now };
        db.SessionPacks.Add(session);
        db.Settings.Add(new Setting { Key = SettingKeys.StartPage, Value = "Library" });
        var term = new VocabularyTerm { Vocabulary = "system", Key = "test-system", Label = "Test System", Origin = TermOrigin.User, State = TermState.Active, CreatedUtc = now };
        db.VocabularyTerms.Add(term);
        db.ClassificationRuns.Add(new ClassificationRun { Id = "run-1", EntryId = entry.Id, ContentHash = book.ContentHash, Provider = "ollama", Model = "test", PromptVersion = 1, SchemaVersion = 1, StartedUtc = now, Outcome = "complete" });
        db.IgnoredFolderLabels.Add(new IgnoredFolderLabel { Folder = "adventures", Vocabulary = "type", TermKey = "adventure", CreatedUtc = now });
        await db.SaveChangesAsync(Ct);

        db.CollectionItems.Add(new CollectionItem { CollectionId = collection.Id, EntryId = entry.Id, AddedUtc = now });
        db.Notes.AddRange(
            new Note { EntryId = entry.Id, Text = "The owlbear is a red herring.", CreatedUtc = now, UpdatedUtc = now },
            new Note { EntryId = entry.Id, PageRefId = notePages.Id, Text = "Read this aloud.", CreatedUtc = now, UpdatedUtc = now });
        var section = new SessionSection { PackId = session.Id, Name = "Maps", Position = 0 };
        db.SessionSections.Add(section);
        db.VocabularyAliases.Add(new VocabularyAlias { TermId = term.Id, Text = "TS", Normalized = "ts" });
        await db.SaveChangesAsync(Ct);
        db.SessionItems.Add(new SessionItem { PackId = session.Id, SectionId = section.Id, Position = 0, EntryId = entry.Id, PageRefId = pages.Id, Label = "Ambush", Note = "Round 1", AddedUtc = now });
        await db.SaveChangesAsync(Ct);
    }
}

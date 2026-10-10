using Bibliotaph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A library folder as a backup holds it: where it was, on which disk, and how many files it had.</summary>
public sealed record BackupRoot(long Id, string Path, string? VolumeSerial, SourceRootAvailability Availability, int Files);

/// <summary>How much user work a catalog holds, for a backup's manifest and the restore screen.</summary>
public sealed record CatalogCounts(int Books, int Documents, int Collections, int SessionPacks, int Notes)
{
    /// <summary>Nothing worth a backup: no book, and so nothing done with one.</summary>
    public bool IsEmpty => Books == 0 && Documents == 0;
}

/// <summary>
/// A catalog.db that isn't the one the app runs on: the copy a backup is made from, or a backup being restored (slice
/// 4j). Its connections aren't pooled, so the file can be zipped, moved or deleted as soon as the work on it is done.
/// </summary>
public sealed class CatalogFile(string path) : IDbContextFactory<CatalogDbContext>
{
    public string FilePath => path;

    string ConnectionString(SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false }.ToString();

    public CatalogDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(ConnectionString(SqliteOpenMode.ReadWrite)).Options);

    /// <summary>The migrations this version of Bibliotaph knows, oldest first; the last is its schema.</summary>
    public static IReadOnlyList<string> KnownMigrations { get; } = LoadKnownMigrations();

    static List<string> LoadKnownMigrations()
    {
        using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite("Data Source=:memory:").Options);
        return [.. db.Database.GetMigrations()];
    }

    /// <summary>
    /// The migrations applied to the file, oldest first, opened read-only so a damaged file is never written to. Null
    /// when it isn't a catalog Bibliotaph made, or SQLite finds it damaged.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ReadMigrationsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(ct);
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check";
                if (await check.ExecuteScalarAsync(ct) as string != "ok") return null;
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
            var migrations = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) migrations.Add(reader.GetString(0));
            return migrations.Count == 0 ? null : migrations;
        }
        catch (SqliteException)
        {
            // Not a database, or one without the migrations table.
            return null;
        }
    }

    /// <summary>
    /// Applies the migrations it lacks, as startup does for catalog.db (slice 4j plan, choice 5). No copy is kept first:
    /// this file is itself a copy, and the backup it came from is untouched.
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>The library folders the user hasn't removed, in the order they were added.</summary>
    public async Task<IReadOnlyList<BackupRoot>> GetRootsAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var rows = await db.SourceRoots.AsNoTracking()
            .Where(r => r.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Path, r.VolumeSerial, r.Availability, Files = r.Files.Count(f => f.ContainerId == null && f.State != FileLocationState.Missing) })
            .ToListAsync(ct);
        return [.. rows.Select(r => new BackupRoot(r.Id, r.Path, r.VolumeSerial, r.Availability, r.Files))];
    }

    public async Task<CatalogCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        // Cards: not a part of a compilation, an image inside a pack, nor a copy that joined another card.
        var books = await db.Entries.CountAsync(e => e.MergedIntoEntryId == null && e.ParentEntryId == null
            && (e.Kind == EntryKind.Pack || e.Kind == EntryKind.Elsewhere || e.Sources.Any()), ct);
        return new CatalogCounts(books, await db.Documents.CountAsync(ct), await db.Collections.CountAsync(ct),
            await db.SessionPacks.CountAsync(ct), await db.Notes.CountAsync(ct));
    }

    /// <summary>
    /// Readies a restored catalog for this computer (slice 4j plan, choice 4), before it replaces catalog.db. A folder
    /// given a new place takes it as "Point to its new place" does (<see cref="SourceRootStore.RelocateAsync"/>), so its
    /// files match by their paths under it and keep their hashes; one left without is offline until the user points it
    /// somewhere. Every folder's volume serial and file IDs are forgotten, being another computer's or another disk's,
    /// and read again at its first scan. Window positions stay with the computer they were saved on. False when a new
    /// place is already another folder's. <paramref name="found"/> are the folders found where the backup says they are;
    /// <paramref name="places"/> gives others their new places, and one not in either is offline.
    /// </summary>
    public async Task<bool> PrepareRestoredAsync(IReadOnlyDictionary<long, string?> places, IReadOnlySet<long> found, CancellationToken ct = default)
    {
        var roots = new SourceRootStore(this);
        foreach (var (rootId, place) in places)
            if (place is not null && !await roots.RelocateAsync(rootId, place, ct)) return false;

        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var root in await db.SourceRoots.Where(r => r.Availability != SourceRootAvailability.RemovedByUser).ToListAsync(ct))
            root.Availability = found.Contains(root.Id) || places.GetValueOrDefault(root.Id) is not null ? SourceRootAvailability.Online : SourceRootAvailability.Offline;
        await db.SaveChangesAsync(ct);
        await db.SourceRoots.ExecuteUpdateAsync(u => u.SetProperty(r => r.VolumeSerial, (string?)null), ct);
        await db.FileLocations.ExecuteUpdateAsync(u => u.SetProperty(f => f.NtfsFileId, (string?)null), ct);
        await db.Settings.Where(s => s.Key == SettingKeys.ReaderWindow).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Folds any write-ahead log into the file and leaves it in rollback-journal mode, so the file alone is the whole
    /// catalog and can be moved into place. The app puts catalog.db back in WAL mode after the move.
    /// </summary>
    public async Task SealAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE";
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Puts a catalog that has just been moved into place back in WAL mode, as migrating a new one does.</summary>
    public async Task UseWalAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        await command.ExecuteNonQueryAsync(ct);
    }
}

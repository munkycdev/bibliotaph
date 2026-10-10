using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Processing;

/// <summary>A restore staged and waiting for the next start: which backup, from when, and what it brings.</summary>
public sealed record RestoreRequest(string Backup, DateTime BackupCreatedUtc, bool IncludesIndex, int OfflineFolders);

/// <summary>What the start after a restore found: the backup swapped in, or why not, with the user's catalog kept as it was.</summary>
public sealed record RestoreOutcome(RestoreRequest? Request, string? Problem, string? PreviousCatalogBackup)
{
    public bool Restored => Problem is null;
}

/// <summary>
/// The swap that finishes a restore (slice 4j plan, choice 4), done at the start after it was staged and before
/// anything opens the databases, as Start over's purge is, because Windows won't replace an open SQLite file. The
/// catalog in use is first copied to the backups folder ("before-restore"), then moved aside, and the restored one
/// put in its place; index.db goes, to be rebuilt from the files unless the backup brought its own (choice 6). On any
/// failure the catalog in use is put back as it was and the outcome says why: a restore never leaves the user without
/// a catalog.
/// </summary>
public static class PendingRestore
{
    const string MarkerName = "restore.pending";
    const string PreviousName = "previous-catalog.db";

    // SQLite's companions: write-ahead log, shared memory, rollback journal.
    static readonly string[] Companions = ["-wal", "-shm", "-journal"];

    /// <summary>Where a backup being restored is unpacked and readied, beside catalog.db so the swap is a move on one disk.</summary>
    public static string StagingFolder(AppPaths paths) => Path.Combine(paths.Root, "restore");

    static string Marker(AppPaths paths) => Path.Combine(paths.Root, MarkerName);

    internal static void Request(AppPaths paths, RestoreRequest request) =>
        File.WriteAllText(Marker(paths), JsonSerializer.Serialize(request, BackupManifest.Json));

    /// <summary>Forgets a staged restore and its files.</summary>
    internal static void Cancel(AppPaths paths)
    {
        if (File.Exists(Marker(paths))) File.Delete(Marker(paths));
        var staging = StagingFolder(paths);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    }

    /// <summary>
    /// Swaps in a staged restore. Returns null when none was staged, after clearing away a backup that was only
    /// previewed. Call before anything opens catalog.db or index.db.
    /// </summary>
    public static async Task<RestoreOutcome?> ApplyIfRequestedAsync(AppPaths paths, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var staging = StagingFolder(paths);
        if (!File.Exists(Marker(paths)))
        {
            if (Directory.Exists(staging)) TryCancel(paths);
            return null;
        }

        RestoreRequest? request = null;
        string? kept = null;
        var previous = Path.Combine(staging, PreviousName);
        var movedAside = false;
        try
        {
            request = JsonSerializer.Deserialize<RestoreRequest>(await File.ReadAllTextAsync(Marker(paths), ct), BackupManifest.Json)
                ?? throw new InvalidDataException("The restore's note is empty.");
            var restored = new CatalogFile(Path.Combine(staging, "catalog.db"));
            if (await restored.ReadMigrationsAsync(ct) is null) throw new InvalidDataException("The backup's catalog can't be read any more.");

            // index.db first: if it can't go, nothing has changed yet. It describes the catalog in use, not the restored one.
            DeleteDatabase(paths.IndexDatabase);
            if (File.Exists(paths.CatalogDatabase))
            {
                kept = await new CatalogDatabase(paths.CatalogDatabase, paths.Backups, clock).BackupAsync("before-restore", ct);
                // The copy's connection closes the file, which folds its write-ahead log in, before it is moved.
                SqliteConnection.ClearAllPools();
                MoveDatabase(paths.CatalogDatabase, previous);
                movedAside = true;
            }
            File.Move(restored.FilePath, paths.CatalogDatabase);
            await new CatalogFile(paths.CatalogDatabase).UseWalAsync(ct);
            // The backup's own index, when it brought one; otherwise the books are read again from their files.
            var index = Path.Combine(staging, "index.db");
            if (request.IncludesIndex && File.Exists(index)) File.Move(index, paths.IndexDatabase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException or InvalidDataException or NotSupportedException)
        {
            SqliteConnection.ClearAllPools();
            if (movedAside)
            {
                DeleteDatabase(paths.CatalogDatabase);
                MoveDatabase(previous, paths.CatalogDatabase);
            }
            TryCancel(paths);
            return new RestoreOutcome(request, ex.Message, kept);
        }
        TryCancel(paths);
        return new RestoreOutcome(request, null, kept);
    }

    static void MoveDatabase(string from, string to)
    {
        File.Move(from, to);
        foreach (var suffix in Companions)
            if (File.Exists(from + suffix)) File.Move(from + suffix, to + suffix, overwrite: true);
    }

    static void DeleteDatabase(string path)
    {
        foreach (var file in Companions.Select(s => path + s).Prepend(path))
            if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Left-over staging is only tidied; one that can't be deleted now is tried again next time.</summary>
    static void TryCancel(AppPaths paths)
    {
        try
        {
            Cancel(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

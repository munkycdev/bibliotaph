using System.Security.Cryptography;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Libraries;

#pragma warning disable RS0030 // Backups and working copies of the server's own databases; books are never local files here.

/// <summary>A backup read for restoring, waiting for the user's choice, with which of its folders this library has.</summary>
public sealed record RestoreChoice(string Token, RestorePreview Preview, IReadOnlySet<long> Found);

/// <summary>The one backup a library has read for restoring; a newer one replaces it, as the Windows app's preview does.</summary>
public sealed class HeldRestore
{
    RestoreChoice? _held;

    public RestoreChoice Keep(RestorePreview preview, IReadOnlySet<long> found)
    {
        var choice = new RestoreChoice(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), preview, found);
        _held = choice;
        return choice;
    }

    public RestoreChoice? Find(string token) => _held is { } held && held.Token == token ? held : null;

    public void Forget() => _held = null;
}

/// <summary>
/// The Windows app's weekly backups and Restore, for the web library. The server's disk is only a working copy, so the
/// backups are kept in <see cref="IBackupStore"/>. A restore swaps the backup's catalog in while the library is closed,
/// as the Windows app does at its next start, but by copying it into catalog.db page by page through SQLite: the file
/// Litestream replicates stays the same file, and the restore reaches the replica as one more change to it. index.db is
/// emptied the same way, or given the backup's own, since it describes the catalog it was made with; the books are read
/// again from their files.
/// </summary>
public sealed class WebBackups(UserLibraries libraries, IBackupStore store, ServerPaths paths, TimeProvider clock, ILogger<WebBackups> log)
{
    /// <summary>
    /// The weekly backup, made the first time the library is open in a week (weeks start on Monday, UTC) unless there is
    /// nothing to keep yet. Then only the last four weekly backups stay; the one made before a restore is never deleted.
    /// Null when none was due.
    /// </summary>
    public async Task<KeptBackup?> BackUpWeeklyIfDueAsync(UserLibrary library, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if ((await store.ListAsync(library.UserId, ct)).FirstOrDefault(b => b.Automatic) is { } last && WeekOf(last.Created) >= WeekOf(now)) return null;
        if ((await new CatalogFile(library.Catalog.FilePath).GetCountsAsync(ct)).IsEmpty) return null;

        var made = await KeepAsync(library, BackupNames.Weekly, now, ct);
        log.LogInformation("Made the weekly backup {Name} of {User} ({Bytes} bytes)", made.Name, library.UserId, made.Bytes);
        foreach (var old in (await store.ListAsync(library.UserId, ct)).Where(b => b.Automatic).Skip(BackupService.KeptAutomatic))
        {
            await store.DeleteAsync(library.UserId, old.Name, ct);
            log.LogInformation("Deleted the old weekly backup {Name} of {User}", old.Name, library.UserId);
        }
        return made;
    }

    static DateOnly WeekOf(DateTimeOffset when)
    {
        var day = DateOnly.FromDateTime(when.UtcDateTime);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    /// <summary>The library's backup, the same ZIP the Windows app makes, kept under <paramref name="kind"/>'s name.</summary>
    async Task<KeptBackup> KeepAsync(UserLibrary library, string kind, DateTimeOffset now, CancellationToken ct)
    {
        await using var zip = await library.Get<BackupService>().BackUpForDownloadAsync(ct);
        var name = BackupNames.For(kind, now);
        await store.UploadAsync(library.UserId, name, zip, ct);
        return new KeptBackup(name, now, zip.Length);
    }

    /// <summary>
    /// Reads a backup for restoring, from <paramref name="zip"/> (an upload, or a kept backup's bytes), as the Windows
    /// app's preview does, and holds it for <see cref="RestoreAsync"/>. A folder counts as found when it is in storage
    /// this library is connected to; a Windows PC's folders never are, and stay offline.
    /// </summary>
    public async Task<(RestoreChoice? Choice, string? Problem)> PreviewAsync(UserLibrary library, Stream zip, CancellationToken ct)
    {
        var backups = library.Get<BackupService>();
        Directory.CreateDirectory(backups.BackupsFolder);
        var file = Path.Combine(backups.BackupsFolder, $"restore-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var copy = File.Create(file)) await zip.CopyToAsync(copy, ct);
            var preview = await backups.PreviewRestoreAsync(file, ct);
            if (!preview.CanRestore) return (null, preview.Problem);
            var drives = library.Drives;
            var found = preview.Folders.Where(f => StoragePath.TryParse(f.Path, out var at) && drives.Find(at.Provider, at.Account) is not null)
                .Select(f => f.Id).ToHashSet();
            return (library.Get<HeldRestore>().Keep(preview, found), null);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Restores the backup <see cref="PreviewAsync"/> held: keeps the catalog as it is now as a before-restore backup,
    /// readies the backup's catalog as the Windows app does (folders found here online, the rest offline), then closes
    /// the library and copies it in, with an empty index.db or the backup's own, and opens the library again, which
    /// starts reading the books. Returns why not, or null when it's done.
    /// </summary>
    public async Task<string?> RestoreAsync(string userId, string token, CancellationToken ct)
    {
        userId = UserIds.Require(userId);
        Directory.CreateDirectory(paths.Staging);
        // Not *.db, so Litestream's watcher ignores them.
        var catalog = Path.Combine(paths.Staging, $"{Guid.NewGuid():N}.restoring");
        var index = Path.Combine(paths.Staging, $"{Guid.NewGuid():N}.index");
        try
        {
            await using (var lease = await libraries.OpenAsync(userId, ct))
            {
                var library = lease.Library;
                var held = library.Get<HeldRestore>();
                if (held.Find(token) is not { } choice) return "That backup isn't ready any more. Choose it again.";
                var backups = library.Get<BackupService>();
                var preview = choice.Preview with { Folders = [.. choice.Preview.Folders.Select(f => f with { Found = choice.Found.Contains(f.Id) })] };
                if (await backups.StageRestoreAsync(preview, new Dictionary<long, string?>(), ct) is { } problem) return problem;

                var before = await KeepAsync(library, BackupNames.BeforeRestore, clock.GetUtcNow(), ct);
                log.LogInformation("Kept {Name} before restoring a backup for {User}", before.Name, userId);
                File.Copy(backups.StagedCatalog, catalog);
                if (backups.StagedIndex is { } staged) File.Copy(staged, index);
                backups.DiscardStaging();
                held.Forget();
            }
            if (!File.Exists(index)) await EmptyIndexAsync(index, ct);

            await libraries.ReplaceAsync(userId, async files =>
            {
                await CopyIntoAsync(catalog, files.CatalogDatabase, ct);
                await CopyIntoAsync(index, files.IndexDatabase, ct);
            });
            log.LogInformation("Restored a backup for {User}", userId);
        }
        finally
        {
            File.Delete(catalog);
            File.Delete(index);
        }
        // Opened again at once, so the books start being read and the next page finds it ready. Books owned elsewhere
        // have no file to read, so the catalog is projected into the new index.db as the Windows app does at its start.
        await using (var lease = await libraries.OpenAsync(userId, ct))
            await lease.Library.Get<MetadataProjector>().ProjectAllAsync(ct);
        return null;
    }

    static async Task EmptyIndexAsync(string path, CancellationToken ct)
    {
        var index = IndexDatabase.ForFile(path);
        await index.InitializeAsync(ct);
        index.ClearPools();
    }

    /// <summary>
    /// Makes <paramref name="target"/> hold what <paramref name="source"/> holds, written through SQLite's backup API
    /// into the live file, so it stays the same database to Litestream, in write-ahead-log mode.
    /// </summary>
    static async Task CopyIntoAsync(string source, string target, CancellationToken ct)
    {
        await using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await from.OpenAsync(ct);
        await to.OpenAsync(ct);
        from.BackupDatabase(to);
        await using var wal = to.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode = WAL";
        await wal.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The weekly backups of the libraries in use: checked a minute after the server starts, then hourly.</summary>
    public sealed class Weekly(UserLibraries libraries, WebBackups backups, IOptions<ServerOptions> options, TimeProvider clock, ILogger<Weekly> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(options.Value.WeeklyBackupFirst, clock, stoppingToken);
                using var timer = new PeriodicTimer(options.Value.WeeklyBackupEvery, clock);
                do
                {
                    foreach (var userId in libraries.OpenUsers)
                    {
                        await using var lease = await libraries.LeaseIfOpenAsync(userId);
                        if (lease is null) continue;
                        try
                        {
                            await backups.BackUpWeeklyIfDueAsync(lease.Library, stoppingToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Tried again within the hour.
                            log.LogWarning(ex, "The weekly backup of {User} failed", userId);
                        }
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // The server is stopping.
            }
        }
    }
}
#pragma warning restore RS0030

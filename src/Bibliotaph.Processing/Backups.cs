using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// What a backup says about itself (slice 4j plan, choice 1), as manifest.json beside catalog.db in the ZIP: which
/// Bibliotaph and schema made it, when, and each library folder's id, path and disk, so a restore can say which are
/// found and remap the rest.
/// </summary>
public sealed record BackupManifest
{
    public const string FileName = "manifest.json";
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    /// <summary>The Bibliotaph that made it, as "0.4.0+4578ab9…".</summary>
    public required string App { get; init; }

    /// <summary>The catalog's latest migration: its schema.</summary>
    public string? Schema { get; init; }

    public DateTime CreatedUtc { get; init; }

    /// <summary>Made by the weekly backup rather than Back up now.</summary>
    public bool Automatic { get; init; }

    /// <summary>The ZIP holds index.db too, so a restore needn't read every book again.</summary>
    public bool IncludesIndex { get; init; }

    public IReadOnlyList<BackupRoot> Roots { get; init; } = [];

    public CatalogCounts? Counts { get; init; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>A backup on disk.</summary>
public sealed record BackupFile(string Path, DateTime CreatedUtc, long SizeBytes);

/// <summary>A library folder of a backup being restored, and whether it is where the backup says (slice 4j plan, choice 4).</summary>
public sealed record RestoreFolder(long Id, string Path, bool Found, int Files);

/// <summary>
/// A backup read for restoring, staged beside catalog.db: what it holds and its library folders, or why it can't be
/// restored (<see cref="Problem"/>). Nothing replaces catalog.db until <see cref="BackupService.StageRestoreAsync"/>
/// and a restart.
/// </summary>
public sealed record RestorePreview(string BackupPath, BackupManifest? Manifest, CatalogCounts? Counts, IReadOnlyList<RestoreFolder> Folders, string? Problem = null)
{
    public bool CanRestore => Problem is null;

    public bool IncludesIndex => Manifest?.IncludesIndex == true;

    internal static RestorePreview Refused(string backup, string problem) => new(backup, null, null, [], problem);
}

/// <summary>
/// Backups of the user's work (slice 4j, A15). A backup is a ZIP of a consistent copy of catalog.db, taken with VACUUM
/// INTO while the app runs, and a manifest; index.db goes in too only when the user asks, since it can be rebuilt from
/// the files. Passwords and AI keys stay in Windows Credential Manager and are never in one (choice 3). Once a week the
/// first start makes one in the backups folder, keeping the last four of those (choice 2). Restoring reads a backup
/// into a staging folder, upgrades it as startup would (choice 5), points its folders at their places on this
/// computer, and leaves it for <see cref="PendingRestore"/> to swap in at the next start, before anything opens the
/// databases (choice 4).
/// </summary>
public sealed class BackupService(AppPaths paths, CatalogDatabase catalog, IndexDatabase index, SettingsStore settings,
    TimeProvider? clock = null, ILogger<BackupService>? log = null) : IDisposable
{
    /// <summary>Automatic backups are named for this, then their UTC time: only these are ever deleted, and only by the weekly backup.</summary>
    public const string AutomaticPrefix = "automatic-backup-";

    /// <summary>How many automatic backups are kept (choice 2).</summary>
    public const int KeptAutomatic = 4;

    const string StampFormat = "yyyyMMdd-HHmmss";
    const string CatalogEntry = "catalog.db";
    const string IndexEntry = "index.db";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<BackupService>.Instance;
    /// <summary>One backup or restore step at a time: the weekly backup can start while the user is in Settings > Backup.</summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>The Bibliotaph doing the work, as the manifest and the JSON export record it.</summary>
    public static string AppVersion { get; } =
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public string BackupsFolder => paths.Backups;

    /// <summary>Whether backups take index.db too: one setting for Back up now and the weekly backup alike.</summary>
    public async Task<bool> GetIncludeIndexAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.BackupIncludeIndex, ct) == bool.TrueString;

    public Task SetIncludeIndexAsync(bool include, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.BackupIncludeIndex, include.ToString(), ct);

    /// <summary>Back up now (choice 2): a backup saved where the user chose, which nothing ever deletes.</summary>
    public async Task<BackupFile> BackUpAsync(string target, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var file = await WriteAsync(Path.GetFullPath(target), automatic: false, await GetIncludeIndexAsync(ct), ct);
            _log.LogInformation("Backed up the catalog to {Path} ({Bytes} bytes)", file.Path, file.SizeBytes);
            return file;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The automatic backups in the backups folder, newest first.</summary>
    public IReadOnlyList<BackupFile> ListAutomatic()
    {
        if (!Directory.Exists(paths.Backups)) return [];
        var found = new List<BackupFile>();
        foreach (var file in new DirectoryInfo(paths.Backups).EnumerateFiles(AutomaticPrefix + "*.zip"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file.Name)[AutomaticPrefix.Length..];
            if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
                found.Add(new BackupFile(file.FullName, created, file.Length));
        }
        return [.. found.OrderByDescending(f => f.CreatedUtc)];
    }

    /// <summary>
    /// The weekly backup (choice 2), at the first start of each week (weeks start on Monday, in local time): made unless
    /// one was already made this week, or there is nothing to keep yet. Then only the last four automatic backups stay;
    /// a backup made before a migration, or saved with Back up now, is never deleted. Null when none was due.
    /// </summary>
    public async Task<BackupFile?> BackUpWeeklyIfDueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            if (ListAutomatic() is [var last, ..] && WeekOf(last.CreatedUtc) >= WeekOf(now.UtcDateTime)) return null;
            if ((await new CatalogFile(catalog.FilePath).GetCountsAsync(ct)).IsEmpty) return null;

            var target = Path.Combine(paths.Backups, AutomaticPrefix + now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + ".zip");
            var file = await WriteAsync(target, automatic: true, await GetIncludeIndexAsync(ct), ct);
            _log.LogInformation("Made the weekly backup {Path} ({Bytes} bytes)", file.Path, file.SizeBytes);
            foreach (var old in ListAutomatic().Skip(KeptAutomatic))
            {
                File.Delete(old.Path);
                _log.LogInformation("Deleted the old automatic backup {Path}", old.Path);
            }
            return file;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Monday a time falls in the week of, in local time.</summary>
    DateOnly WeekOf(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _clock.LocalTimeZone);
        return DateOnly.FromDateTime(local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)));
    }

    /// <summary>
    /// Copies catalog.db (and index.db when asked) into a working folder, writes the ZIP beside the target under
    /// another name, then puts it in place, so an interrupted backup never leaves a broken ZIP under the backup's name.
    /// </summary>
    async Task<BackupFile> WriteAsync(string target, bool automatic, bool includeIndex, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Backups);
        var work = Directory.CreateDirectory(Path.Combine(paths.Backups, ".working-" + Guid.NewGuid().ToString("N"))).FullName;
        var partial = target + ".partial";
        try
        {
            var catalogCopy = Path.Combine(work, CatalogEntry);
            await catalog.CopyToAsync(catalogCopy, ct);
            var copy = new CatalogFile(catalogCopy);
            var manifest = new BackupManifest
            {
                App = AppVersion,
                Schema = await copy.ReadMigrationsAsync(ct) is [.., var latest] ? latest : null,
                CreatedUtc = _clock.GetUtcNow().UtcDateTime,
                Automatic = automatic,
                IncludesIndex = includeIndex,
                Roots = await copy.GetRootsAsync(ct),
                Counts = await copy.GetCountsAsync(ct),
            };

            string? indexCopy = null;
            if (includeIndex && index.FilePath is not null)
            {
                indexCopy = Path.Combine(work, IndexEntry);
                await using var connection = index.OpenRead();
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $target";
                command.Parameters.AddWithValue("$target", indexCopy);
                await command.ExecuteNonQueryAsync(ct);
            }

            await Task.Run(() =>
            {
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    using (var entry = zip.CreateEntry(BackupManifest.FileName).Open())
                        JsonSerializer.Serialize(entry, manifest, BackupManifest.Json);
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(catalogCopy, CatalogEntry, CompressionLevel.Optimal);
                    ct.ThrowIfCancellationRequested();
                    if (indexCopy is not null) zip.CreateEntryFromFile(indexCopy, IndexEntry, CompressionLevel.Optimal);
                }
                File.Move(partial, target, overwrite: true);
            }, ct);
            return new BackupFile(target, manifest.CreatedUtc, new FileInfo(target).Length);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// Reads a backup for restoring (choice 4): unpacks it into the staging folder, checks its catalog is whole and
    /// isn't from a newer Bibliotaph (choice 5, "Update Bibliotaph first"), upgrades it as startup would, and lists its
    /// library folders with whether each is where it was. Any earlier preview is discarded first.
    /// </summary>
    public async Task<RestorePreview> PreviewRestoreAsync(string backup, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var staging = PendingRestore.StagingFolder(paths);
            DiscardStaging();
            Directory.CreateDirectory(staging);
            var preview = await ReadAsync(Path.GetFullPath(backup), staging, ct);
            if (!preview.CanRestore) DiscardStaging();
            _log.LogInformation("Read backup {Path} for restoring: {Outcome}", backup, preview.Problem ?? "can be restored");
            return preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<RestorePreview> ReadAsync(string backup, string staging, CancellationToken ct)
    {
        BackupManifest? manifest;
        try
        {
            manifest = await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(backup);
                if (zip.GetEntry(CatalogEntry) is not { } catalogEntry || zip.GetEntry(BackupManifest.FileName) is not { } manifestEntry) return null;
                BackupManifest? read;
                using (var stream = manifestEntry.Open()) read = JsonSerializer.Deserialize<BackupManifest>(stream, BackupManifest.Json);
                if (read is null) return null;
                catalogEntry.ExtractToFile(Path.Combine(staging, CatalogEntry));
                if (read.IncludesIndex && zip.GetEntry(IndexEntry) is { } indexEntry) indexEntry.ExtractToFile(Path.Combine(staging, IndexEntry));
                return read;
            }, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return RestorePreview.Refused(backup, "This file isn't a Bibliotaph backup, or it is damaged.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RestorePreview.Refused(backup, $"The backup can't be read: {ex.Message}");
        }
        if (manifest is null) return RestorePreview.Refused(backup, "This file isn't a Bibliotaph backup: it has no catalog and manifest.");

        var restored = new CatalogFile(Path.Combine(staging, CatalogEntry));
        if (await restored.ReadMigrationsAsync(ct) is not { } migrations)
            return RestorePreview.Refused(backup, "The catalog in this backup can't be read. It may be damaged.");
        if (migrations.Except(CatalogFile.KnownMigrations, StringComparer.Ordinal).Any())
            return RestorePreview.Refused(backup, "This backup was made by a newer Bibliotaph. Update Bibliotaph first, then restore it.");
        await restored.MigrateAsync(ct);

        var roots = await restored.GetRootsAsync(ct);
        var folders = await Task.Run(() => roots.Select(r => new RestoreFolder(r.Id, r.Path, Directory.Exists(r.Path), r.Files)).ToList(), ct);
        return new RestorePreview(backup, manifest, await restored.GetCountsAsync(ct), folders);
    }

    /// <summary>
    /// Whether <paramref name="path"/> can be a restored folder's new place, as Point to its new place checks a folder
    /// (<see cref="IndexingService.RelocateRootAsync"/>): it exists, and isn't another of the backup's folders, inside
    /// one or holding one, wherever those are going.
    /// </summary>
    public static RootRelocation CheckPlace(RestorePreview preview, IReadOnlyDictionary<long, string?> places, long rootId, string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) return RootRelocation.NotFound;
        var others = preview.Folders.Where(f => f.Id != rootId).Select(f => places.GetValueOrDefault(f.Id) ?? f.Path)
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));
        return others.Any(other => SourceScanner.IsUnder(full, other) || SourceScanner.IsUnder(other, full)) ? RootRelocation.Overlaps : RootRelocation.Relocated;
    }

    /// <summary>
    /// Readies the previewed backup to replace catalog.db at the next start (choice 4): each folder pointed somewhere
    /// takes that place, one not found and not pointed anywhere stays offline to be fixed later, and the swap is left
    /// for <see cref="PendingRestore"/>. The caller then restarts the app. Returns why not, or null when it is ready.
    /// </summary>
    public async Task<string?> StageRestoreAsync(RestorePreview preview, IReadOnlyDictionary<long, string?> places, CancellationToken ct = default)
    {
        if (!preview.CanRestore || preview.Manifest is not { } manifest) return preview.Problem ?? "This backup can't be restored.";
        foreach (var (rootId, place) in places)
            if (place is not null && CheckPlace(preview, places, rootId, place) != RootRelocation.Relocated)
                return $"{place} can't be a library folder's new place: it is missing, or is another folder, inside one or holds one.";

        await _gate.WaitAsync(ct);
        try
        {
            var restored = new CatalogFile(Path.Combine(PendingRestore.StagingFolder(paths), CatalogEntry));
            if (await restored.ReadMigrationsAsync(ct) is null) return "The backup isn't ready any more. Choose it again.";
            var found = preview.Folders.Where(f => f.Found).Select(f => f.Id).ToHashSet();
            if (!await restored.PrepareRestoredAsync(places, found, ct)) return "Two library folders can't have the same place.";
            await restored.SealAsync(ct);
            var offline = preview.Folders.Count(f => !f.Found && places.GetValueOrDefault(f.Id) is null);
            PendingRestore.Request(paths, new RestoreRequest(Path.GetFileName(preview.BackupPath), manifest.CreatedUtc, manifest.IncludesIndex, offline));
            _log.LogInformation("Restore of {Path} staged: {Moved} folders pointed to new places, {Offline} left offline; it is swapped in at the next start",
                preview.BackupPath, places.Count(p => p.Value is not null), offline);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a previewed backup without restoring it, and a restore staged but not yet swapped in.</summary>
    public void DiscardStaging() => PendingRestore.Cancel(paths);
}

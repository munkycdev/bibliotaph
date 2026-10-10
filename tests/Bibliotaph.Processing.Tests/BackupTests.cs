using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 4j: backups, weekly backups and what they keep, refusing what can't be restored, the swap at startup, and CSV.</summary>
public sealed class BackupTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-backup-").FullName;
    readonly Clock _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)); // a Monday
    AppPaths _paths = null!;
    CatalogDatabase _catalog = null!;
    IndexDatabase _index = null!;
    BackupService _backups = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clock the test sets, in UTC as local time too, so weeks start at Monday midnight UTC.</summary>
    sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    public async ValueTask InitializeAsync()
    {
        _paths = new AppPaths(Path.Combine(_dir, "data"));
        foreach (var directory in _paths.Directories) Directory.CreateDirectory(directory);
        _catalog = new CatalogDatabase(_paths.CatalogDatabase, _paths.Backups, _clock);
        await _catalog.MigrateAsync(Ct);
        _index = IndexDatabase.ForFile(_paths.IndexDatabase);
        await _index.InitializeAsync(Ct);
        _backups = new BackupService(_paths, _catalog, _index, new SettingsStore(new Factory(_catalog)), _clock);
    }

    public ValueTask DisposeAsync()
    {
        _backups.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    async Task AddBookAsync(string title = "Haunted Inn")
    {
        await using var db = _catalog.CreateContext();
        var document = db.Documents.Add(new Document { ContentHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(title))), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
        var entry = db.Entries.Add(new Entry { Kind = EntryKind.Whole, CreatedUtc = DateTime.UtcNow }).Entity;
        await db.SaveChangesAsync(Ct);
        db.EntrySources.Add(new EntrySource { EntryId = entry.Id, DocumentId = document.Id, IsCurrent = true });
        db.Notes.Add(new Note { EntryId = entry.Id, Text = $"Notes on {title}", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    static async Task<int> NotesAsync(string path)
    {
        await using var db = new CatalogFile(path).CreateDbContext();
        return await db.Notes.CountAsync(Ct);
    }

    [Fact]
    public async Task A_weekly_backup_is_made_at_the_first_start_of_each_week_and_only_the_last_four_are_kept()
    {
        // Nothing to keep yet: no backup.
        Assert.Null(await _backups.BackUpWeeklyIfDueAsync(Ct));
        await AddBookAsync();
        // Backups the weekly backup must never delete: one made before a migration, one the user saved there.
        var migration = await _catalog.BackupAsync("before-20261010064418_CatalogNotes", Ct);
        var saved = Path.Combine(_paths.Backups, "Bibliotaph backup 2026-09-01.zip");
        await _backups.BackUpAsync(saved, Ct);

        var made = new List<string>();
        for (var week = 0; week < 6; week++)
        {
            _clock.Now = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero).AddDays(7 * week);
            made.Add((await _backups.BackUpWeeklyIfDueAsync(Ct))?.Path ?? throw new InvalidOperationException($"No backup in week {week}."));
            // Later starts that week, up to Sunday night, make none.
            _clock.Now = _clock.Now.AddDays(2);
            Assert.Null(await _backups.BackUpWeeklyIfDueAsync(Ct));
            _clock.Now = _clock.Now.AddDays(4).AddHours(14.9);
            Assert.Null(await _backups.BackUpWeeklyIfDueAsync(Ct));
        }

        Assert.Equal(made.TakeLast(BackupService.KeptAutomatic).Reverse(), _backups.ListAutomatic().Select(b => b.Path));
        Assert.All(made.Take(2), path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(migration));
        Assert.True(File.Exists(saved));
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc), _backups.ListAutomatic()[0].CreatedUtc);
    }

    [Fact]
    public async Task A_backup_holds_a_copy_of_the_catalog_and_a_manifest_and_the_index_only_when_asked()
    {
        await AddBookAsync();
        await using (var db = _catalog.CreateContext())
        {
            db.SourceRoots.Add(new SourceRoot { Path = Path.Combine(_dir, "RPG Library"), VolumeSerial = "1234ABCD", AddedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var target = Path.Combine(_dir, "Saved elsewhere", "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var file = await _backups.BackUpAsync(target, Ct);
        Assert.Equal(target, file.Path);
        using (var zip = ZipFile.OpenRead(target))
        {
            Assert.Equal(["catalog.db", "manifest.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            using var stream = zip.GetEntry("manifest.json")!.Open();
            var manifest = JsonDocument.Parse(stream).RootElement;
            Assert.Equal(CatalogFile.KnownMigrations[^1], manifest.GetProperty("schema").GetString());
            Assert.False(manifest.GetProperty("includesIndex").GetBoolean());
            var root = Assert.Single(manifest.GetProperty("roots").EnumerateArray());
            Assert.Equal(Path.Combine(_dir, "RPG Library"), root.GetProperty("path").GetString());
            Assert.Equal("1234ABCD", root.GetProperty("volumeSerial").GetString());
            Assert.Equal(1, manifest.GetProperty("counts").GetProperty("books").GetInt32());
            Assert.Equal(1, manifest.GetProperty("counts").GetProperty("notes").GetInt32());
        }
        Assert.Equal([target], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!));
        Assert.Empty(Directory.EnumerateDirectories(_paths.Backups));

        await _backups.SetIncludeIndexAsync(true, Ct);
        await _backups.BackUpAsync(target, Ct);
        using (var zip = ZipFile.OpenRead(target))
            Assert.Contains(zip.Entries, e => e.FullName == "index.db");
    }

    [Fact]
    public async Task A_backup_from_a_newer_Bibliotaph_is_refused_and_nothing_is_staged()
    {
        await AddBookAsync();
        var backup = Path.Combine(_dir, "newer.zip");
        await _backups.BackUpAsync(backup, Ct);
        // As a later version would have left it: one migration this version doesn't know.
        await RewriteCatalogAsync(backup, "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '99.0.0')");

        var preview = await _backups.PreviewRestoreAsync(backup, Ct);

        Assert.False(preview.CanRestore);
        Assert.Contains("Update Bibliotaph first", preview.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(PendingRestore.StagingFolder(_paths)));
        Assert.NotNull(await _backups.StageRestoreAsync(preview, new Dictionary<long, string?>(), Ct));
        Assert.Null(await PendingRestore.ApplyIfRequestedAsync(_paths, _clock, Ct));
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_refused()
    {
        var notZip = Path.Combine(_dir, "notes.zip");
        await File.WriteAllTextAsync(notZip, "not a zip", Ct);
        Assert.Contains("isn't a Bibliotaph backup", (await _backups.PreviewRestoreAsync(notZip, Ct)).Problem, StringComparison.Ordinal);

        var otherZip = Path.Combine(_dir, "other.zip");
        using (var zip = ZipFile.Open(otherZip, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Assert.Contains("isn't a Bibliotaph backup", (await _backups.PreviewRestoreAsync(otherZip, Ct)).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_backup_is_upgraded_as_it_is_restored()
    {
        // A catalog as an earlier Bibliotaph left it, before collections existed, zipped as that version would have.
        var old = Path.Combine(_dir, "old", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        var earlier = CatalogFile.KnownMigrations.First(m => m.EndsWith("_CatalogFavorites", StringComparison.Ordinal));
        await using (var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite($"Data Source={old};Pooling=False").Options))
            await db.GetService<IMigrator>().MigrateAsync(earlier, Ct);
        var backup = Path.Combine(_dir, "old.zip");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(old, "catalog.db");
            using var manifest = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await manifest.WriteAsync($$"""{ "format": 1, "app": "0.3.0", "schema": "{{earlier}}", "createdUtc": "2026-09-01T10:00:00Z", "roots": [] }""");
        }
        SqliteConnection.ClearAllPools();

        var preview = await _backups.PreviewRestoreAsync(backup, Ct);

        Assert.True(preview.CanRestore, preview.Problem);
        Assert.Equal("0.3.0", preview.Manifest!.App);
        var staged = new CatalogFile(Path.Combine(PendingRestore.StagingFolder(_paths), "catalog.db"));
        Assert.Equal(CatalogFile.KnownMigrations, await staged.ReadMigrationsAsync(Ct));
    }

    [Fact]
    public async Task A_restore_replaces_the_catalog_at_the_next_start_keeping_the_old_one_as_a_backup_and_dropping_the_index()
    {
        await AddBookAsync("Haunted Inn");
        var backup = Path.Combine(_dir, "backup.zip");
        await _backups.BackUpAsync(backup, Ct);
        // Work done after the backup, which the restore takes back to how it was.
        await AddBookAsync("Dragon Lairs");
        Assert.True(File.Exists(_paths.IndexDatabase));

        var preview = await _backups.PreviewRestoreAsync(backup, Ct);
        Assert.True(preview.CanRestore, preview.Problem);
        Assert.Equal(1, preview.Counts!.Books);
        Assert.Null(await _backups.StageRestoreAsync(preview, new Dictionary<long, string?>(), Ct));
        // The app closes here, then starts again.
        SqliteConnection.ClearAllPools();

        var outcome = await PendingRestore.ApplyIfRequestedAsync(_paths, _clock, Ct);

        Assert.True(outcome!.Restored, outcome.Problem);
        Assert.Equal("backup.zip", outcome.Request!.Backup);
        Assert.Equal(1, await NotesAsync(_paths.CatalogDatabase));
        Assert.Equal(2, await NotesAsync(outcome.PreviousCatalogBackup!));
        Assert.Contains("before-restore", outcome.PreviousCatalogBackup, StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.IndexDatabase));
        Assert.False(Directory.Exists(PendingRestore.StagingFolder(_paths)));
        Assert.Null(await PendingRestore.ApplyIfRequestedAsync(_paths, _clock, Ct));
        Assert.Empty((await new CatalogDatabase(_paths.CatalogDatabase, _paths.Backups).MigrateAsync(Ct)).AppliedMigrations);
        await using var c = new SqliteConnection(_catalog.ConnectionString);
        await c.OpenAsync(Ct);
        await using var command = c.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task A_restore_that_fails_at_the_next_start_keeps_the_catalog_in_use_and_says_why()
    {
        await AddBookAsync();
        var backup = Path.Combine(_dir, "backup.zip");
        await _backups.BackUpAsync(backup, Ct);
        await AddBookAsync("Dragon Lairs");
        var preview = await _backups.PreviewRestoreAsync(backup, Ct);
        Assert.Null(await _backups.StageRestoreAsync(preview, new Dictionary<long, string?>(), Ct));
        SqliteConnection.ClearAllPools();
        // Something spoils the staged catalog before the next start.
        await File.WriteAllTextAsync(Path.Combine(PendingRestore.StagingFolder(_paths), "catalog.db"), "spoiled", Ct);

        var outcome = await PendingRestore.ApplyIfRequestedAsync(_paths, _clock, Ct);

        Assert.False(outcome!.Restored);
        Assert.Equal(2, await NotesAsync(_paths.CatalogDatabase));
        Assert.False(Directory.Exists(PendingRestore.StagingFolder(_paths)));
        Assert.Null(await PendingRestore.ApplyIfRequestedAsync(_paths, _clock, Ct));
    }

    [Fact]
    public async Task A_restored_folder_can_be_pointed_only_at_a_folder_that_exists_and_is_not_another_ones()
    {
        var first = Directory.CreateDirectory(Path.Combine(_dir, "Books")).FullName;
        var second = Path.Combine(_dir, "Old disk", "Maps");
        var moved = Directory.CreateDirectory(Path.Combine(_dir, "New disk", "Maps")).FullName;
        var preview = new RestorePreview("backup.zip", null, null, [new RestoreFolder(1, first, true, 10), new RestoreFolder(2, second, false, 5)]);
        var places = new Dictionary<long, string?>();

        Assert.Equal(RootRelocation.Relocated, BackupService.CheckPlace(preview, places, 2, moved));
        Assert.Equal(RootRelocation.NotFound, BackupService.CheckPlace(preview, places, 2, second));
        Assert.Equal(RootRelocation.Overlaps, BackupService.CheckPlace(preview, places, 2, Directory.CreateDirectory(Path.Combine(first, "Maps")).FullName));
        Assert.Equal(RootRelocation.Overlaps, BackupService.CheckPlace(preview, places, 2, _dir));
        places[1] = moved;
        Assert.Equal(RootRelocation.Overlaps, BackupService.CheckPlace(preview, places, 2, moved));
    }

    [Fact]
    public async Task A_CSV_is_UTF8_with_a_byte_order_mark_and_quotes_fields_as_RFC_4180_says()
    {
        Assert.Equal("plain", Csv.Field("plain"));
        Assert.Equal("", Csv.Field(null));
        Assert.Equal("\"Dungeons, Dragons\"", Csv.Field("Dungeons, Dragons"));
        Assert.Equal("\"The \"\"Red\"\" Hand\"", Csv.Field("The \"Red\" Hand"));
        Assert.Equal("\"two\r\nlines\"", Csv.Field("two\r\nlines"));
        Assert.Equal("\"line\nfeed\"", Csv.Field("line\nfeed"));
        Assert.Equal("Levels 1–5", Csv.Field("Levels 1–5"));

        var path = Path.Combine(_dir, "books.csv");
        await File.WriteAllTextAsync(path, "an older export", Ct);
        await Csv.WriteAsync(path, ["Title", "Tags"], [["Tomb, of \"Horrors\"", "Friday; Café"], ["Plain", null]], Ct);

        var bytes = await File.ReadAllBytesAsync(path, Ct);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Title,Tags\r\n\"Tomb, of \"\"Horrors\"\"\",Friday; Café\r\nPlain,\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal([path], Directory.EnumerateFiles(_dir, "books.csv*"));
    }

    /// <summary>Runs SQL on the catalog inside a backup, as another version of Bibliotaph might have left it.</summary>
    async Task RewriteCatalogAsync(string backup, string sql)
    {
        var work = Directory.CreateDirectory(Path.Combine(_dir, "rewrite")).FullName;
        var copy = Path.Combine(work, "catalog.db");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("catalog.db")!;
            entry.ExtractToFile(copy);
            await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                await c.OpenAsync(Ct);
                await using var command = c.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(Ct);
            }
            entry.Delete();
            zip.CreateEntryFromFile(copy, "catalog.db");
        }
        Directory.Delete(work, recursive: true);
    }
}

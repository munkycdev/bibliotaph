using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Bibliotaph.Catalog.Tests;

public sealed class CatalogDatabaseTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-catalog-").FullName;

    string DbPath => Path.Combine(_dir, "catalog.db");
    string Backups => Path.Combine(_dir, "backups");

    CatalogDatabase Database() => new(DbPath, Backups, new FixedClock(new DateTimeOffset(2026, 10, 8, 17, 30, 0, TimeSpan.Zero)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static long Scalar(string connectionString, string sql)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_new_catalog_is_migrated_without_a_backup()
    {
        var result = await Database().MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Null(result.BackupPath);
        Assert.False(Directory.Exists(Backups) && Directory.EnumerateFiles(Backups).Any());
    }

    [Fact]
    public async Task An_existing_catalog_is_copied_before_a_pending_migration_runs()
    {
        // An existing file that predates the pending migration, holding some user work.
        var database = Database();
        using (var c = new SqliteConnection(database.ConnectionString))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "CREATE TABLE user_work (note TEXT); INSERT INTO user_work VALUES ('keep me');";
            command.ExecuteNonQuery();
        }

        var result = await database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result.BackupPath);
        Assert.Equal(Path.Combine(Backups, "catalog-20261008-173000-before-" + result.AppliedMigrations[0] + ".db"), result.BackupPath);
        var backup = new SqliteConnectionStringBuilder { DataSource = result.BackupPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        Assert.Equal(1, Scalar(backup, "SELECT count(*) FROM user_work WHERE note = 'keep me'"));
        Assert.Equal(0, Scalar(backup, "SELECT count(*) FROM sqlite_schema WHERE name = 'document'"));
    }

    [Fact]
    public async Task A_catalog_with_nothing_pending_is_not_copied_again()
    {
        var database = Database();
        await database.MigrateAsync(TestContext.Current.CancellationToken);

        var second = await database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(second.AppliedMigrations);
        Assert.Null(second.BackupPath);
    }

    [Fact]
    public async Task The_catalog_runs_in_WAL_mode()
    {
        var database = Database();
        await database.MigrateAsync(TestContext.Current.CancellationToken);

        using var c = new SqliteConnection(database.ConnectionString);
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task Backups_taken_in_the_same_second_do_not_overwrite_each_other()
    {
        var database = Database();
        await database.MigrateAsync(TestContext.Current.CancellationToken);

        var first = await database.BackupAsync("manual", TestContext.Current.CancellationToken);
        var second = await database.BackupAsync("manual", TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task The_model_matches_the_migrations()
    {
        var database = Database();
        await using var context = database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges(),
            "The EF model has changes with no migration. Run: dotnet ef migrations add <Name> --project src/Bibliotaph.Catalog --output-dir Migrations");
    }

    [Fact]
    public async Task Core_tables_use_snake_case_names()
    {
        var database = Database();
        await database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach (var table in new[] { "source_root", "file_location", "document", "assertion", "page_ref", "setting" })
            Assert.Equal(1, Scalar(database.ConnectionString, $"SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = '{table}'"));
    }

    [Fact]
    public async Task A_slice_1_catalog_migrates_to_slice_2_keeping_its_documents_and_with_a_backup_first()
    {
        var database = Database();
        await using (var context = database.CreateContext())
        {
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync("20261008172504_InitialCreate", TestContext.Current.CancellationToken);
        }
        using (var c = new SqliteConnection(database.ConnectionString))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = $"INSERT INTO document (content_hash, format, protection, created_utc) VALUES ('{new string('b', 64)}', 'pdf', 'None', '2026-10-08 12:00:00')";
            command.ExecuteNonQuery();
        }

        var result = await database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_Slice2Metadata", StringComparison.Ordinal));
        Assert.NotNull(result.BackupPath);
        Assert.Equal(1, Scalar(database.ConnectionString, "SELECT count(*) FROM document"));
        foreach (var table in new[] { "rejection", "vocabulary_term", "vocabulary_alias", "classification_run", "ignored_folder_label" })
            Assert.Equal(1, Scalar(database.ConnectionString, $"SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = '{table}'"));
    }

    [Fact]
    public async Task A_slice_2_catalog_gives_every_document_an_entry_and_moves_the_users_work_onto_it()
    {
        var database = Database();
        await using (var context = database.CreateContext())
        {
            await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync("20261009001922_Slice2Metadata", TestContext.Current.CancellationToken);
        }
        string a = new('a', 64), b = new('b', 64);
        using (var c = new SqliteConnection(database.ConnectionString))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = $"""
                INSERT INTO document (id, content_hash, format, protection, created_utc) VALUES
                    (4, '{a}', 'pdf', 'None', '2026-10-08 12:00:00'), (9, '{b}', 'pdf', 'None', '2026-10-09 12:00:00');
                INSERT INTO assertion (document_id, field, value_json, normalized_value, origin, state, from_sampling, created_utc) VALUES
                    (4, 'title', '"Tomb"', 'tomb', 'FileName', 'Provisional', 0, '2026-10-08 12:00:00'),
                    (4, 'tags', '"Friday game"', 'friday game', 'User', 'Confirmed', 0, '2026-10-08 12:00:00'),
                    (9, 'system', '"dnd"', 'dnd', 'Ai', 'Provisional', 0, '2026-10-09 12:00:00');
                INSERT INTO rejection (document_id, field, normalized_value, created_utc) VALUES (9, 'type', 'adventure', '2026-10-09 12:00:00');
                INSERT INTO classification_run (id, document_id, content_hash, provider, model, prompt_version, schema_version, started_utc)
                    VALUES ('run-1', 9, '{b}', 'ollama', 'model-a', 1, 1, '2026-10-09 12:00:00');
                """;
            command.ExecuteNonQuery();
        }

        var result = await database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.AppliedMigrations, m => m.EndsWith("_CatalogEntries", StringComparison.Ordinal));
        Assert.NotNull(result.BackupPath);
        var cs = database.ConnectionString;
        // Each document is shown by one whole-document entry with the same id, so index.db's ids still match.
        Assert.Equal(2, Scalar(cs, "SELECT count(*) FROM entry WHERE kind = 'Whole' AND parent_entry_id IS NULL AND id IN (4, 9)"));
        Assert.Equal(2, Scalar(cs, "SELECT count(*) FROM entry_source WHERE entry_id = document_id AND is_current = 1 AND first_pdf_page IS NULL"));
        Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM entry WHERE id = 9 AND created_utc = '2026-10-09 12:00:00'"));
        // The user's work is on the entries; proposals remember the copy they came from, the user's own values don't.
        Assert.Equal(2, Scalar(cs, "SELECT count(*) FROM assertion WHERE entry_id = 4"));
        Assert.Equal(1, Scalar(cs, $"SELECT count(*) FROM assertion WHERE entry_id = 4 AND origin = 'FileName' AND content_hash = '{a}'"));
        Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM assertion WHERE entry_id = 4 AND origin = 'User' AND content_hash IS NULL"));
        Assert.Equal(1, Scalar(cs, $"SELECT count(*) FROM assertion WHERE entry_id = 9 AND content_hash = '{b}'"));
        Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM rejection WHERE entry_id = 9"));
        Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM classification_run WHERE entry_id = 9"));
        Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pragma_table_info('assertion') WHERE name = 'document_id'"));
        Assert.Equal(0, Scalar(cs, "SELECT count(*) FROM pragma_foreign_key_check"));

        // A document added after the migration gets the next entry id, never one that is already taken.
        await using var db = database.CreateContext();
        var entry = new Entities.Entry { Kind = Core.EntryKind.Whole, CreatedUtc = DateTime.UtcNow };
        var document = new Entities.Document { ContentHash = new string('c', 64), Format = "pdf", CreatedUtc = DateTime.UtcNow };
        db.EntrySources.Add(new Entities.EntrySource { Entry = entry, Document = document, IsCurrent = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10, entry.Id);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

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

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

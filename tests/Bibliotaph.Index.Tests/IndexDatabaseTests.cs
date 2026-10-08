using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index.Tests;

public sealed class IndexDatabaseTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-index-").FullName;

    string DbPath => Path.Combine(_dir, "index.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static long UserVersion(SqliteConnection c) => c.ExecuteScalar<long>("PRAGMA user_version");

    [Fact]
    public async Task A_new_file_is_created_at_the_current_schema_version_in_WAL_mode()
    {
        var db = IndexDatabase.ForFile(DbPath);

        Assert.Equal(IndexOpenResult.Created, await db.InitializeAsync(TestContext.Current.CancellationToken));

        using var c = db.OpenRead();
        Assert.Equal(IndexSchema.Version, UserVersion(c));
        Assert.Equal("wal", c.ExecuteScalar<string>("PRAGMA journal_mode"));
        var tables = c.Query<string>("SELECT name FROM sqlite_schema WHERE type = 'table'").ToHashSet();
        Assert.Superset(new HashSet<string> { "page", "page_fts", "doc_fts", "stage_status", "job" }, tables);
    }

    [Fact]
    public async Task Reopening_a_current_file_keeps_its_contents()
    {
        var db = IndexDatabase.ForFile(DbPath);
        await db.InitializeAsync(TestContext.Current.CancellationToken);
        using (var c = new SqliteConnection(db.ConnectionString))
            c.Execute("INSERT INTO page (document_id, pdf_page, width_pt, height_pt, text) VALUES (1, 0, 612, 792, 'kept')");

        Assert.Equal(IndexOpenResult.Opened, await db.InitializeAsync(TestContext.Current.CancellationToken));

        using var read = db.OpenRead();
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT count(*) FROM page"));
    }

    [Fact]
    public async Task A_file_with_the_wrong_user_version_is_deleted_and_rebuilt()
    {
        var db = IndexDatabase.ForFile(DbPath);
        await db.InitializeAsync(TestContext.Current.CancellationToken);
        using (var c = new SqliteConnection(db.ConnectionString))
        {
            c.Execute("INSERT INTO page (document_id, pdf_page, width_pt, height_pt, text) VALUES (1, 0, 612, 792, 'stale')");
            c.Execute($"PRAGMA user_version = {IndexSchema.Version + 41}");
        }

        Assert.Equal(IndexOpenResult.Rebuilt, await db.InitializeAsync(TestContext.Current.CancellationToken));

        using var read = db.OpenRead();
        Assert.Equal(IndexSchema.Version, UserVersion(read));
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM page"));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_rebuilt()
    {
        File.WriteAllText(DbPath, "this is not a SQLite database, just some bytes that happen to be in the way");
        var db = IndexDatabase.ForFile(DbPath);

        Assert.Equal(IndexOpenResult.Rebuilt, await db.InitializeAsync(TestContext.Current.CancellationToken));

        using var read = db.OpenRead();
        Assert.Equal(IndexSchema.Version, UserVersion(read));
    }

    [Fact]
    public async Task Read_connections_cannot_write()
    {
        var db = IndexDatabase.ForFile(DbPath);
        await db.InitializeAsync(TestContext.Current.CancellationToken);

        using var read = db.OpenRead();
        Assert.Throws<SqliteException>(() =>
            read.Execute("INSERT INTO page (document_id, pdf_page, width_pt, height_pt) VALUES (1, 0, 1, 1)"));
    }

    [Fact]
    public void The_embedded_schema_declares_the_version_the_code_expects() =>
        Assert.Contains($"PRAGMA user_version = {IndexSchema.Version};", IndexSchema.Script, StringComparison.Ordinal);
}

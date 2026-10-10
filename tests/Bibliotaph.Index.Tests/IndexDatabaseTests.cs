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
        Assert.Superset(new HashSet<string> { "page", "page_fts", "entry_doc", "entry_fts", "stage_status", "job" }, tables);
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
    public async Task A_slice_1_file_is_upgraded_in_place_keeping_pages_ocr_and_jobs()
    {
        using (var c = new SqliteConnection(IndexDatabase.ForFile(DbPath).ConnectionString))
        {
            c.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "index-v2.sql")));
            c.Execute("""
                INSERT INTO doc (document_id, content_hash, format, display_title, meta_title, folder_hint, added_utc)
                VALUES (7, 'h7', 'pdf', 'Tomb Final', 'Tomb of Horrors', 'Classics', '2026-10-08T12:00:00Z');
                INSERT INTO page (document_id, pdf_page, width_pt, height_pt, text, text_source) VALUES (7, 0, 612, 792, 'a lich in a crypt', 'ocr');
                INSERT INTO job (document_id, content_hash, stage, stage_version, status, created_utc) VALUES (7, 'h7', 'Ocr', 1, 'done', '2026-10-08T12:00:00Z');
                INSERT INTO doc_fts (rowid, title, subtitle, publisher, series, tags, notes, confirmed, provisional) VALUES (7, 'Tomb Final', '', '', '', '', '', '', '');
                """);
        }
        var db = IndexDatabase.ForFile(DbPath);

        Assert.Equal(IndexOpenResult.Upgraded, await db.InitializeAsync(TestContext.Current.CancellationToken));

        using var read = db.OpenRead();
        Assert.Equal(IndexSchema.Version, UserVersion(read));
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT count(*) FROM page_fts WHERE page_fts MATCH 'lich'"));
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT count(*) FROM job"));
        // The catalog gives each existing document an entry with the same id, so the library shows before projection runs.
        Assert.Equal((7L, 7L, "Whole"), read.QuerySingle<(long, long, string)>("SELECT entry_id, document_id, kind FROM entry_doc"));
        Assert.Equal(7, read.ExecuteScalar<long>("SELECT rowid FROM entry_fts WHERE entry_fts MATCH 'title : tomb'"));
        Assert.Equal(7, read.ExecuteScalar<long>("SELECT rowid FROM entry_fts WHERE entry_fts MATCH 'provisional : horrors'"));
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM entry_meta"));
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM entry_ai"));
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM sqlite_schema WHERE name IN ('doc_meta', 'doc_facet', 'doc_ai', 'doc_fts')"));
        // Version 6: cards count their copies, and Match looks pages up by fingerprint.
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT copies FROM entry_doc"));
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'page_fingerprint'"));
        // Version 7: packs have a name and their images.
        Assert.Equal((null, 0L), read.QuerySingle<(string?, long)>("SELECT name, members FROM entry_doc"));
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM entry_member"));
        // Version 8: the names of a pack's images are searchable.
        Assert.Equal(0, read.ExecuteScalar<long>("SELECT count(*) FROM member_fts"));
        // Version 9: a book owned elsewhere has no document, and keeps when it was added.
        Assert.Equal((7L, (string?)null), read.QuerySingle<(long, string?)>("SELECT document_id, added_utc FROM entry_doc"));
        Assert.Equal(0L, read.ExecuteScalar<long>("SELECT \"notnull\" FROM pragma_table_info('entry_doc') WHERE name = 'document_id'"));
        Assert.Equal(1, read.ExecuteScalar<long>("SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'entry_doc_document'"));
    }

    [Fact]
    public void Every_step_from_slice_1_has_an_upgrade_and_older_versions_rebuild()
    {
        Assert.NotNull(IndexSchema.UpgradePath(2));
        Assert.Null(IndexSchema.UpgradePath(1));
        Assert.Null(IndexSchema.UpgradePath(IndexSchema.Version));
    }

    [Fact]
    public void The_embedded_schema_declares_the_version_the_code_expects() =>
        Assert.Contains($"PRAGMA user_version = {IndexSchema.Version};", IndexSchema.Script, StringComparison.Ordinal);
}

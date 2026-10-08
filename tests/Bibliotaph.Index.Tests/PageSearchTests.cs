using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index.Tests;

/// <summary>FTS5 behaviour the search design relies on, against in-memory SQLite.</summary>
public sealed class PageSearchTests : IAsyncLifetime
{
    readonly IndexDatabase _db = IndexDatabase.InMemory($"pages-{Guid.NewGuid():N}");
    SqliteConnection _keepAlive = null!;

    public async ValueTask InitializeAsync()
    {
        // A shared-cache in-memory database lives only while a connection holds it open.
        _keepAlive = new SqliteConnection(_db.ConnectionString);
        _keepAlive.Open();
        await _db.InitializeAsync();
        _keepAlive.Execute(
            "INSERT INTO page (document_id, pdf_page, width_pt, height_pt, text) VALUES (@Doc, @Page, 612, 792, @Text)",
            new[]
            {
                new { Doc = 1, Page = 0, Text = "Contents and credits" },
                new { Doc = 1, Page = 4, Text = "A chase through the city ends at the lighthouse." },
                new { Doc = 2, Page = 9, Text = "The Café of Déjà Vu serves crêpes to wandering adventurers." },
            });
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();

    List<(long Doc, long Page)> Search(string match)
    {
        using var c = _db.OpenRead();
        return [.. c.Query<(long, long)>(
            "SELECT p.document_id, p.pdf_page FROM page_fts JOIN page p ON p.id = page_fts.rowid WHERE page_fts MATCH @match ORDER BY bm25(page_fts)",
            new { match })];
    }

    [Fact]
    public void A_phrase_finds_the_page_it_is_on() =>
        Assert.Equal([(1L, 4L)], Search("\"chase through the city\""));

    [Fact]
    public void Diacritics_are_ignored_both_ways()
    {
        Assert.Equal([(2L, 9L)], Search("cafe"));
        Assert.Equal([(2L, 9L)], Search("\"deja vu\""));
        Assert.Equal([(2L, 9L)], Search("crepes"));
    }

    [Fact]
    public void Updating_and_deleting_pages_keeps_the_index_in_step()
    {
        _keepAlive.Execute("UPDATE page SET text = 'Now about owlbears' WHERE document_id = 1 AND pdf_page = 4");
        Assert.Empty(Search("lighthouse"));
        Assert.Equal([(1L, 4L)], Search("owlbears"));

        _keepAlive.Execute("DELETE FROM page WHERE document_id = 1 AND pdf_page = 4");
        Assert.Empty(Search("owlbears"));
    }

    [Fact]
    public void Snippets_quote_the_indexed_page_text()
    {
        using var c = _db.OpenRead();
        var snippet = c.ExecuteScalar<string>(
            "SELECT snippet(page_fts, 0, '[', ']', '…', 6) FROM page_fts WHERE page_fts MATCH 'lighthouse'");

        Assert.Contains("[lighthouse]", snippet, StringComparison.Ordinal);
    }
}

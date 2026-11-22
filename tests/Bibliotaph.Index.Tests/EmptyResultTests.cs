namespace Bibliotaph.Index.Tests;

// Dapper matches a record's constructor to the column types before the first row, and with no rows SQLite names no type
// for a count, so these queries must come back empty rather than fail.
public sealed class EmptyResultTests : IndexFixture
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shared_pages_are_none_when_no_other_document_has_the_pages() =>
        Assert.Empty(await new IndexQueries(Database).GetSharedPagesAsync([1, 2], Ct));

    [Fact]
    public async Task Chapter_values_are_none_before_any_chapter_is_read() =>
        Assert.Empty(await new ChapterQueries(Database).GetValuesAsync(ct: Ct));
}

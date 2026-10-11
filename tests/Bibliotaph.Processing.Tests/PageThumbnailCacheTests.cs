using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

public sealed class PageThumbnailCacheTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-thumbnails-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    AppPaths Paths => new(Path.Combine(_dir, "data"));

    [Fact]
    public void Keeps_each_pages_thumbnail_under_its_book_in_the_cache_folder()
    {
        var cache = new PageThumbnailCache(Paths);

        Assert.Null(cache.Read("ABC123", 4));
        cache.Write("ABC123", 4, [1, 2, 3]);
        cache.Write("ABC123", 5, [4, 5]);

        Assert.Equal([1, 2, 3], cache.Read("abc123", 4));
        Assert.Equal([4, 5], cache.Read("ABC123", 5));
        Assert.Null(cache.Read("ABC123", 6));
        Assert.Null(cache.Read("DEF456", 4));
        Assert.StartsWith(Paths.Cache, cache.Folder, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(cache.Folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Forgetting_a_book_deletes_its_thumbnails_only()
    {
        var cache = new PageThumbnailCache(Paths);
        cache.Write("ABC123", 0, [1]);
        cache.Write("DEF456", 0, [2]);

        cache.Delete("ABC123");
        cache.Delete("NOTHERE");

        Assert.Null(cache.Read("ABC123", 0));
        Assert.Equal([2], cache.Read("DEF456", 0));
    }

    [Fact]
    public void Over_its_limit_the_cache_drops_the_books_used_longest_ago_and_keeps_the_one_opening()
    {
        var cache = new PageThumbnailCache(Paths, limitBytes: 250);
        var page = new byte[100];
        foreach (var book in new[] { "OLDEST", "OLDER", "RECENT", "OPENING" }) cache.Write(book, 0, page);
        // Their last use, oldest first; the book opening now is the oldest of all until it is touched.
        var folders = Path.Combine(Paths.Cache, "pages");
        Directory.SetLastWriteTimeUtc(Path.Combine(folders, "opening"), DateTime.UtcNow.AddDays(-9));
        Directory.SetLastWriteTimeUtc(Path.Combine(folders, "oldest"), DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(Path.Combine(folders, "older"), DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(Path.Combine(folders, "recent"), DateTime.UtcNow.AddDays(-1));

        cache.Touch("OPENING");

        Assert.Null(cache.Read("OLDEST", 0));
        Assert.Null(cache.Read("OLDER", 0));
        Assert.NotNull(cache.Read("RECENT", 0));
        Assert.NotNull(cache.Read("OPENING", 0));
    }

    [Fact]
    public void Touching_a_book_with_no_thumbnails_yet_is_no_error()
    {
        var cache = new PageThumbnailCache(Paths);

        cache.Touch("ABC123");

        Assert.Null(cache.Read("ABC123", 0));
    }

    [Theory]
    [InlineData(612, 792, 144.0 / 612)]  // Letter: held to the width
    [InlineData(200, 1000, 0.2)]         // a tall strip: held to the height
    public void Draws_a_page_at_thumbnail_size(double width, double height, double expected) =>
        Assert.Equal(expected, PageThumbnailCache.ScaleFor(width, height), 6);
}

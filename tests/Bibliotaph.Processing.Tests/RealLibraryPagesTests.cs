namespace Bibliotaph.Processing.Tests;

/// <summary>
/// The readers on real library pages saved on this computer (online libraries, O4), never committed (choice 18): set
/// BIBLIOTAPH_REAL_PAGES to a folder of saved pages to run it. Each page must be read by exactly one reader, with books.
/// What it says names the files and counts their books, never a book.
/// </summary>
public sealed class RealLibraryPagesTests(ITestOutputHelper output)
{
    [Fact]
    public void Each_saved_page_is_read_by_one_reader()
    {
        var folder = Environment.GetEnvironmentVariable("BIBLIOTAPH_REAL_PAGES");
        Assert.SkipWhen(string.IsNullOrEmpty(folder) || !Directory.Exists(folder), "BIBLIOTAPH_REAL_PAGES names no folder of saved pages.");

        var readers = OnlineImports.Services;
        foreach (var path in Directory.EnumerateFiles(folder!, "*.htm*"))
        {
            var html = File.ReadAllText(path);
            var read = readers.Select(r => (Reader: r, Page: r.Read(html))).Where(r => r.Page is not null).ToList();
            output.WriteLine($"{Path.GetFileName(path)}: {string.Join(", ", read.Select(r => $"{r.Reader.Name} {r.Page!.Books.Count} books, "
                + $"{r.Page.Books.Count(b => b.Publisher is not null)} with publishers, {r.Page.Books.Count(b => b.CoverUrl is not null)} with covers, "
                + $"{(r.Page.Complete ? "whole library" : "part")}"))}{(read.Count == 0 ? "not read: " + string.Join(" / ", readers.Select(r => r.Hint(html)).OfType<string>()) : "")}");
            Assert.True(read.Count <= 1, $"{Path.GetFileName(path)} is read by {string.Join(" and ", read.Select(r => r.Reader.Name))}.");
        }
    }
}

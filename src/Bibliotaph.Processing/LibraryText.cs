namespace Bibliotaph.Processing;

/// <summary>The Library's own words that the Windows app and the web library share.</summary>
public static class LibraryText
{
    /// <summary>A search whose words no title or detail has, but some pages do.</summary>
    public const string OnlyInsideTitle = "Only inside books.";

    public const string ShowInside = "Show pages inside documents";

    /// <summary>"No book's title or details have these words, but 52 pages in 7 books do."</summary>
    public static string OnlyInside(long pages, long books) =>
        $"No book's title or details have these words, but {Plural(pages, "page", "pages")} in {Plural(books, "book", "books")} {(pages == 1 ? "does" : "do")}.";

    static string Plural(long count, string one, string many) =>
        $"{count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";
}

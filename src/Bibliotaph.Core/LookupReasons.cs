namespace Bibliotaph.Core;

/// <summary>
/// Why a file wasn't looked up online at all, as its Look up step keeps it: the reasons the index counts by, since a
/// file with nothing to find isn't one no catalogue found.
/// </summary>
public static class LookupReasons
{
    /// <summary>A file with no ISBN, product code or text to find its title in, as an image.</summary>
    public const string NothingAtAll = "It prints no ISBN, product code or title to look up.";

    /// <summary>A map, a character sheet or a handout, which no catalogue of books lists.</summary>
    public const string NotABook = "It looks like a map, a sheet or a handout rather than a book, so it isn't looked up.";
}

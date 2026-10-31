namespace Bibliotaph.Core.Search;

/// <summary>
/// The Found online filter's facets: one for each catalogue that found the book (RPGGeek, Open Library), by the
/// catalogue's name. They come from the lookups' records, not the book's metadata.
/// </summary>
public static class FoundOnline
{
    public const string Field = "found";

    /// <summary>What the filter calls the books no catalogue found, including those not looked up yet.</summary>
    public const string NotFound = "Not found online";
}

namespace Bibliotaph.Index;

/// <summary>
/// The SQL that files a publisher's several names under one (<see cref="Core.Metadata.PublisherNames"/>), through the
/// <c>publisher_key</c> function every read connection has.
/// </summary>
static class PublisherSql
{
    /// <summary>
    /// Each publisher (<c>k</c>) with the name it's filed under (<c>name</c>): of the names the library's cards use, the
    /// one most use, and on a tie the shorter, as <see cref="Core.Metadata.PublisherNames.Choose"/> picks.
    /// </summary>
    public const string Chosen = """
        SELECT k, name FROM (
            SELECT publisher_key(publisher) AS k, publisher AS name,
                   row_number() OVER (PARTITION BY publisher_key(publisher) ORDER BY count(*) DESC, length(publisher), publisher) AS r
            FROM entry_meta WHERE publisher IS NOT NULL AND publisher <> '' GROUP BY publisher)
        WHERE r = 1
        """;
}

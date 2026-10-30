using System.Globalization;
using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// The Summary page's figures that index.db knows: what the cards are, what they're about, what has been opened and
/// read. Every figure is over the cards in a scope (the books in the library's folders, and those owned elsewhere),
/// counted as the Library counts them: a file's card only once Probe has its document.
/// </summary>
public sealed class SummaryQueries(IndexDatabase database)
{
    /// <summary>The fields the page lists the values of.</summary>
    static readonly string[] ListedFields =
        [MetadataFields.System.Key, MetadataFields.Types.Key, MetadataFields.Settings.Key, MetadataFields.AlsoOwn.Key, MetadataFields.GetItFrom.Key];

    // The cards in scope, as the Library would list them.
    const string Shown = """
        WITH shown AS (
            SELECT e.entry_id, e.document_id, e.kind, e.copies, coalesce(e.added_utc, d.added_utc) AS added_utc, d.format
            FROM entry_doc e LEFT JOIN doc d ON d.document_id = e.document_id
            WHERE (d.document_id IS NOT NULL OR e.document_id IS NULL) AND e.entry_id IN (SELECT value FROM json_each(@scope)))
        """;

    /// <summary>
    /// The figures for the cards in <paramref name="scope"/>, with <see cref="LibrarySummary.Growth"/> running to the
    /// month of <paramref name="today"/>. Folders and the user's own things (collections, binders, notes, saved
    /// searches) are catalog.db's to add.
    /// </summary>
    public async Task<LibrarySummary> GetAsync(IReadOnlyCollection<EntryId> scope, DateOnly today, CancellationToken ct = default)
    {
        var args = new DynamicParameters();
        args.Add("scope", JsonSerializer.Serialize(scope));
        args.Add("ebooks", JsonSerializer.Serialize(SourceFormats.Ebooks));
        args.Add("fields", JsonSerializer.Serialize(ListedFields));
        args.Add("favorites", ScopeKeys.Favorites);
        args.Add("adventure", AdventureType);
        await using var connection = database.OpenRead();
        Task<T> One<T>(string sql) => connection.QuerySingleAsync<T>(new CommandDefinition(Shown + sql, args, cancellationToken: ct));
        Task<IEnumerable<T>> Many<T>(string sql) => connection.QueryAsync<T>(new CommandDefinition(Shown + sql, args, cancellationToken: ct));

        var counts = await One<CountsRow>(
            """
            SELECT
                coalesce(sum(kind <> 'Part'), 0) AS Books,
                coalesce(sum(kind = 'Whole'), 0) AS Files,
                coalesce(sum(kind = 'Pack'), 0) AS Packs,
                coalesce(sum(kind = 'Part'), 0) AS Adventures,
                coalesce(sum(kind = 'Elsewhere'), 0) AS Elsewhere,
                coalesce(sum(kind = 'Whole' AND format IN (SELECT value FROM json_each(@ebooks))), 0) AS Ebooks,
                coalesce(sum(CASE WHEN kind = 'Whole' AND copies > 1 THEN copies - 1 ELSE 0 END), 0) AS Duplicates,
                coalesce(sum(kind <> 'Part' AND entry_id IN (SELECT entry_id FROM entry_opened)), 0) AS Opened,
                coalesce(sum(kind <> 'Part' AND entry_id IN (SELECT entry_id FROM entry_scope WHERE scope = @favorites)), 0) AS Favorites,
                coalesce(sum(kind <> 'Part' AND entry_id IN (SELECT entry_id FROM entry_ai)), 0) AS ReadByAi,
                coalesce(sum(entry_id IN (SELECT entry_id FROM entry_note)), 0) AS NotedBooks,
                coalesce(sum(kind = 'Whole' AND document_id IN
                    (SELECT document_id FROM stage_status WHERE stage = 'Text' AND status IN ('Complete', 'Partial', 'Skipped'))), 0) AS TextRead
            FROM shown
            """);

        // A pack's pages are its files'; a book in several copies counts once, through the file its card opens.
        var pages = await One<PagesRow>(
            """
            , docs AS (
                SELECT document_id FROM shown WHERE kind = 'Whole'
                UNION SELECT em.document_id FROM entry_member em JOIN shown p ON p.entry_id = em.entry_id WHERE p.kind = 'Pack')
            SELECT
                (SELECT coalesce(sum(page_count), 0) FROM doc WHERE document_id IN (SELECT document_id FROM docs)) AS Pages,
                (SELECT count(*) FROM page WHERE text_source = 'ocr' AND document_id IN (SELECT document_id FROM docs)) AS Ocr
            """);

        var values = (await Many<ValueRow>(
            """
            SELECT f.field AS Field, f.value AS Value, min(f.label) AS Label, count(*) AS Count, sum(f.confirmed) AS Confirmed
            FROM shown e JOIN entry_facet f ON f.entry_id = e.entry_id
            WHERE e.kind <> 'Part' AND f.field IN (SELECT value FROM json_each(@fields))
            GROUP BY f.field, f.value
            ORDER BY count(*) DESC, min(f.label) COLLATE NOCASE
            """)).Select(v => new SummaryValue(v.Field, v.Value, v.Label, v.Count, v.Confirmed)).ToList();

        var other = await One<OtherRow>(
            """
            SELECT
                coalesce(sum(kind IN ('Whole', 'Elsewhere') AND entry_id NOT IN (SELECT entry_id FROM entry_facet WHERE field = 'system')), 0) AS SystemUnknown,
                coalesce(sum(kind = 'Elsewhere' AND entry_id IN (SELECT entry_id FROM entry_facet WHERE field = 'store')), 0) AS StoreWaiting,
                coalesce(sum(kind = 'Whole' AND entry_id IN (SELECT entry_id FROM entry_facet WHERE field = 'store')), 0) AS StoreDownloaded,
                coalesce(sum(kind <> 'Pack' AND entry_id IN (SELECT entry_id FROM entry_facet WHERE field = 'type' AND value = @adventure)
                    AND entry_id IN (SELECT entry_id FROM entry_meta WHERE level_state = 'unknown')), 0) AS LevelsUnknown
            FROM shown
            """);

        // Adventures inside files count here too: a level is what a game master looks for one by.
        var levels = new long[SummaryText.TopLevel];
        foreach (var (min, max) in await Many<(long Min, long Max)>(
            """
            SELECT m.level_min, m.level_max FROM shown e JOIN entry_meta m ON m.entry_id = e.entry_id
            WHERE e.kind <> 'Pack' AND m.level_state = 'known' AND m.level_min IS NOT NULL AND m.level_max IS NOT NULL
            """))
        {
            var top = Math.Min(Math.Max(min, max), SummaryText.TopLevel);
            for (var level = Math.Max(1, min); level <= top; level++) levels[level - 1]++;
        }

        var years = (await Many<(long Year, long Count)>(
            """
            SELECT m.year, count(*) FROM shown e JOIN entry_meta m ON m.entry_id = e.entry_id
            WHERE e.kind IN ('Whole', 'Elsewhere') AND m.year IS NOT NULL
            GROUP BY m.year
            """)).Where(y => y.Year >= FirstYear && y.Year <= today.Year + 1).ToList();
        var withYear = years.Sum(y => y.Count);
        var dated = counts.Files + counts.Elsewhere;

        var days = (await Many<(string Day, long Count)>(
            """
            SELECT substr(added_utc, 1, 10), count(*) FROM shown
            WHERE kind <> 'Part' AND added_utc IS NOT NULL
            GROUP BY substr(added_utc, 1, 10)
            """)).Select(d => (Day: DateOnly.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture), d.Count)).OrderBy(d => d.Day).ToList();

        return new LibrarySummary
        {
            Books = counts.Books,
            Files = counts.Files,
            Packs = counts.Packs,
            Adventures = counts.Adventures,
            Elsewhere = counts.Elsewhere,
            Ebooks = counts.Ebooks,
            Duplicates = counts.Duplicates,
            Pages = pages.Pages,
            OcrPages = pages.Ocr,
            Opened = counts.Opened,
            Favorites = counts.Favorites,
            ReadByAi = counts.ReadByAi,
            NotedBooks = counts.NotedBooks,
            TextRead = counts.TextRead,
            Sources = [.. values.Where(v => v.Field == MetadataFields.AlsoOwn.Key || v.Field == MetadataFields.GetItFrom.Key)],
            Systems = [.. values.Where(v => v.Field == MetadataFields.System.Key)],
            Types = [.. values.Where(v => v.Field == MetadataFields.Types.Key)],
            Settings = [.. values.Where(v => v.Field == MetadataFields.Settings.Key)],
            SystemUnknown = other.SystemUnknown,
            StoreDownloaded = other.StoreDownloaded,
            StoreWaiting = other.StoreWaiting,
            Levels = levels,
            LevelsUnknown = other.LevelsUnknown,
            Decades = Decades(years),
            OldestYear = years.Count == 0 ? null : (int)years.Min(y => y.Year),
            NewestYear = years.Count == 0 ? null : (int)years.Max(y => y.Year),
            YearUnknown = Math.Max(0, dated - withYear),
            Growth = Growth(days, today),
            BiggestDay = days.Where(d => d.Count > 1).OrderByDescending(d => d.Count).ThenBy(d => d.Day).Select(d => new SummaryDay(d.Day, d.Count)).FirstOrDefault(),
        };
    }

    /// <summary>How many cards the Library shows of <paramref name="scope"/>: one folder's books, say.</summary>
    public async Task<long> CountBooksAsync(IReadOnlyCollection<EntryId> scope, CancellationToken ct = default)
    {
        if (scope.Count == 0) return 0;
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(Shown + "SELECT count(*) FROM shown WHERE kind <> 'Part'",
            new { scope = JsonSerializer.Serialize(scope) }, cancellationToken: ct));
    }

    /// <summary>The type vocabulary's key for adventures, as the catalog files them.</summary>
    const string AdventureType = "adventure";

    /// <summary>Years before this are taken for a typing slip, not a publication date.</summary>
    const int FirstYear = 1900;

    /// <summary>Every decade from the first with a book to the last, so a gap shows as a gap.</summary>
    static IReadOnlyList<SummaryDecade> Decades(List<(long Year, long Count)> years)
    {
        if (years.Count == 0) return [];
        var byDecade = years.GroupBy(y => (int)(y.Year / 10 * 10)).ToDictionary(g => g.Key, g => g.Sum(y => y.Count));
        var first = byDecade.Keys.Min();
        var last = byDecade.Keys.Max();
        return [.. Enumerable.Range(0, (last - first) / 10 + 1).Select(i => first + i * 10).Select(d => new SummaryDecade(d, byDecade.GetValueOrDefault(d)))];
    }

    /// <summary>The running total at the end of each month, from the first book's month to this one.</summary>
    static List<SummaryMonth> Growth(List<(DateOnly Day, long Count)> days, DateOnly today)
    {
        if (days.Count == 0) return [];
        var month = new DateOnly(days[0].Day.Year, days[0].Day.Month, 1);
        var last = new DateOnly(today.Year, today.Month, 1);
        if (last < month) last = new DateOnly(days[^1].Day.Year, days[^1].Day.Month, 1);
        var growth = new List<SummaryMonth>();
        long total = 0;
        var i = 0;
        for (; month <= last; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            for (; i < days.Count && days[i].Day < next; i++) total += days[i].Count;
            growth.Add(new SummaryMonth(month, total));
        }
        return growth;
    }

    sealed record CountsRow(long Books, long Files, long Packs, long Adventures, long Elsewhere, long Ebooks, long Duplicates, long Opened,
        long Favorites, long ReadByAi, long NotedBooks, long TextRead);

    sealed record PagesRow(long Pages, long Ocr);

    sealed record ValueRow(string Field, string Value, string Label, long Count, long Confirmed);

    sealed record OtherRow(long SystemUnknown, long StoreWaiting, long StoreDownloaded, long LevelsUnknown);
}

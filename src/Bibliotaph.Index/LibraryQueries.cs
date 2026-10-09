using System.Globalization;
using System.Text;
using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// A library card as the grid and search results show it: an entry, through the document it shows
/// (<see cref="DocumentId"/>, which opens). <see cref="Title"/> is its effective title, or the one from its file name. <see cref="System"/> ("D&amp;D 5e"), <see cref="Kind"/> ("Adventure") and <see cref="Levels"/>
/// ("Levels 1–5") read as cards show them; <see cref="Suggested"/> means some of it is unconfirmed. <see cref="AiModel"/>
/// is the model that last read it, if one has. <see cref="Copies"/> counts the files of the book it has. A pack
/// (<see cref="EntryKind"/>) shows its first image, counts its <see cref="Members"/>, and has the covers of its first
/// four images for its mosaic (<see cref="MemberCovers"/>).
/// </summary>
public sealed record LibraryEntry(
    EntryId EntryId, long DocumentId, string Title, string Format, int? PageCount, string? Cover, string? FolderHint, DateTime AddedUtc, bool Searchable,
    string? System = null, string? Kind = null, string? Publisher = null, string? Levels = null, bool NeedsReview = false, bool Suggested = false,
    string? AiModel = null, int Copies = 1, EntryKind EntryKind = EntryKind.Whole, int Members = 0, string? MemberCovers = null)
{
    public bool IsPack => EntryKind == EntryKind.Pack;

    /// <summary>The cover file names of a pack's first four images that have one.</summary>
    public IReadOnlyList<string> MosaicCovers => MemberCovers is null ? [] : JsonSerializer.Deserialize<List<string>>(MemberCovers) ?? [];
}

/// <summary>An image in a pack, in file name order: the document it shows, its own entry, its name and its cover.</summary>
public sealed record PackImage(long DocumentId, EntryId MemberEntryId, string Name, string Format, string? Cover);

public enum LibrarySort
{
    /// <summary>Best match first; for a library with no search, the same as <see cref="RecentlyAdded"/>.</summary>
    Relevance,
    RecentlyAdded,
    Title,
    /// <summary>By publisher, documents without one last, then by title.</summary>
    Publisher,
}

/// <summary>Kind choices in the filter panel (F4 plan, choice 10). The format: field gives finer control.</summary>
public enum KindFilter
{
    All,
    /// <summary>PDFs.</summary>
    Books,
    /// <summary>Single images, not those in packs.</summary>
    Images,
    Packs,
}

/// <summary>The AI choice in the filter panel: books a model has read, or those it hasn't yet.</summary>
public enum AiFilter
{
    All,
    Read,
    NotRead,
}

/// <summary>
/// What the library shows: the entries in scope (from catalog.db: with a file present in a folder the user hasn't
/// removed, optionally one folder), a format, game systems and document types (any of those chosen; unknown is
/// <see cref="SearchQuery.Unknown"/>), a level, and an order. A level matches books whose range contains it; books
/// with unknown levels match only with <see cref="IncludeUnknownLevel"/> (A12). <see cref="Ai"/> keeps books a model
/// has or hasn't read, and <see cref="OnlyWithCopies"/> those with more than one file.
/// </summary>
public sealed record LibraryFilter(
    IReadOnlyCollection<EntryId>? Scope = null,
    KindFilter Kind = KindFilter.All,
    LibrarySort Sort = LibrarySort.Relevance,
    IReadOnlyCollection<string>? Systems = null,
    IReadOnlyCollection<string>? Types = null,
    int? Level = null,
    bool IncludeUnknownLevel = false,
    AiFilter Ai = AiFilter.All,
    bool OnlyWithCopies = false);

/// <summary>How many entries have a value, for the filter panel. <see cref="Value"/> is <see cref="SearchQuery.Unknown"/> for those with none.</summary>
public sealed record FacetCount(string Value, string Label, long Count);

/// <summary>A page that matched, with its indexed text quoted around the hits. Hits sit between <see cref="LibraryQueries.HitStart"/> and <see cref="LibraryQueries.HitEnd"/>.</summary>
public sealed record PageHit(EntryId EntryId, long DocumentId, int PdfPage, string? Label, string Snippet, bool FromOcr);

public sealed record EntryHits(LibraryEntry Entry, IReadOnlyList<PageHit> Pages, long MatchingPages);

/// <summary>Inside-documents results: the best pages of the best books, and how many matched in all.</summary>
public sealed record PageResults(IReadOnlyList<EntryHits> Entries, long MatchingEntries, long MatchingPages)
{
    public static PageResults None { get; } = new([], 0, 0);
}

public sealed record StageState(Stage Stage, StageStatus Status, string? Reason);

/// <summary>What the inspector shows from index.db.</summary>
public sealed record DocumentDetails(
    LibraryEntry Entry, bool Encrypted, bool CanCopy, string? MetaTitle, string? MetaAuthor, string? MetaSubject,
    int? WidthPx, int? HeightPx, long OcrPages, long PagesAwaitingOcr, IReadOnlyList<StageState> Stages);

/// <summary>
/// The library grid and both search tabs, over index.db. SQL text comes from fixed fragments; every value,
/// including FTS5 expressions built by <see cref="SearchPlan"/>, is a bound parameter.
/// </summary>
public sealed class LibraryQueries(IndexDatabase database)
{
    /// <summary>Marks the start of a hit in a snippet. Control characters, so they can't clash with page text.</summary>
    public const char HitStart = '\u0002';

    /// <summary>Marks the end of a hit in a snippet.</summary>
    public const char HitEnd = '\u0003';

    /// <summary>Words of context around hits in a snippet.</summary>
    const int SnippetTokens = 24;

    /// <summary>Column weights for entry_fts, in schema order: title, subtitle, publisher, series, authors, tags, notes, confirmed, provisional.</summary>
    const string DocRank = "bm25(entry_fts, 10.0, 5.0, 2.0, 3.0, 2.0, 3.0, 1.0, 4.0, 1.0)";

    const string EntryColumns = """
        e.entry_id AS EntryId, d.document_id AS DocumentId, coalesce(m.title, e.name, d.display_title) AS Title, d.format AS Format, d.page_count AS PageCount, d.cover AS Cover,
        d.folder_hint AS FolderHint, d.added_utc AS AddedUtc,
        coalesce(s.status IN ('Complete', 'Partial', 'Skipped'), 0) AS Searchable,
        m.system_label AS SystemLabel, m.kind_label AS KindLabel, m.publisher AS Publisher, m.level_min AS LevelMin, m.level_max AS LevelMax,
        coalesce(m.level_state, 'unknown') AS LevelState, coalesce(m.needs_review, 0) AS NeedsReview, coalesce(m.suggested, 0) AS Suggested,
        ai.model AS AiModel, e.copies AS Copies, e.kind AS EntryKind, e.members AS Members,
        CASE WHEN e.kind = 'Pack' THEN (
            SELECT json_group_array(cover) FROM (
                SELECT md.cover FROM entry_member em JOIN doc md ON md.document_id = em.document_id
                WHERE em.entry_id = e.entry_id AND md.cover IS NOT NULL ORDER BY em.ord LIMIT 4)) END AS MemberCovers
        """;

    /// <summary>Library cards: each entry with the document it shows.</summary>
    const string Entries = "entry_doc e JOIN doc d ON d.document_id = e.document_id";

    const string EntryJoin = """
        LEFT JOIN stage_status s ON s.document_id = d.document_id AND s.stage = 'Text'
        LEFT JOIN entry_meta m ON m.entry_id = e.entry_id
        LEFT JOIN entry_ai ai ON ai.entry_id = e.entry_id
        """;

    /// <summary>The whole library (or the part in scope) with the plan's fields and exclusions applied, in the filter's order.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> ListAsync(LibraryFilter filter, SearchPlan? plan = null, CancellationToken ct = default)
    {
        var where = new Where(filter, plan ?? new SearchPlan(), titleAsFilter: true);
        var sql = $"""
            SELECT {EntryColumns}
            FROM {Entries} {EntryJoin}
            WHERE {where.Sql}
            ORDER BY {Order(filter.Sort, relevance: null)}
            """;
        await using var connection = database.OpenRead();
        return ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)));
    }

    /// <summary>The Documents tab: titles and metadata. With no words or title: to find, this is the filtered library.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> SearchDocumentsAsync(SearchPlan plan, LibraryFilter filter, int limit = 1000, CancellationToken ct = default)
    {
        if (plan.DocumentMatch is not { } match) return [.. (await ListAsync(filter, plan, ct)).Take(limit)];

        var where = new Where(filter, plan, titleAsFilter: false);
        where.Parameters.Add("match", match);
        where.Parameters.Add("limit", limit);
        var sql = $"""
            SELECT {EntryColumns}
            FROM entry_fts JOIN entry_doc e ON e.entry_id = entry_fts.rowid JOIN doc d ON d.document_id = e.document_id {EntryJoin}
            WHERE entry_fts MATCH @match AND {where.Sql}
            ORDER BY {Order(filter.Sort, relevance: DocRank)}
            LIMIT @limit
            """;
        await using var connection = database.OpenRead();
        return ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)));
    }

    /// <summary>
    /// How many entries have each value of a vocabulary field (<c>system</c>, <c>type</c>), among those the library
    /// shows for <paramref name="filter"/> and <paramref name="plan"/>, plus those with none (<see cref="SearchQuery.Unknown"/>).
    /// To count the choices within a dimension, pass a filter without that dimension's own choices.
    /// </summary>
    public async Task<IReadOnlyList<FacetCount>> GetFacetCountsAsync(string field, LibraryFilter filter, SearchPlan? plan = null, CancellationToken ct = default)
    {
        plan ??= new SearchPlan();
        var where = new Where(filter, plan, titleAsFilter: true);
        if (plan.TextMatch is { } match) where.Also("e.entry_id IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @countMatch)", "countMatch", match);
        where.Parameters.Add("countField", field);
        await using var connection = database.OpenRead();
        var counts = (await connection.QueryAsync<(string Value, string Label, long Count)>(new CommandDefinition(
            $"""
            SELECT f.value, min(f.label), count(*)
            FROM {Entries} JOIN entry_facet f ON f.entry_id = e.entry_id AND f.field = @countField
            WHERE {where.Sql}
            GROUP BY f.value
            ORDER BY count(*) DESC, min(f.label) COLLATE NOCASE
            """, where.Parameters, cancellationToken: ct))).Select(c => new FacetCount(c.Value, c.Label, c.Count)).ToList();
        var unknown = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT count(*) FROM {Entries}
            WHERE {where.Sql} AND e.entry_id NOT IN (SELECT entry_id FROM entry_facet WHERE field = @countField)
            """, where.Parameters, cancellationToken: ct));
        if (unknown > 0) counts.Add(new FacetCount(SearchQuery.Unknown, "Unknown", unknown));
        return counts;
    }

    /// <summary>
    /// How many entries have each format (pdf, jpg, png), most first, among those in <paramref name="filter"/>'s
    /// scope: the values the search box's field guide offers after <c>format:</c>. <see cref="FacetCount.Label"/> is the
    /// format in capitals.
    /// </summary>
    public async Task<IReadOnlyList<FacetCount>> GetFormatCountsAsync(LibraryFilter filter, CancellationToken ct = default)
    {
        var where = new Where(filter, new SearchPlan(), titleAsFilter: true);
        await using var connection = database.OpenRead();
        var counts = await connection.QueryAsync<(string Format, long Count)>(new CommandDefinition(
            $"""
            SELECT d.format, count(*)
            FROM {Entries}
            WHERE {where.Sql}
            GROUP BY d.format
            ORDER BY count(*) DESC, d.format
            """, where.Parameters, cancellationToken: ct));
        return [.. counts.Select(c => new FacetCount(c.Format, c.Format.ToUpperInvariant(), c.Count))];
    }

    /// <summary>How many entries a model has read, so the filter panel offers the AI choice only once there are some.</summary>
    public async Task<long> CountAiReadAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM entry_ai", cancellationToken: ct));
    }

    /// <summary>How many books are in more than one file, for the filter panel's Copies choice.</summary>
    public async Task<long> CountWithCopiesAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM entry_doc WHERE copies > 1", cancellationToken: ct));
    }

    /// <summary>
    /// The Inside documents tab: pages whose text matches, grouped by book with the best books first, at most
    /// <paramref name="pagesPerDocument"/> pages each. Only pages of the document each entry shows count, so other
    /// copies of a book don't repeat its hits. Snippets are made only for the pages returned.
    /// Exclusions alone find no pages, so a query like <c>-maps</c> returns nothing here.
    /// </summary>
    public async Task<PageResults> SearchPagesAsync(
        SearchPlan plan, LibraryFilter filter, int maxDocuments = 100, int pagesPerDocument = 3, CancellationToken ct = default)
    {
        if (plan.TextMatch is not { } match) return PageResults.None;

        var where = new Where(filter, plan, titleAsFilter: true);
        where.Parameters.Add("match", match);
        where.Parameters.Add("perDocument", pagesPerDocument);
        var sql = $"""
            WITH matched AS (
                SELECT p.document_id AS doc, p.id AS page_id, page_fts.rank AS score
                FROM page_fts JOIN page p ON p.id = page_fts.rowid
                WHERE page_fts MATCH @match
            ),
            ranked AS (
                SELECT e.entry_id AS entry, m.page_id,
                       row_number() OVER (PARTITION BY e.entry_id ORDER BY m.score, m.page_id) AS n,
                       min(m.score) OVER (PARTITION BY e.entry_id) AS best,
                       count(*) OVER (PARTITION BY e.entry_id) AS pages
                FROM matched m JOIN entry_doc e ON e.document_id = m.doc JOIN doc d ON d.document_id = e.document_id
                WHERE {where.Sql}
            )
            SELECT entry AS Entry, page_id AS PageId, pages AS Pages FROM ranked WHERE n <= @perDocument ORDER BY best, entry, n
            """;

        await using var connection = database.OpenRead();
        var rows = (await connection.QueryAsync<(long Entry, long PageId, long Pages)>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return PageResults.None;

        // Rows arrive grouped by book, best book first.
        var books = new List<(long Entry, long Pages, List<long> PageIds)>();
        foreach (var (entry, pageId, pages) in rows)
        {
            if (books.Count == 0 || books[^1].Entry != entry) books.Add((entry, pages, []));
            books[^1].PageIds.Add(pageId);
        }
        var matchingPages = books.Sum(b => b.Pages);
        var shown = books.Take(maxDocuments).ToList();

        var hits = (await connection.QueryAsync<HitRow>(new CommandDefinition(
            """
            SELECT p.id AS PageId, p.document_id AS DocumentId, p.pdf_page AS PdfPage, p.label AS Label, p.text_source AS Source,
                   snippet(page_fts, 0, @start, @end, '…', @tokens) AS Snippet
            FROM page_fts JOIN page p ON p.id = page_fts.rowid
            WHERE page_fts MATCH @match AND page_fts.rowid IN (SELECT value FROM json_each(@ids))
            """,
            new
            {
                match,
                start = HitStart.ToString(),
                end = HitEnd.ToString(),
                tokens = SnippetTokens,
                ids = JsonSerializer.Serialize(shown.SelectMany(b => b.PageIds)),
            },
            cancellationToken: ct))).ToDictionary(h => h.PageId);

        var entries = (await GetEntriesAsync(connection, shown.Select(b => b.Entry), ct)).ToDictionary(e => e.EntryId.Value);
        var found = shown
            .Where(b => entries.ContainsKey(b.Entry))
            .Select(b => new EntryHits(
                entries[b.Entry],
                [.. b.PageIds.Where(hits.ContainsKey).Select(id => hits[id]).Select(h =>
                    new PageHit(new EntryId(b.Entry), h.DocumentId, (int)h.PdfPage, h.Label, h.Snippet, h.Source == "ocr"))],
                b.Pages))
            .ToList();
        return new PageResults(found, books.Count, matchingPages);
    }

    /// <summary>
    /// Everything the inspector shows from index.db about an entry and the document it shows, or null for an entry
    /// whose document Probe hasn't reached.
    /// </summary>
    public async Task<DocumentDetails?> GetDetailsAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var entry = (await GetEntriesAsync(connection, [entryId.Value], ct)).SingleOrDefault();
        if (entry is null) return null;
        var documentId = entry.DocumentId;

        var (encrypted, canCopy, metaTitle, metaAuthor, metaSubject, widthPx, heightPx) =
            await connection.QuerySingleAsync<(long Encrypted, long CanCopy, string? MetaTitle, string? MetaAuthor, string? MetaSubject, long? WidthPx, long? HeightPx)>(
            new CommandDefinition(
                "SELECT encrypted, can_copy, meta_title, meta_author, meta_subject, width_px, height_px FROM doc WHERE document_id = @documentId",
                new { documentId }, cancellationToken: ct));
        var (ocrPages, awaiting) = await connection.QuerySingleAsync<(long OcrPages, long Awaiting)>(new CommandDefinition(
            """
            SELECT count(*) FILTER (WHERE text_source = 'ocr'), count(*) FILTER (WHERE needs_ocr = 1)
            FROM page WHERE document_id = @documentId
            """, new { documentId }, cancellationToken: ct));
        var stages = await connection.QueryAsync<(string Stage, string Status, string? Reason)>(new CommandDefinition(
            "SELECT stage, status, reason FROM stage_status WHERE document_id = @documentId", new { documentId }, cancellationToken: ct));

        return new DocumentDetails(entry, encrypted != 0, canCopy != 0, metaTitle, metaAuthor, metaSubject,
            (int?)widthPx, (int?)heightPx, ocrPages, awaiting,
            [.. stages.Select(s => new StageState(Enum.Parse<Stage>(s.Stage), Enum.Parse<StageStatus>(s.Status), s.Reason))
                .OrderBy(s => s.Stage)]); // the enum is in pipeline order
    }

    /// <summary>
    /// The words OCR read on a page, in reading order, with their boxes in PDF points; empty when the page's text came
    /// from the PDF itself. The viewer selects against these on scanned pages.
    /// </summary>
    public async Task<IReadOnlyList<OcrWordRow>> GetOcrWordsAsync(long documentId, int pdfPage, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<WordRow>(new CommandDefinition(
            """
            SELECT w.text AS Text, w.left_pt AS LeftPt, w.top_pt AS TopPt, w.right_pt AS RightPt, w.bottom_pt AS BottomPt
            FROM ocr_word w JOIN page p ON p.id = w.page_id
            WHERE p.document_id = @documentId AND p.pdf_page = @pdfPage AND p.text_source = 'ocr'
            ORDER BY w.ord
            """, new { documentId, pdfPage }, cancellationToken: ct));
        return [.. rows.Select(r => new OcrWordRow(r.Text, r.LeftPt, r.TopPt, r.RightPt, r.BottomPt))];
    }

    /// <summary>A pack's images in file name order, for its inspector grid and for stepping through it in the viewer.</summary>
    public async Task<IReadOnlyList<PackImage>> GetPackImagesAsync(EntryId packId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, long MemberEntryId, string Name, string? Format, string? Cover)>(new CommandDefinition(
            """
            SELECT em.document_id, em.member_entry_id, em.name, d.format, d.cover
            FROM entry_member em LEFT JOIN doc d ON d.document_id = em.document_id
            WHERE em.entry_id = @packId
            ORDER BY em.ord
            """, new { packId = packId.Value }, cancellationToken: ct));
        return [.. rows.Select(r => new PackImage(r.DocumentId, new EntryId(r.MemberEntryId), r.Name, r.Format ?? "", r.Cover))];
    }

    /// <summary>The library cards of these entries, as the grid shows them.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> GetEntriesAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await GetEntriesAsync(connection, entryIds.Select(e => e.Value), ct);
    }

    static async Task<IReadOnlyList<LibraryEntry>> GetEntriesAsync(Microsoft.Data.Sqlite.SqliteConnection connection, IEnumerable<long> entryIds, CancellationToken ct) =>
        ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(
            $"""
            SELECT {EntryColumns}
            FROM {Entries} {EntryJoin}
            WHERE e.entry_id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = JsonSerializer.Serialize(entryIds) }, cancellationToken: ct)));

    static string Order(LibrarySort sort, string? relevance) => sort switch
    {
        LibrarySort.Title => "coalesce(m.title, e.name, d.display_title) COLLATE NOCASE, e.entry_id",
        LibrarySort.Publisher => "m.publisher IS NULL, m.publisher COLLATE NOCASE, coalesce(m.title, e.name, d.display_title) COLLATE NOCASE, e.entry_id",
        LibrarySort.Relevance when relevance is not null => $"{relevance}, e.entry_id",
        _ => "d.added_utc DESC, e.entry_id DESC",
    };

    static List<LibraryEntry> ToEntries(IEnumerable<EntryRow> rows) =>
        [.. rows.Select(r => new LibraryEntry(new EntryId(r.EntryId), r.DocumentId, r.Title, r.Format, (int?)r.PageCount, r.Cover, r.FolderHint,
            DateTime.Parse(r.AddedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            r.Searchable != 0, r.SystemLabel, r.KindLabel, r.Publisher, DescribeLevels(r.LevelState, r.LevelMin, r.LevelMax),
            r.NeedsReview != 0, r.Suggested != 0, r.AiModel, (int)r.Copies, Enum.Parse<EntryKind>(r.EntryKind), (int)r.Members, r.MemberCovers))];

    static string? DescribeLevels(string state, long? min, long? max) => state switch
    {
        "na" => LevelRange.None.Describe(),
        "known" when min is { } low && max is { } high => new LevelRange((int)low, (int)high).Describe(),
        _ => null,
    };

    // Settable properties rather than constructors: SQLite reports no type for an expression column in an empty
    // result, and Dapper's constructor matching then fails where property mapping converts.

    sealed class WordRow
    {
        public string Text { get; init; } = "";
        public double LeftPt { get; init; }
        public double TopPt { get; init; }
        public double RightPt { get; init; }
        public double BottomPt { get; init; }
    }

    sealed class EntryRow
    {
        public long EntryId { get; init; }
        public long DocumentId { get; init; }
        public string Title { get; init; } = "";
        public string Format { get; init; } = "";
        public long? PageCount { get; init; }
        public string? Cover { get; init; }
        public string? FolderHint { get; init; }
        public string AddedUtc { get; init; } = "";
        public long Searchable { get; init; }
        public string? SystemLabel { get; init; }
        public string? KindLabel { get; init; }
        public string? Publisher { get; init; }
        public long? LevelMin { get; init; }
        public long? LevelMax { get; init; }
        public string LevelState { get; init; } = "unknown";
        public long NeedsReview { get; init; }
        public long Suggested { get; init; }
        public string? AiModel { get; init; }
        public long Copies { get; init; } = 1;
        public string EntryKind { get; init; } = nameof(Core.EntryKind.Whole);
        public long Members { get; init; }
        public string? MemberCovers { get; init; }
    }

    sealed class HitRow
    {
        public long PageId { get; init; }
        public long DocumentId { get; init; }
        public long PdfPage { get; init; }
        public string? Label { get; init; }
        public string Source { get; init; } = "";
        public string Snippet { get; init; } = "";
    }

    /// <summary>
    /// The WHERE clause shared by the library and both tabs, over <c>entry_doc e</c> and <c>doc d</c>, the document each entry shows. Built from fixed fragments with
    /// parameter names it numbers itself; values go into <see cref="Parameters"/>.
    /// </summary>
    sealed class Where
    {
        readonly StringBuilder _sql = new("1");

        public DynamicParameters Parameters { get; } = new();

        public string Sql => _sql.ToString();

        /// <param name="filter">The scope, format and order the user chose.</param>
        /// <param name="plan">The parsed search; its fields and exclusions become clauses here.</param>
        /// <param name="titleAsFilter">
        /// Apply title: as a document filter: for the library and page search. The Documents tab has it in its MATCH instead.
        /// </param>
        public Where(LibraryFilter filter, SearchPlan plan, bool titleAsFilter)
        {
            if (filter.Scope is { } scope) Add("e.entry_id IN (SELECT value FROM json_each(@scope))", "scope", JsonSerializer.Serialize(scope));

            switch (filter.Kind)
            {
                case KindFilter.Books:
                    Add("e.kind <> 'Pack' AND d.format = @formatPdf", "formatPdf", SourceFormats.Pdf);
                    break;
                case KindFilter.Images:
                    Add("e.kind <> 'Pack' AND d.format IN (@formatJpeg, @formatPng)", "formatJpeg", SourceFormats.Jpeg);
                    Parameters.Add("formatPng", SourceFormats.Png);
                    break;
                case KindFilter.Packs:
                    _sql.Append(" AND e.kind = 'Pack'");
                    break;
            }

            if (filter.OnlyWithCopies) _sql.Append(" AND e.copies > 1");

            if (filter.Ai != AiFilter.All)
                _sql.Append($" AND e.entry_id {(filter.Ai == AiFilter.Read ? "IN" : "NOT IN")} (SELECT entry_id FROM entry_ai)");

            if (plan.Formats.Count > 0) Add("d.format IN (SELECT value FROM json_each(@formats))", "formats", JsonSerializer.Serialize(plan.Formats));
            if (plan.ExcludedFormats.Count > 0)
                Add("d.format NOT IN (SELECT value FROM json_each(@excludedFormats))", "excludedFormats", JsonSerializer.Serialize(plan.ExcludedFormats));

            for (var i = 0; i < plan.Folders.Count; i++)
                Add($"coalesce(d.folder_hint, '') LIKE @folder{i} ESCAPE '\\'", $"folder{i}", Contains(plan.Folders[i]));
            for (var i = 0; i < plan.ExcludedFolders.Count; i++)
                Add($"coalesce(d.folder_hint, '') NOT LIKE @excludedFolder{i} ESCAPE '\\'", $"excludedFolder{i}", Contains(plan.ExcludedFolders[i]));

            // Only set when there are no words to find, so it never applies to page search (which then finds nothing).
            if (plan.TextExclude is { } textExclude)
                Add("e.entry_id NOT IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @textExclude)", "textExclude", textExclude);
            if (titleAsFilter && plan.FieldMatch is { } fields)
                Add("e.entry_id IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @fieldMatch)", "fieldMatch", fields);
            if (plan.FieldExclude is { } fieldExclude)
                Add("e.entry_id NOT IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @fieldExclude)", "fieldExclude", fieldExclude);

            for (var i = 0; i < plan.Facets.Count; i++) AddFacet(plan.Facets[i], i);
            for (var i = 0; i < plan.Levels.Count; i++) AddLevel(plan.Levels[i], i);

            if (filter.Systems is { Count: > 0 } systems) AddChoice("system", systems, "systems");
            if (filter.Types is { Count: > 0 } types) AddChoice("type", types, "types");
            if (filter.Level is { } level)
            {
                var known = "EXISTS (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND lm.level_state = 'known' AND lm.level_min <= @level AND lm.level_max >= @level)";
                Add(filter.IncludeUnknownLevel
                    ? $"({known} OR NOT EXISTS (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND lm.level_state <> 'unknown'))"
                    : known, "level", level);
            }
        }

        /// <summary>Entries with any of <paramref name="values"/> for <paramref name="field"/>; unknown picks those with none.</summary>
        void AddChoice(string field, IReadOnlyCollection<string> values, string name)
        {
            var known = values.Where(v => v != SearchQuery.Unknown).ToList();
            var clauses = new List<string>();
            if (known.Count > 0)
                clauses.Add($"e.entry_id IN (SELECT entry_id FROM entry_facet WHERE field = @{name}Field AND value IN (SELECT value FROM json_each(@{name})))");
            if (known.Count < values.Count)
                clauses.Add($"e.entry_id NOT IN (SELECT entry_id FROM entry_facet WHERE field = @{name}Field)");
            Add("(" + string.Join(" OR ", clauses) + ")", name + "Field", field);
            Parameters.Add(name, JsonSerializer.Serialize(known));
        }

        /// <summary>
        /// <c>type:adventure</c>: a value matches a term through any of its names (term_alias), its label, or its key;
        /// a prefix (<c>type:adv*</c>) through the start of its label or names.
        /// </summary>
        void AddFacet(FacetCondition facet, int i)
        {
            var fields = $"(SELECT value FROM json_each(@facetFields{i}))";
            Parameters.Add($"facetFields{i}", JsonSerializer.Serialize(facet.Fields));
            if (facet.IsUnknown)
            {
                _sql.Append(" AND e.entry_id ").Append(facet.Negated ? "IN" : "NOT IN")
                    .Append($" (SELECT entry_id FROM entry_facet WHERE field IN {fields})");
                return;
            }
            var normalized = MetadataText.Normalize(facet.Value);
            var matches = facet.Prefix
                ? $"f.label LIKE @facetLike{i} ESCAPE '\\' OR f.value IN (SELECT a.value FROM term_alias a WHERE a.vocabulary = f.field AND a.alias LIKE @facetLike{i} ESCAPE '\\')"
                : $"f.value = @facetKey{i} OR f.label LIKE @facetLike{i} ESCAPE '\\' OR f.value IN (SELECT a.value FROM term_alias a WHERE a.vocabulary = f.field AND a.alias = @facetAlias{i})";
            Add($"e.entry_id {(facet.Negated ? "NOT IN" : "IN")} (SELECT f.entry_id FROM entry_facet f WHERE f.field IN {fields} AND ({matches}))",
                $"facetLike{i}", facet.Prefix ? Escape(normalized) + "%" : Contains(facet.Value));
            Parameters.Add($"facetKey{i}", facet.Value.ToLowerInvariant());
            Parameters.Add($"facetAlias{i}", normalized);
        }

        /// <summary><c>level:3</c> and <c>level:2-4</c> match books whose range overlaps; none and unknown match those states.</summary>
        void AddLevel(LevelCondition level, int i)
        {
            string condition;
            if (level.Value == SearchQuery.Unknown) condition = "lm.level_state <> 'unknown'";
            else if (!LevelRange.TryParse(level.Value, out var range)) return;
            else if (range.NotApplicable) condition = "lm.level_state = 'na'";
            else
            {
                condition = $"lm.level_state = 'known' AND lm.level_min <= @levelMax{i} AND lm.level_max >= @levelMin{i}";
                Parameters.Add($"levelMin{i}", range.Min);
                Parameters.Add($"levelMax{i}", range.Max);
            }
            // Unknown is the absence of a known state, so level:unknown is "no known or n/a levels".
            var exists = level.Value == SearchQuery.Unknown ? !level.Negated : level.Negated;
            _sql.Append(" AND ").Append(exists ? "NOT EXISTS" : "EXISTS")
                .Append($" (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND {condition})");
        }

        /// <summary>One more clause, for a caller that needs to narrow the set further.</summary>
        public void Also(string clause, string name, object value) => Add(clause, name, value);

        void Add(string clause, string name, object value)
        {
            _sql.Append(" AND ").Append(clause);
            Parameters.Add(name, value);
        }

        /// <summary>A LIKE pattern for "contains", with LIKE's own wildcards escaped.</summary>
        static string Contains(string value) => "%" + Escape(value) + "%";

        static string Escape(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    }
}

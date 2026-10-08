using System.Globalization;
using System.Text;
using System.Text.Json;
using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>A document as the library grid and search results show it.</summary>
public sealed record LibraryEntry(
    long DocumentId, string Title, string Format, int? PageCount, string? Cover, string? FolderHint, DateTime AddedUtc, bool Searchable);

public enum LibrarySort
{
    /// <summary>Best match first; for a library with no search, the same as <see cref="RecentlyAdded"/>.</summary>
    Relevance,
    RecentlyAdded,
    Title,
}

/// <summary>Format choices in the filter panel. The format: field gives finer control.</summary>
public enum FormatFilter
{
    All,
    Pdf,
    Images,
}

/// <summary>
/// What the library shows: the documents in scope (from catalog.db: present in a folder the user hasn't removed,
/// optionally one folder), a format, and an order.
/// </summary>
public sealed record LibraryFilter(IReadOnlyCollection<long>? Scope = null, FormatFilter Format = FormatFilter.All, LibrarySort Sort = LibrarySort.Relevance);

/// <summary>A page that matched, with its indexed text quoted around the hits. Hits sit between <see cref="LibraryQueries.HitStart"/> and <see cref="LibraryQueries.HitEnd"/>.</summary>
public sealed record PageHit(long DocumentId, int PdfPage, string? Label, string Snippet, bool FromOcr);

public sealed record DocumentHits(LibraryEntry Document, IReadOnlyList<PageHit> Pages, long MatchingPages);

/// <summary>Inside-documents results: the best pages of the best books, and how many matched in all.</summary>
public sealed record PageResults(IReadOnlyList<DocumentHits> Documents, long MatchingDocuments, long MatchingPages)
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

    /// <summary>Column weights for doc_fts, in schema order: title, subtitle, publisher, series, tags, notes, confirmed, provisional.</summary>
    const string DocRank = "bm25(doc_fts, 10.0, 5.0, 2.0, 3.0, 3.0, 1.0, 4.0, 1.0)";

    const string EntryColumns = """
        d.document_id AS DocumentId, d.display_title AS Title, d.format AS Format, d.page_count AS PageCount, d.cover AS Cover,
        d.folder_hint AS FolderHint, d.added_utc AS AddedUtc,
        coalesce(s.status IN ('Complete', 'Partial', 'Skipped'), 0) AS Searchable
        """;

    const string EntryJoin = "LEFT JOIN stage_status s ON s.document_id = d.document_id AND s.stage = 'Text'";

    /// <summary>The whole library (or the part in scope) with the plan's fields and exclusions applied, in the filter's order.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> ListAsync(LibraryFilter filter, SearchPlan? plan = null, CancellationToken ct = default)
    {
        var where = new Where(filter, plan ?? new SearchPlan(), titleAsFilter: true);
        var sql = $"""
            SELECT {EntryColumns}
            FROM doc d {EntryJoin}
            WHERE {where.Sql}
            ORDER BY {Order(filter.Sort, relevance: null)}
            """;
        await using var connection = database.OpenRead();
        return Entries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)));
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
            FROM doc_fts JOIN doc d ON d.document_id = doc_fts.rowid {EntryJoin}
            WHERE doc_fts MATCH @match AND {where.Sql}
            ORDER BY {Order(filter.Sort, relevance: DocRank)}
            LIMIT @limit
            """;
        await using var connection = database.OpenRead();
        return Entries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)));
    }

    /// <summary>
    /// The Inside documents tab: pages whose text matches, grouped by book with the best books first, at most
    /// <paramref name="pagesPerDocument"/> pages each. Snippets are made only for the pages returned.
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
                SELECT m.doc, m.page_id,
                       row_number() OVER (PARTITION BY m.doc ORDER BY m.score, m.page_id) AS n,
                       min(m.score) OVER (PARTITION BY m.doc) AS best,
                       count(*) OVER (PARTITION BY m.doc) AS pages
                FROM matched m JOIN doc d ON d.document_id = m.doc
                WHERE {where.Sql}
            )
            SELECT doc AS Doc, page_id AS PageId, pages AS Pages FROM ranked WHERE n <= @perDocument ORDER BY best, doc, n
            """;

        await using var connection = database.OpenRead();
        var rows = (await connection.QueryAsync<(long Doc, long PageId, long Pages)>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return PageResults.None;

        // Rows arrive grouped by book, best book first.
        var books = new List<(long Doc, long Pages, List<long> PageIds)>();
        foreach (var (doc, pageId, pages) in rows)
        {
            if (books.Count == 0 || books[^1].Doc != doc) books.Add((doc, pages, []));
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

        var entries = (await GetEntriesAsync(connection, shown.Select(b => b.Doc), ct)).ToDictionary(e => e.DocumentId);
        var documents = shown
            .Where(b => entries.ContainsKey(b.Doc))
            .Select(b => new DocumentHits(
                entries[b.Doc],
                [.. b.PageIds.Where(hits.ContainsKey).Select(id => hits[id]).Select(h =>
                    new PageHit(h.DocumentId, (int)h.PdfPage, h.Label, h.Snippet, h.Source == "ocr"))],
                b.Pages))
            .ToList();
        return new PageResults(documents, books.Count, matchingPages);
    }

    /// <summary>Everything the inspector shows from index.db, or null for a document Probe hasn't reached.</summary>
    public async Task<DocumentDetails?> GetDetailsAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var entry = (await GetEntriesAsync(connection, [documentId], ct)).SingleOrDefault();
        if (entry is null) return null;

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

    static async Task<IReadOnlyList<LibraryEntry>> GetEntriesAsync(Microsoft.Data.Sqlite.SqliteConnection connection, IEnumerable<long> documentIds, CancellationToken ct) =>
        Entries(await connection.QueryAsync<EntryRow>(new CommandDefinition(
            $"""
            SELECT {EntryColumns}
            FROM doc d {EntryJoin}
            WHERE d.document_id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = JsonSerializer.Serialize(documentIds) }, cancellationToken: ct)));

    static string Order(LibrarySort sort, string? relevance) => sort switch
    {
        LibrarySort.Title => "d.display_title COLLATE NOCASE, d.document_id",
        LibrarySort.Relevance when relevance is not null => $"{relevance}, d.document_id",
        _ => "d.added_utc DESC, d.document_id DESC",
    };

    static List<LibraryEntry> Entries(IEnumerable<EntryRow> rows) =>
        [.. rows.Select(r => new LibraryEntry(r.DocumentId, r.Title, r.Format, (int?)r.PageCount, r.Cover, r.FolderHint,
            DateTime.Parse(r.AddedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            r.Searchable != 0))];

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
        public long DocumentId { get; init; }
        public string Title { get; init; } = "";
        public string Format { get; init; } = "";
        public long? PageCount { get; init; }
        public string? Cover { get; init; }
        public string? FolderHint { get; init; }
        public string AddedUtc { get; init; } = "";
        public long Searchable { get; init; }
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
    /// The WHERE clause shared by the library and both tabs, over <c>doc d</c>. Built from fixed fragments with
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
            if (filter.Scope is { } scope) Add("d.document_id IN (SELECT value FROM json_each(@scope))", "scope", JsonSerializer.Serialize(scope));

            switch (filter.Format)
            {
                case FormatFilter.Pdf:
                    Add("d.format = @formatPdf", "formatPdf", SourceFormats.Pdf);
                    break;
                case FormatFilter.Images:
                    Add("d.format IN (@formatJpeg, @formatPng)", "formatJpeg", SourceFormats.Jpeg);
                    Parameters.Add("formatPng", SourceFormats.Png);
                    break;
            }

            if (plan.Formats.Count > 0) Add("d.format IN (SELECT value FROM json_each(@formats))", "formats", JsonSerializer.Serialize(plan.Formats));
            if (plan.ExcludedFormats.Count > 0)
                Add("d.format NOT IN (SELECT value FROM json_each(@excludedFormats))", "excludedFormats", JsonSerializer.Serialize(plan.ExcludedFormats));

            for (var i = 0; i < plan.Folders.Count; i++)
                Add($"coalesce(d.folder_hint, '') LIKE @folder{i} ESCAPE '\\'", $"folder{i}", Contains(plan.Folders[i]));
            for (var i = 0; i < plan.ExcludedFolders.Count; i++)
                Add($"coalesce(d.folder_hint, '') NOT LIKE @excludedFolder{i} ESCAPE '\\'", $"excludedFolder{i}", Contains(plan.ExcludedFolders[i]));

            // Only set when there are no words to find, so it never applies to page search (which then finds nothing).
            if (plan.TextExclude is { } textExclude)
                Add("d.document_id NOT IN (SELECT rowid FROM doc_fts WHERE doc_fts MATCH @textExclude)", "textExclude", textExclude);
            if (titleAsFilter && plan.TitleMatch is { } title)
                Add("d.document_id IN (SELECT rowid FROM doc_fts WHERE doc_fts MATCH @titleMatch)", "titleMatch", title);
            if (plan.TitleExclude is { } titleExclude)
                Add("d.document_id NOT IN (SELECT rowid FROM doc_fts WHERE doc_fts MATCH @titleExclude)", "titleExclude", titleExclude);
        }

        void Add(string clause, string name, object value)
        {
            _sql.Append(" AND ").Append(clause);
            Parameters.Add(name, value);
        }

        /// <summary>A LIKE pattern for "contains", with LIKE's own wildcards escaped.</summary>
        static string Contains(string value) =>
            "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
    }
}

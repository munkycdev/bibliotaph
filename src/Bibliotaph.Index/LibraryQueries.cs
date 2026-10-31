using System.Globalization;
using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Dapper;
using static Bibliotaph.Index.LibraryRows;
using static Bibliotaph.Index.LibrarySql;

namespace Bibliotaph.Index;

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

    /// <summary>
    /// The whole library (or the part in scope) with the plan's fields and exclusions applied, in the filter's order;
    /// the first <paramref name="limit"/> of them when there is one, as Home shows.
    /// </summary>
    public async Task<IReadOnlyList<LibraryEntry>> ListAsync(LibraryFilter filter, SearchPlan? plan = null, int? limit = null, CancellationToken ct = default)
    {
        plan ??= new SearchPlan();
        var where = new LibraryWhere(filter, plan, titleAsFilter: true);
        if (limit is { } count) where.Parameters.Add("limit", count);
        var sql = Found(null, EntryColumns, $"{Entries(filter.PackFiles)} {EntryJoin}", where.Sql, Order(filter.Sort, relevance: null), limit is not null,
            filesGiveWay: filter.PartsInBooks && !plan.IsEmpty);
        await using var connection = database.OpenRead();
        return ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)), filter.PartsInBooks);
    }

    /// <summary>
    /// The Documents tab: titles and metadata, and the names of the files in packs. A pack found by a file's name
    /// says which (the best match). With no words or title: to find, this is the filtered library. While each file in a
    /// pack has a card of its own, a file is found by its own name or by its pack's.
    /// </summary>
    public async Task<IReadOnlyList<LibraryEntry>> SearchDocumentsAsync(SearchPlan plan, LibraryFilter filter, int limit = 1000, CancellationToken ct = default)
    {
        if (plan.DocumentMatch is not { } match) return await ListAsync(filter, plan, limit, ct);

        var where = new LibraryWhere(filter, plan, titleAsFilter: false);
        where.Parameters.Add("match", match);
        where.Parameters.Add("limit", limit);
        // Image names answer plain words only; title: and the other fields are about the card.
        var members = plan.FieldMatch is null ? $"""
            UNION ALL
            SELECT em.entry_id, {MemberRank}, em.name, em.document_id
            FROM member_fts JOIN entry_member em ON em.member_entry_id = member_fts.rowid
            WHERE member_fts MATCH @match
            """ : "";
        var hits = $"""
            hit AS MATERIALIZED (
                SELECT rowid AS entry_id, {DocRank} AS rank, NULL AS member, NULL AS member_document FROM entry_fts WHERE entry_fts MATCH @match
                {members})
            """;
        if (filter.PackFiles)
        {
            // A pack's own hits find all its files, a file's name only that file; each card keeps its best hit.
            var fileSql = Found(
                $"""
                {hits},
                card AS (
                    SELECT c.*, h.rank, row_number() OVER (PARTITION BY coalesce(c.pack_file, c.entry_id) ORDER BY h.rank) AS best
                    FROM {Cards(true)} c JOIN hit h ON h.entry_id = c.entry_id AND (h.member_document IS NULL OR h.member_document = c.document_id))
                """,
                $"{EntryColumns}, NULL AS MatchedMember, NULL AS MatchedDocumentId",
                $"(SELECT * FROM card WHERE best = 1) e LEFT JOIN doc d ON d.document_id = e.document_id {EntryJoin}",
                where.Sql, Order(filter.Sort, relevance: "e.rank"), limit: true, filesGiveWay: filter.PartsInBooks);
            await using var files = database.OpenRead();
            return ToEntries(await files.QueryAsync<EntryRow>(new CommandDefinition(fileSql, where.Parameters, cancellationToken: ct)), filter.PartsInBooks);
        }

        // One row per card, its best hit: SQLite takes the bare columns from the row min() picks. The hits are
        // materialized, since bm25() can't run once flattened into the grouping.
        var sql = Found(
            hits,
            $"{EntryColumns}, h.member AS MatchedMember, h.member_document AS MatchedDocumentId",
            $"""
            (SELECT entry_id, min(rank) AS rank, member, member_document FROM hit GROUP BY entry_id) h
            JOIN {Cards(false)} e ON e.entry_id = h.entry_id LEFT JOIN doc d ON d.document_id = e.document_id {EntryJoin}
            """,
            where.Sql, Order(filter.Sort, relevance: "h.rank"), limit: true, filesGiveWay: filter.PartsInBooks);
        await using var connection = database.OpenRead();
        return ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct)), filter.PartsInBooks);
    }

    /// <summary>
    /// How many entries have each value of a vocabulary field (<c>system</c>, <c>type</c>), among those the library
    /// shows for <paramref name="filter"/> and <paramref name="plan"/>, in name order, then those with none (<see cref="SearchQuery.Unknown"/>).
    /// To count the choices within a dimension, pass a filter without that dimension's own choices. <paramref
    /// name="unknown"/> names those with none.
    /// </summary>
    public async Task<IReadOnlyList<FacetCount>> GetFacetCountsAsync(string field, LibraryFilter filter, SearchPlan? plan = null, string unknown = "Unknown",
        CancellationToken ct = default)
    {
        var where = CountWhere(filter, plan);
        where.Parameters.Add("countField", field);
        await using var connection = database.OpenRead();
        var counts = (await connection.QueryAsync<(string Value, string Label, long Count)>(new CommandDefinition(
            $"""
            SELECT f.value, min(f.label), count(*)
            FROM {Entries(filter.PackFiles)} JOIN entry_facet f ON f.entry_id = e.entry_id AND f.field = @countField
            WHERE {where.Sql}
            GROUP BY f.value
            ORDER BY min(f.label) COLLATE NOCASE
            """, where.Parameters, cancellationToken: ct))).Select(c => new FacetCount(c.Value, c.Label, c.Count)).ToList();
        var none = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"""
            SELECT count(*) FROM {Entries(filter.PackFiles)}
            WHERE {where.Sql} AND e.entry_id NOT IN (SELECT entry_id FROM entry_facet WHERE field = @countField)
            """, where.Parameters, cancellationToken: ct));
        if (none > 0) counts.Add(new FacetCount(SearchQuery.Unknown, unknown, none));
        return counts;
    }

    /// <summary>
    /// The values of a field the Library filters by typing (setting, theme, environment, edition, publisher), each with
    /// how many of the entries shown for <paramref name="filter"/> and <paramref name="plan"/> have it, most first: what
    /// its box offers. Terms come with their other names to type; publishers are free text, counted by name. To count
    /// a box's values beside its own picks, pass a filter without them.
    /// </summary>
    public async Task<IReadOnlyList<ValueCount>> GetValueCountsAsync(string field, LibraryFilter filter, SearchPlan? plan = null, CancellationToken ct = default)
    {
        var where = CountWhere(filter, plan);
        where.Parameters.Add("countField", field);
        where.Parameters.Add("countVocabulary", MetadataFields.Find(field)?.Vocabulary ?? field);
        var sql = field == MetadataFields.Publisher.Key
            ? $"""
                SELECT m.publisher, m.publisher, count(*), ''
                FROM {Entries(filter.PackFiles)} JOIN entry_meta m ON m.entry_id = e.entry_id
                WHERE {where.Sql} AND m.publisher IS NOT NULL
                GROUP BY m.publisher
                ORDER BY count(*) DESC, m.publisher COLLATE NOCASE
                """
            : $"""
                SELECT f.value, min(f.label), count(*),
                       coalesce((SELECT group_concat(a.alias, ' · ') FROM term_alias a WHERE a.vocabulary = @countVocabulary AND a.value = f.value), '')
                FROM {Entries(filter.PackFiles)} JOIN entry_facet f ON f.entry_id = e.entry_id AND f.field = @countField
                WHERE {where.Sql}
                GROUP BY f.value
                ORDER BY count(*) DESC, min(f.label) COLLATE NOCASE
                """;
        await using var connection = database.OpenRead();
        var counts = await connection.QueryAsync<(string Value, string Label, long Count, string Names)>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct));
        return [.. counts.Select(c => new ValueCount(c.Value, c.Label, c.Count, c.Names))];
    }

    /// <summary>
    /// The entries a count covers: those the library shows, found as the Documents tab finds them, packs by their
    /// images' names too, and a file on a card of its own by its own name.
    /// </summary>
    static LibraryWhere CountWhere(LibraryFilter filter, SearchPlan? plan)
    {
        plan ??= new SearchPlan();
        var where = new LibraryWhere(filter, plan, titleAsFilter: true);
        if (plan.TextMatch is { } match)
            where.Also(filter.PackFiles
                ? """
                    (e.entry_id IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @countMatch)
                        OR e.pack_file IN (SELECT rowid FROM member_fts WHERE member_fts MATCH @countMatch))
                    """
                : """
                    e.entry_id IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @countMatch
                        UNION SELECT em.entry_id FROM member_fts JOIN entry_member em ON em.member_entry_id = member_fts.rowid WHERE member_fts MATCH @countMatch)
                    """, "countMatch", match);
        return where;
    }

    /// <summary>
    /// How many entries have each format (pdf, jpg, png, webp), most first, among those in <paramref name="filter"/>'s
    /// scope: the values the search box's field guide offers after <c>format:</c>. <see cref="FacetCount.Label"/> is the
    /// format in capitals.
    /// </summary>
    public async Task<IReadOnlyList<FacetCount>> GetFormatCountsAsync(LibraryFilter filter, CancellationToken ct = default)
    {
        var where = new LibraryWhere(filter, new SearchPlan(), titleAsFilter: true);
        await using var connection = database.OpenRead();
        var counts = await connection.QueryAsync<(string Format, long Count)>(new CommandDefinition(
            $"""
            SELECT d.format, count(*)
            FROM {Entries(filter.PackFiles)}
            WHERE {where.Sql} AND d.format IS NOT NULL
            GROUP BY d.format
            ORDER BY count(*) DESC, d.format
            """, where.Parameters, cancellationToken: ct));
        return [.. counts.Select(c => new FacetCount(c.Format, c.Format.ToUpperInvariant(), c.Count))];
    }

    /// <summary>
    /// How many cards in the library (or the part in <paramref name="scope"/>) each group whose name starts with
    /// <paramref name="prefix"/> holds, such as every collection's, for the collection cards.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> CountGroupsAsync(string prefix, IReadOnlyCollection<EntryId>? scope, CancellationToken ct = default)
    {
        var where = new LibraryWhere(new LibraryFilter(scope), new SearchPlan(), titleAsFilter: true);
        where.Parameters.Add("prefix", prefix + "%");
        var sql = $"""
            SELECT es.scope AS Scope, count(*) AS Count
            FROM entry_scope es JOIN entry_doc e ON e.entry_id = es.entry_id LEFT JOIN doc d ON d.document_id = e.document_id
            WHERE es.scope LIKE @prefix AND {where.Sql}
            GROUP BY es.scope
            """;
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(string Scope, long Count)>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct));
        return rows.ToDictionary(r => r.Scope, r => r.Count);
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
    /// How many adventures inside files there are, so the Library offers to hide them once there are some.
    /// </summary>
    public async Task<long> CountPartsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM entry_doc WHERE kind = 'Part'", cancellationToken: ct));
    }

    /// <summary>
    /// The Inside documents tab: pages whose text matches, grouped by book with the best books first, at most <paramref
    /// name="pagesPerDocument"/> pages each. Only pages of the document each entry shows count, so other copies of a
    /// book don't repeat its hits. A page inside an adventure marked in a file goes with the adventure, and with the
    /// file only when the adventure is filtered out. A page of a PDF in a pack goes with the pack, its card. Snippets are made only for the pages returned. Exclusions alone
    /// find no pages, so a query like <c>-maps</c> returns nothing here.
    /// </summary>
    public async Task<PageResults> SearchPagesAsync(
        SearchPlan plan, LibraryFilter filter, int maxDocuments = 100, int pagesPerDocument = 3, CancellationToken ct = default)
    {
        if (plan.TextMatch is not { } match) return PageResults.None;
        var where = new LibraryWhere(filter, plan, titleAsFilter: true);
        where.Parameters.Add("match", match);
        const string matched = """
            SELECT p.document_id AS doc, p.pdf_page AS pdf_page, p.id AS page_id, page_fts.rank AS score
            FROM page_fts JOIN page p ON p.id = page_fts.rowid
            WHERE page_fts MATCH @match
            """;
        return await PagesAsync(matched, where, filter, maxDocuments, pagesPerDocument, async (connection, ids) =>
            (await connection.QueryAsync<HitRow>(new CommandDefinition(
                """
                SELECT p.id AS PageId, p.document_id AS DocumentId, p.pdf_page AS PdfPage, p.label AS Label, p.text_source AS Source,
                       snippet(page_fts, 0, @start, @end, '…', @tokens) AS Snippet
                FROM page_fts JOIN page p ON p.id = page_fts.rowid
                WHERE page_fts MATCH @match AND page_fts.rowid IN (SELECT value FROM json_each(@ids))
                """,
                new { match, start = HitStart.ToString(), end = HitEnd.ToString(), tokens = SnippetTokens, ids = JsonSerializer.Serialize(ids) },
                cancellationToken: ct))).ToList(), ct);
    }

    /// <summary>
    /// The Inside documents tab for pages ranked outside the database (by meaning and words, for <c>about:</c>): the same
    /// cards, filters and grouping as <see cref="SearchPagesAsync"/>, with <paramref name="ranked"/>'s order standing in
    /// for bm25, so a book's place is its best page's. Each page's snippet is the one given, marked as search marks its hits.
    /// </summary>
    public async Task<PageResults> RankedPagesAsync(
        IReadOnlyList<RankedPageHit> ranked, SearchPlan plan, LibraryFilter filter, int maxDocuments = 100, int pagesPerDocument = 3,
        CancellationToken ct = default)
    {
        if (ranked.Count == 0) return PageResults.None;
        var where = new LibraryWhere(filter, plan, titleAsFilter: true);
        where.Parameters.Add("ranked", JsonSerializer.Serialize(ranked.Select(r => r.PageId)));
        const string matched = """
            SELECT p.document_id AS doc, p.pdf_page AS pdf_page, p.id AS page_id, r.key AS score
            FROM json_each(@ranked) r JOIN page p ON p.id = r.value
            """;
        var snippets = ranked.GroupBy(r => r.PageId).ToDictionary(g => g.Key, g => g.First().Snippet);
        return await PagesAsync(matched, where, filter, maxDocuments, pagesPerDocument, async (connection, ids) =>
            [.. (await connection.QueryAsync<HitRow>(new CommandDefinition(
                """
                SELECT p.id AS PageId, p.document_id AS DocumentId, p.pdf_page AS PdfPage, p.label AS Label, p.text_source AS Source
                FROM page p WHERE p.id IN (SELECT value FROM json_each(@ids))
                """,
                new { ids = JsonSerializer.Serialize(ids) }, cancellationToken: ct)))
                .Select(h => new HitRow
                {
                    PageId = h.PageId, DocumentId = h.DocumentId, PdfPage = h.PdfPage, Label = h.Label, Source = h.Source,
                    Snippet = snippets.GetValueOrDefault(h.PageId) ?? "",
                })], ct);
    }

    /// <summary>
    /// Both page tabs' grouping: the <paramref name="matched"/> pages (doc, pdf_page, page_id and a score, lower is better)
    /// given to the cards that show them, at most <paramref name="pagesPerDocument"/> a card, best card first, with the
    /// hits <paramref name="hits"/> reads for the pages returned.
    /// </summary>
    async Task<PageResults> PagesAsync(string matched, LibraryWhere where, LibraryFilter filter, int maxDocuments, int pagesPerDocument,
        Func<Microsoft.Data.Sqlite.SqliteConnection, IEnumerable<long>, Task<List<HitRow>>> hits, CancellationToken ct)
    {
        where.Parameters.Add("perDocument", pagesPerDocument);
        // While each file has a card of its own, its pages are its own card's; otherwise they are the pack's.
        var inPacks = filter.PackFiles ? "" : $"""
            UNION ALL
            SELECT e.entry_id, e.kind, m.page_id, m.score
            FROM matched m
            JOIN entry_member em ON em.document_id = m.doc
            JOIN {Cards(false)} e ON e.entry_id = em.entry_id
            JOIN doc d ON d.document_id = e.document_id
            WHERE {where.Sql}
            """;
        var sql = $"""
            WITH matched AS (
                {matched}
            ),
            owned AS (
                SELECT coalesce(e.pack_file, e.entry_id) AS entry, e.kind AS kind, m.page_id, m.score
                FROM matched m
                JOIN {Cards(filter.PackFiles)} e ON e.document_id = m.doc AND e.kind <> 'Pack' AND (e.kind <> 'Part' OR m.pdf_page BETWEEN e.first_page AND e.last_page)
                JOIN doc d ON d.document_id = e.document_id
                WHERE {where.Sql}
                {inPacks}
            ),
            claimed AS (
                SELECT entry, page_id, score,
                       row_number() OVER (PARTITION BY page_id ORDER BY kind <> 'Part', entry) AS k
                FROM owned
            ),
            ranked AS (
                SELECT entry, page_id,
                       row_number() OVER (PARTITION BY entry ORDER BY score, page_id) AS n,
                       min(score) OVER (PARTITION BY entry) AS best,
                       count(*) OVER (PARTITION BY entry) AS pages
                FROM claimed WHERE k = 1
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

        var found = (await hits(connection, shown.SelectMany(b => b.PageIds))).ToDictionary(h => h.PageId);
        var entries = (await GetEntriesAsync(connection, shown.Select(b => b.Entry), ct, filter.PartsInBooks, filter.PackFiles)).ToDictionary(e => e.CardId.Value);
        var results = shown
            .Where(b => entries.ContainsKey(b.Entry))
            .Select(b => new EntryHits(
                entries[b.Entry],
                [.. b.PageIds.Where(found.ContainsKey).Select(id => found[id]).Select(h =>
                    new PageHit(new EntryId(b.Entry), h.DocumentId, (int)h.PdfPage, h.Label, h.Snippet, h.Source == "ocr"))],
                b.Pages))
            .ToList();
        return new PageResults(results, books.Count, matchingPages);
    }

    /// <summary>
    /// The pages a question to the library may read among the cards the library shows for <paramref name="filter"/> and
    /// <paramref name="plan"/>: each PDF's or e-book's pages, or an adventure's own pages in its file. Images, packs and books owned
    /// elsewhere have no pages to read. A file gives way to its found adventure as it does in the Library.
    /// </summary>
    public async Task<IReadOnlyList<PageSpan>> GetPageSpansAsync(LibraryFilter filter, SearchPlan plan, CancellationToken ct = default)
    {
        var where = new LibraryWhere(filter, plan, titleAsFilter: true);
        where.Parameters.Add("spanFormats", JsonSerializer.Serialize(SourceFormats.Paged));
        var giveWay = filter.PartsInBooks && !plan.IsEmpty
            ? "WHERE NOT EXISTS (SELECT 1 FROM found c WHERE c.Kind = 'Part' AND c.ParentId = f.EntryId)"
            : "";
        var sql = $"""
            WITH found AS MATERIALIZED (
                SELECT e.entry_id AS EntryId, e.parent_id AS ParentId, e.document_id AS DocumentId, e.kind AS Kind,
                    e.first_page AS FirstPage, e.last_page AS LastPage
                FROM {Entries(filter.PackFiles)}
                WHERE {where.Sql} AND e.document_id IS NOT NULL AND e.kind <> 'Pack' AND d.format IN (SELECT value FROM json_each(@spanFormats)))
            SELECT f.DocumentId, f.Kind, f.FirstPage, f.LastPage FROM found f {giveWay}
            """;
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, string Kind, long? FirstPage, long? LastPage)>(new CommandDefinition(sql, where.Parameters, cancellationToken: ct));
        return [.. rows.Select(r => r.Kind == nameof(EntryKind.Part) && r.FirstPage is { } first && r.LastPage is { } last
            ? new PageSpan(r.DocumentId, (int)first, (int)last)
            : new PageSpan(r.DocumentId))];
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
        if (entry.IsElsewhere) return new DocumentDetails(entry, false, true, null, null, null, null, null, 0, 0, []);
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

        var identifiers = await connection.QueryAsync<(string Kind, string Printed, long? PdfPage, string? Label)>(new CommandDefinition(
            """
            SELECT i.kind, i.printed, i.pdf_page, p.label
            FROM doc_identifier i LEFT JOIN page p ON p.document_id = i.document_id AND p.pdf_page = i.pdf_page
            WHERE i.document_id = @documentId
            ORDER BY i.pdf_page IS NOT NULL, i.pdf_page, i.kind
            """, new { documentId }, cancellationToken: ct));

        return new DocumentDetails(entry, encrypted != 0, canCopy != 0, metaTitle, metaAuthor, metaSubject,
            (int?)widthPx, (int?)heightPx, ocrPages, awaiting,
            [.. stages.Select(s => new StageState(Enum.Parse<Stage>(s.Stage), Enum.Parse<StageStatus>(s.Status), s.Reason))
                .OrderBy(s => s.Stage)]) // the enum is in pipeline order
        {
            Identifiers = [.. identifiers.Select(i => new PrintedIdentifier(Enum.Parse<IdentifierKind>(i.Kind), i.Printed,
                i.PdfPage is { } page ? i.Label ?? (page + 1).ToString(CultureInfo.InvariantCulture) : null))],
        };
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

    /// <summary>
    /// Documents as cards of their own, by id: for a session item whose book has no card in the Library, such as an
    /// image hidden in a pack, which still shows and opens in the pack.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, LibraryEntry>> GetDocumentCardsAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new Dictionary<long, LibraryEntry>();
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, string Title, string Format, long? PageCount, string? Cover, string? FolderHint, string AddedUtc)>(
            new CommandDefinition(
                "SELECT document_id, display_title, format, page_count, cover, folder_hint, added_utc FROM doc WHERE document_id IN @ids",
                new { ids = documentIds.Distinct().ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.DocumentId, r => new LibraryEntry(default, r.DocumentId, r.Title, r.Format, (int?)r.PageCount, r.Cover, r.FolderHint,
            DateTime.Parse(r.AddedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), Searchable: true));
    }

    /// <summary>A pack's files in file name order, for its inspector grid and for stepping through it in the viewer.</summary>
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

    /// <summary>
    /// Cards by <see cref="LibraryEntry.CardId"/>: entries, or with <paramref name="packFiles"/> also files in packs by
    /// their own entries.
    /// </summary>
    static async Task<IReadOnlyList<LibraryEntry>> GetEntriesAsync(Microsoft.Data.Sqlite.SqliteConnection connection, IEnumerable<long> entryIds, CancellationToken ct,
        bool partsInBooks = false, bool packFiles = false) =>
        ToEntries(await connection.QueryAsync<EntryRow>(new CommandDefinition(
            $"""
            SELECT {EntryColumns}
            FROM {Entries(packFiles)} {EntryJoin}
            WHERE coalesce(e.pack_file, e.entry_id) IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = JsonSerializer.Serialize(entryIds) }, cancellationToken: ct)), partsInBooks);
}

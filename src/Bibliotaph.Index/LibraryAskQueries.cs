using System.Text.Json;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>Pages of one document a question to the library may read: all of them, or PDF pages <see cref="First"/> to <see cref="Last"/>.</summary>
public sealed record PageSpan(long DocumentId, int? First = null, int? Last = null)
{
    /// <summary>The whole document.</summary>
    public bool IsWhole => First is null && Last is null;

    public bool Contains(int pdfPage) => pdfPage >= (First ?? 0) && pdfPage <= (Last ?? int.MaxValue);
}

/// <summary>
/// Which pages a question to the library reads: every page, or the pages in some spans. Each filter (a book's system
/// or type, a page range of an adventure, and later a chapter's tags) gives a scope, and the question reads the pages
/// all of them allow (<see cref="Intersect"/>). A whole document takes in any range of it.
/// </summary>
public sealed class PageScope
{
    readonly Dictionary<long, List<PageSpan>>? _byDocument;
    readonly HashSet<long> _excluded;

    PageScope(Dictionary<long, List<PageSpan>>? byDocument, HashSet<long>? excluded = null)
    {
        _byDocument = byDocument;
        _excluded = excluded ?? [];
    }

    /// <summary>Every page in the library.</summary>
    public static PageScope Everything { get; } = new(null);

    /// <summary>The pages in these spans; none when there are none.</summary>
    public static PageScope Of(IEnumerable<PageSpan> spans)
    {
        var byDocument = new Dictionary<long, List<PageSpan>>();
        foreach (var span in spans)
        {
            if (!byDocument.TryGetValue(span.DocumentId, out var list)) byDocument[span.DocumentId] = list = [];
            if (list.Count == 1 && list[0].IsWhole) continue;
            if (span.IsWhole)
            {
                list.Clear();
                list.Add(span);
            }
            else list.Add(span);
        }
        return new PageScope(byDocument);
    }

    /// <summary>Every page, with no narrowing.</summary>
    public bool IsEverything => _byDocument is null && _excluded.Count == 0;

    public bool Contains(long documentId, int pdfPage) =>
        !_excluded.Contains(documentId) && (_byDocument is null || (_byDocument.TryGetValue(documentId, out var spans) && spans.Any(s => s.Contains(pdfPage))));

    /// <summary>The same pages without any of these documents': the books in a library folder kept from AI, say.</summary>
    public PageScope Without(IEnumerable<long> documentIds) => new(_byDocument, [.. _excluded, .. documentIds]);

    /// <summary>The pages both scopes allow.</summary>
    public PageScope Intersect(PageScope other)
    {
        HashSet<long> excluded = [.. _excluded, .. other._excluded];
        if (_byDocument is null) return new PageScope(other._byDocument, excluded);
        if (other._byDocument is null) return new PageScope(_byDocument, excluded);
        var spans = new List<PageSpan>();
        foreach (var (document, mine) in _byDocument)
        {
            if (!other._byDocument.TryGetValue(document, out var theirs)) continue;
            foreach (var a in mine)
                foreach (var b in theirs)
                {
                    var first = Math.Max(a.First ?? 0, b.First ?? 0);
                    var last = Math.Min(a.Last ?? int.MaxValue, b.Last ?? int.MaxValue);
                    if (first > last) continue;
                    spans.Add(new PageSpan(document, first == 0 && a.First is null && b.First is null ? null : first,
                        last == int.MaxValue ? null : last));
                }
        }
        return Of(spans).Without(excluded);
    }

    /// <summary>
    /// A SQL condition on page <paramref name="alias"/> that keeps its pages in scope, with the parameters it binds: the
    /// documents left out as one JSON list, the whole documents as another, the ranges as a third of [document, first,
    /// last].
    /// </summary>
    internal string Condition(string alias, DynamicParameters parameters)
    {
        var excluded = "1";
        if (_excluded.Count > 0)
        {
            parameters.Add("scopeExcluded", JsonSerializer.Serialize(_excluded));
            excluded = $"{alias}.document_id NOT IN (SELECT value FROM json_each(@scopeExcluded))";
        }
        if (_byDocument is null) return excluded;
        var spans = _byDocument.Values.SelectMany(s => s).ToList();
        parameters.Add("scopeWhole", JsonSerializer.Serialize(spans.Where(s => s.IsWhole).Select(s => s.DocumentId)));
        parameters.Add("scopeRanges", JsonSerializer.Serialize(spans.Where(s => !s.IsWhole).Select(s => new[] { s.DocumentId, s.First ?? 0, s.Last ?? int.MaxValue })));
        return $"""
            {excluded} AND ({alias}.document_id IN (SELECT value FROM json_each(@scopeWhole))
             OR EXISTS (SELECT 1 FROM json_each(@scopeRanges) r WHERE json_extract(r.value, '$[0]') = {alias}.document_id
                        AND {alias}.pdf_page BETWEEN json_extract(r.value, '$[1]') AND json_extract(r.value, '$[2]')))
            """;
    }
}

/// <summary>A page a question's words found anywhere in the library, with its bm25 rank (lower is better).</summary>
public sealed record LibraryWordHit(long PageId, long DocumentId, int PdfPage, double Rank);

/// <summary>A page of the library, with its document, PDF page, printed label and indexed text.</summary>
public sealed record LibraryPageRow(long PageId, long DocumentId, int PdfPage, string? Label, string Text);

/// <summary>A page's vector by the embedding model asked for, with its document and PDF page.</summary>
public sealed record LibraryPageVector(long PageId, long DocumentId, int PdfPage, float[] Vector);

/// <summary>How much of what a question to the library reads has text, and how much of that has vectors: books and pages.</summary>
public sealed record LibraryCoverage(int Books, int PagesWithText, int PagesWithVectors);

/// <summary>
/// What a question to the whole library reads from index.db: pages by the question's words and page vectors, within a
/// <see cref="PageScope"/>, how much has text and vectors, and which book's vectors to make next. Only the documents the
/// library's cards show count, so another copy of a book doesn't repeat its pages. Every value is a bound parameter;
/// FTS5 expressions come from AskRetrieval, which quotes every word.
/// </summary>
public sealed class LibraryAskQueries(IndexDatabase database)
{
    /// <summary>Pages of the documents cards show: the library, without other copies of a book or forgotten text.</summary>
    const string Shown = "p.document_id IN (SELECT document_id FROM entry_doc WHERE document_id IS NOT NULL)";

    /// <summary>The pages in scope that have any of the words, best first by bm25, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<LibraryWordHit>> FindWordsAsync(string match, PageScope scope, int limit, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters(new { match, limit });
        var sql = $"""
            SELECT p.id, p.document_id, p.pdf_page, page_fts.rank
            FROM page_fts JOIN page p ON p.id = page_fts.rowid
            WHERE page_fts MATCH @match AND {Shown} AND {scope.Condition("p", parameters)}
            ORDER BY page_fts.rank, p.id
            LIMIT @limit
            """;
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long DocumentId, long PdfPage, double Rank)>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return [.. rows.Select(r => new LibraryWordHit(r.PageId, r.DocumentId, (int)r.PdfPage, r.Rank))];
    }

    /// <summary>For each FTS5 expression (one word of a question each), how many pages in scope it finds: how rare the word is there.</summary>
    public async Task<IReadOnlyList<int>> CountPagesAsync(IReadOnlyList<string> matches, PageScope scope, CancellationToken ct = default)
    {
        if (matches.Count == 0) return [];
        await using var connection = database.OpenRead();
        var counts = new List<int>(matches.Count);
        foreach (var match in matches)
        {
            var parameters = new DynamicParameters(new { match });
            counts.Add((int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"""
                SELECT count(*) FROM page_fts JOIN page p ON p.id = page_fts.rowid
                WHERE page_fts MATCH @match AND {Shown} AND {scope.Condition("p", parameters)}
                """, parameters, cancellationToken: ct)));
        }
        return counts;
    }

    /// <summary>These pages, with their documents, labels and text.</summary>
    public async Task<IReadOnlyList<LibraryPageRow>> GetPagesAsync(IReadOnlyCollection<long> pageIds, CancellationToken ct = default)
    {
        if (pageIds.Count == 0) return [];
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long DocumentId, long PdfPage, string? Label, string Text)>(new CommandDefinition(
            "SELECT id, document_id, pdf_page, label, text FROM page WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = JsonSerializer.Serialize(pageIds) }, cancellationToken: ct));
        return [.. rows.Select(r => new LibraryPageRow(r.PageId, r.DocumentId, (int)r.PdfPage, r.Label, r.Text))];
    }

    /// <summary>
    /// The vectors <paramref name="model"/> made of the pages in scope, at this text version, one at a time as they are
    /// read: a library's vectors are too many to hold at once.
    /// </summary>
    public async IAsyncEnumerable<LibraryPageVector> ReadVectorsAsync(string model, int version, PageScope scope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new DynamicParameters(new { model, version });
        var sql = $"""
            SELECT v.page_id, p.document_id, p.pdf_page, v.vector
            FROM page_vec v JOIN page p ON p.id = v.page_id
            WHERE v.model = @model AND v.version = @version AND {Shown} AND {scope.Condition("p", parameters)}
            """;
        await using var connection = database.OpenRead();
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        while (await reader.ReadAsync(ct))
            yield return new LibraryPageVector(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), VectorBlob.FromBytes((byte[])reader.GetValue(3)));
    }

    /// <summary>The vectors <paramref name="model"/> made of these pages at this text version; pages without one are left out.</summary>
    public async Task<IReadOnlyList<LibraryPageVector>> GetVectorsAsync(string model, int version, IReadOnlyCollection<long> pageIds, CancellationToken ct = default)
    {
        if (pageIds.Count == 0) return [];
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long DocumentId, long PdfPage, byte[] Vector)>(new CommandDefinition(
            """
            SELECT v.page_id, p.document_id, p.pdf_page, v.vector
            FROM page_vec v JOIN page p ON p.id = v.page_id
            WHERE v.model = @model AND v.version = @version AND v.page_id IN (SELECT value FROM json_each(@ids))
            """, new { model, version, ids = JsonSerializer.Serialize(pageIds) }, cancellationToken: ct));
        return [.. rows.Select(r => new LibraryPageVector(r.PageId, r.DocumentId, (int)r.PdfPage, VectorBlob.FromBytes(r.Vector)))];
    }

    /// <summary>How many books in scope have text, and how many of their pages have text and a vector from this model and version.</summary>
    public async Task<LibraryCoverage> GetCoverageAsync(string? model, int version, PageScope scope, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters(new { model = model ?? "", version });
        await using var connection = database.OpenRead();
        var (books, withText, withVectors) = await connection.QuerySingleAsync<(long, long, long)>(new CommandDefinition(
            $"""
            SELECT count(DISTINCT p.document_id), count(*),
                   coalesce(sum(EXISTS (SELECT 1 FROM page_vec v WHERE v.page_id = p.id AND v.model = @model AND v.version = @version)), 0)
            FROM page p WHERE p.text <> '' AND {Shown} AND {scope.Condition("p", parameters)}
            """, parameters, cancellationToken: ct));
        return new LibraryCoverage((int)books, (int)withText, (int)withVectors);
    }

    /// <summary>
    /// The next document after <paramref name="after"/>, in id order, that the library shows and has pages with text and
    /// no vector from this model and version, leaving out <paramref name="skipped"/>; null when there is none.
    /// </summary>
    public async Task<long?> NextWithoutVectorsAsync(string model, int version, long after, IReadOnlyCollection<long> skipped, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            $"""
            SELECT p.document_id FROM page p
            WHERE p.document_id > @after AND p.text <> '' AND {Shown}
              AND p.document_id NOT IN (SELECT value FROM json_each(@skipped))
              AND NOT EXISTS (SELECT 1 FROM page_vec v WHERE v.page_id = p.id AND v.model = @model AND v.version = @version)
            ORDER BY p.document_id
            LIMIT 1
            """, new { model, version, after, skipped = JsonSerializer.Serialize(skipped) }, cancellationToken: ct));
    }

    /// <summary>
    /// How many pages with text the library's books have, leaving out <paramref name="skipped"/>, and how many of them
    /// have a vector from this model and version: how far reading the library for meaning has got.
    /// </summary>
    public async Task<(int Pages, int WithVectors)> GetVectorProgressAsync(string model, int version, IReadOnlyCollection<long> skipped, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var (pages, withVectors) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            $"""
            SELECT count(*), coalesce(sum(EXISTS (SELECT 1 FROM page_vec v WHERE v.page_id = p.id AND v.model = @model AND v.version = @version)), 0)
            FROM page p
            WHERE p.text <> '' AND {Shown} AND p.document_id NOT IN (SELECT value FROM json_each(@skipped))
            """, new { model, version, skipped = JsonSerializer.Serialize(skipped) }, cancellationToken: ct));
        return ((int)pages, (int)withVectors);
    }

    /// <summary>The title each document is listed under: its card's, or an adventure's file's when only adventures in it show; else its file name's.</summary>
    public async Task<IReadOnlyDictionary<long, string>> GetTitlesAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new Dictionary<long, string>();
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, string Title)>(new CommandDefinition(
            """
            SELECT d.document_id, coalesce(
                (SELECT coalesce(m.title, e.name) FROM entry_doc e LEFT JOIN entry_meta m ON m.entry_id = e.entry_id
                 WHERE e.document_id = d.document_id ORDER BY e.kind = 'Part', e.entry_id LIMIT 1),
                d.display_title)
            FROM doc d WHERE d.document_id IN (SELECT value FROM json_each(@ids))
            """, new { ids = JsonSerializer.Serialize(documentIds) }, cancellationToken: ct));
        return rows.ToDictionary(r => r.DocumentId, r => r.Title);
    }
}

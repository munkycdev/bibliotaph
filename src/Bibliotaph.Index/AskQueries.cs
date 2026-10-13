using System.Buffers.Binary;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>A page the question's words found, with its bm25 rank (lower is better).</summary>
public sealed record WordHit(long PageId, int PdfPage, double Rank);

/// <summary>A page of the book being asked: its row, its PDF page, its printed label and its indexed text.</summary>
public sealed record AskPageRow(long PageId, int PdfPage, string? Label, string Text);

/// <summary>A page's vector, by the embedding model asked for.</summary>
public sealed record PageVector(long PageId, int PdfPage, float[] Vector);

/// <summary>
/// "212 of 226 pages have text; 14 await OCR": a book's pages, those with text, those waiting for OCR, and those with a
/// vector from the embedding model asked about.
/// </summary>
public sealed record PageCoverage(int Pages, int WithText, int AwaitingOcr, int WithVectors);

/// <summary>Turns a vector into page_vec's blob, little-endian float32s, and back.</summary>
public static class VectorBlob
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        return vector;
    }
}

/// <summary>
/// What Ask this book reads from index.db for one book: pages by the question's words, page texts, page vectors and
/// how much of the book has text and vectors. Every value is a bound parameter; the FTS5 expression comes from
/// AskRetrieval.WordsMatch, which quotes every word.
/// </summary>
public sealed class AskQueries(IndexDatabase database)
{
    /// <summary>The book's pages that have any of the words, best first by bm25, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<WordHit>> FindWordsAsync(long documentId, string match, int limit, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long PdfPage, double Rank)>(new CommandDefinition(
            """
            SELECT p.id, p.pdf_page, page_fts.rank
            FROM page_fts JOIN page p ON p.id = page_fts.rowid
            WHERE page_fts MATCH @match AND p.document_id = @documentId
            ORDER BY page_fts.rank, p.pdf_page
            LIMIT @limit
            """, new { match, documentId, limit }, cancellationToken: ct));
        return [.. rows.Select(r => new WordHit(r.PageId, (int)r.PdfPage, r.Rank))];
    }

    /// <summary>
    /// For each FTS5 expression (one word of a question each), how many of the book's pages it finds: how rare the word
    /// is in this book. One count a word, for the few words of a question.
    /// </summary>
    public async Task<IReadOnlyList<int>> CountPagesAsync(long documentId, IReadOnlyList<string> matches, CancellationToken ct = default)
    {
        if (matches.Count == 0) return [];
        await using var connection = database.OpenRead();
        var counts = new List<int>(matches.Count);
        foreach (var match in matches)
        {
            counts.Add((int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT count(*) FROM page_fts JOIN page p ON p.id = page_fts.rowid
                WHERE page_fts MATCH @match AND p.document_id = @documentId
                """, new { match, documentId }, cancellationToken: ct)));
        }
        return counts;
    }

    /// <summary>These pages of the book, with their labels and text.</summary>
    public async Task<IReadOnlyList<AskPageRow>> GetPagesAsync(long documentId, IReadOnlyCollection<long> pageIds, CancellationToken ct = default)
    {
        if (pageIds.Count == 0) return [];
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long PdfPage, string? Label, string Text)>(new CommandDefinition(
            """
            SELECT id, pdf_page, label, text FROM page
            WHERE document_id = @documentId AND id IN (SELECT value FROM json_each(@ids))
            ORDER BY pdf_page
            """, new { documentId, ids = System.Text.Json.JsonSerializer.Serialize(pageIds) }, cancellationToken: ct));
        return [.. rows.Select(r => new AskPageRow(r.PageId, (int)r.PdfPage, r.Label, r.Text))];
    }

    /// <summary>Every page's printed label, by PDF page, for the page numbers a reader sees. A page without one is null.</summary>
    public async Task<IReadOnlyList<string?>> GetLabelsAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = (await connection.QueryAsync<(long PdfPage, string? Label)>(new CommandDefinition(
            "SELECT pdf_page, label FROM page WHERE document_id = @documentId ORDER BY pdf_page", new { documentId }, cancellationToken: ct))).ToList();
        var labels = new string?[rows.Count == 0 ? 0 : (int)rows[^1].PdfPage + 1];
        foreach (var (page, label) in rows) labels[(int)page] = label;
        return labels;
    }

    /// <summary>The vectors the embedding model <paramref name="model"/> made of the book's pages, at this text version.</summary>
    public async Task<IReadOnlyList<PageVector>> GetVectorsAsync(long documentId, string model, int version, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long PdfPage, byte[] Vector)>(new CommandDefinition(
            """
            SELECT v.page_id, p.pdf_page, v.vector
            FROM page_vec v JOIN page p ON p.id = v.page_id
            WHERE p.document_id = @documentId AND v.model = @model AND v.version = @version
            """, new { documentId, model, version }, cancellationToken: ct));
        return [.. rows.Select(r => new PageVector(r.PageId, (int)r.PdfPage, VectorBlob.FromBytes(r.Vector)))];
    }

    /// <summary>Up to <paramref name="limit"/> of the book's pages with text and no vector from this model and version, in page order.</summary>
    public async Task<IReadOnlyList<AskPageRow>> GetPagesWithoutVectorsAsync(long documentId, string model, int version, int limit, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PageId, long PdfPage, string? Label, string Text)>(new CommandDefinition(
            """
            SELECT p.id, p.pdf_page, p.label, p.text FROM page p
            WHERE p.document_id = @documentId AND p.text <> ''
              AND NOT EXISTS (SELECT 1 FROM page_vec v WHERE v.page_id = p.id AND v.model = @model AND v.version = @version)
            ORDER BY p.pdf_page
            LIMIT @limit
            """, new { documentId, model, version, limit }, cancellationToken: ct));
        return [.. rows.Select(r => new AskPageRow(r.PageId, (int)r.PdfPage, r.Label, r.Text))];
    }

    /// <summary>How many of the book's pages there are, have text, wait for OCR, and have a vector from this model and version.</summary>
    public async Task<PageCoverage> GetCoverageAsync(long documentId, string? model, int version, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var (pages, withText, awaitingOcr, withVectors) = await connection.QuerySingleAsync<(long, long, long, long)>(new CommandDefinition(
            """
            SELECT count(*), coalesce(sum(p.text <> ''), 0), coalesce(sum(p.needs_ocr = 1), 0),
                   coalesce(sum(EXISTS (SELECT 1 FROM page_vec v WHERE v.page_id = p.id AND v.model = @model AND v.version = @version)), 0)
            FROM page p WHERE p.document_id = @documentId
            """, new { documentId, model = model ?? "", version }, cancellationToken: ct));
        return new PageCoverage((int)pages, (int)withText, (int)awaitingOcr, (int)withVectors);
    }
}

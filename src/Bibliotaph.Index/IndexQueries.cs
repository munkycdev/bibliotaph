using System.Globalization;
using System.Text.Json;
using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index;

public sealed record QueueSummary(long Pending, long Failed)
{
    public bool IsIdle => Pending == 0;
}

/// <summary>
/// Where indexing stands, in counts a person can read: documents known to the index, searchable (text done, or an
/// image, which is found by its title),
/// still processing (any stage waiting or running), needing attention (a stage failed or is blocked), scanned
/// pages still waiting for OCR (not counting books whose OCR is blocked or failed), and documents with index-lane
/// work (any stage but OCR) waiting or running. For progress bars: documents queued for any stage (known before
/// Probe adds them to the index), and scanned pages already read by OCR.
/// </summary>
public sealed record IndexProgress(
    long Documents, long Searchable, long Processing, long NeedAttention, long PagesAwaitingOcr, long Indexing, long Queued = 0, long OcrPagesDone = 0)
{
    /// <summary>Queued documents with no text, cover or other index-lane stage left to run.</summary>
    public long Indexed => Math.Max(0, Queued - Indexing);

    public long OcrPages => OcrPagesDone + PagesAwaitingOcr;
}

/// <summary>A document with a failed or blocked stage, and why.</summary>
public sealed record AttentionItem(long DocumentId, string Title, Stage Stage, StageStatus Status, string? Reason, DateTime UpdatedUtc);

/// <summary>A page the OCR stage still has to read.</summary>
public sealed record OcrPage(int PdfPage, double WidthPt, double HeightPt);

/// <summary>Read-side queries over index.db. Every value is a bound parameter.</summary>
public sealed class IndexQueries(IndexDatabase database)
{
    public async Task<QueueSummary> GetQueueSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.QuerySingleAsync<QueueSummary>(new CommandDefinition(
            """
            SELECT
                count(*) FILTER (WHERE status IN ('pending', 'leased')) AS Pending,
                count(*) FILTER (WHERE status = 'failed')              AS Failed
            FROM job
            """, cancellationToken: ct));
    }

    public async Task<IndexProgress> GetProgressAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.QuerySingleAsync<IndexProgress>(new CommandDefinition(
            """
            SELECT
                (SELECT count(*) FROM doc) AS Documents,
                (SELECT count(*) FROM stage_status WHERE stage = 'Text' AND status IN ('Complete', 'Partial', 'Skipped')) AS Searchable,
                (SELECT count(DISTINCT document_id) FROM job WHERE status IN ('pending', 'leased')) AS Processing,
                (SELECT count(DISTINCT document_id) FROM stage_status WHERE status IN ('Failed', 'Blocked')) AS NeedAttention,
                (SELECT count(*) FROM page WHERE needs_ocr = 1 AND document_id NOT IN
                    (SELECT document_id FROM stage_status WHERE stage = 'Ocr' AND status IN ('Blocked', 'Failed'))) AS PagesAwaitingOcr,
                (SELECT count(DISTINCT document_id) FROM job WHERE status IN ('pending', 'leased') AND stage <> 'Ocr') AS Indexing,
                (SELECT count(DISTINCT document_id) FROM stage_status) AS Queued,
                (SELECT count(*) FROM page WHERE text_source = 'ocr') AS OcrPagesDone
            """, cancellationToken: ct));
    }

    /// <summary>Failed and blocked stages, newest first, with the document's title where Probe got that far.</summary>
    public async Task<IReadOnlyList<AttentionItem>> GetAttentionAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, string? Title, string Stage, string Status, string? Reason, string Updated)>(new CommandDefinition(
            """
            SELECT s.document_id, d.display_title, s.stage, s.status, s.reason, s.updated_utc
            FROM stage_status s LEFT JOIN doc d ON d.document_id = s.document_id
            WHERE s.status IN ('Failed', 'Blocked')
            ORDER BY s.updated_utc DESC
            """, cancellationToken: ct));
        return [.. rows.Select(r => new AttentionItem(r.DocumentId, r.Title ?? $"Document {r.DocumentId}", Enum.Parse<Stage>(r.Stage),
            Enum.Parse<StageStatus>(r.Status), r.Reason, DateTime.Parse(r.Updated, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)))];
    }

    public async Task<IReadOnlyList<OcrPage>> GetPagesNeedingOcrAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PdfPage, double WidthPt, double HeightPt)>(new CommandDefinition(
            "SELECT pdf_page, width_pt, height_pt FROM page WHERE document_id = @documentId AND needs_ocr = 1 ORDER BY pdf_page",
            new { documentId }, cancellationToken: ct));
        return [.. rows.Select(r => new OcrPage((int)r.PdfPage, r.WidthPt, r.HeightPt))];
    }

    /// <summary>A document's cover file name in the cover cache, or null when it has none.</summary>
    public async Task<string?> GetCoverAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT cover FROM doc WHERE document_id = @documentId", new { documentId }, cancellationToken: ct));
    }

    /// <summary>A document's title, or null before Probe has given it one.</summary>
    public async Task<string?> GetTitleAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT display_title FROM doc WHERE document_id = @documentId", new { documentId }, cancellationToken: ct));
    }

    /// <summary>How many of <paramref name="documentIds"/> are searchable, as <see cref="IndexProgress.Searchable"/> counts them.</summary>
    public async Task<long> CountSearchableAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return 0;
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*) FROM stage_status
            WHERE stage = 'Text' AND status IN ('Complete', 'Partial', 'Skipped') AND document_id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = JsonSerializer.Serialize(documentIds) }, cancellationToken: ct));
    }
}

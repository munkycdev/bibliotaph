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
/// Probe adds them to the index), and scanned pages already read by OCR. Classification by AI is counted on its own
/// (<see cref="ToClassify"/>, <see cref="Classified"/>, <see cref="ClassifyFailed"/>) and nowhere else, since it is
/// optional and may wait for an endpoint for as long as the user likes.
/// </summary>
public sealed record IndexProgress(
    long Documents, long Searchable, long Processing, long NeedAttention, long PagesAwaitingOcr, long Indexing, long Queued = 0, long OcrPagesDone = 0,
    long ToClassify = 0, long Classified = 0, long ClassifyFailed = 0)
{
    /// <summary>Queued documents with no text, cover or other index-lane stage left to run.</summary>
    public long Indexed => Math.Max(0, Queued - Indexing);

    public long OcrPages => OcrPagesDone + PagesAwaitingOcr;
}

/// <summary>A document with a failed or blocked stage, and why.</summary>
public sealed record AttentionItem(long DocumentId, string Title, Stage Stage, StageStatus Status, string? Reason, DateTime UpdatedUtc);

/// <summary>A page the OCR stage still has to read.</summary>
public sealed record OcrPage(int PdfPage, double WidthPt, double HeightPt);

/// <summary>A page's indexed text, from the PDF or from OCR.</summary>
public sealed record PageTextEntry(int PdfPage, string Text);

/// <summary>A book the pilot could pick: its folder hint and game system, to spread the sample across.</summary>
public sealed record PilotCandidate(long DocumentId, string Folder, string? System);

/// <summary>The PDF's own document information, as Probe read it.</summary>
public sealed record EmbeddedInfo(string? Title, string? Author, string? Subject, string? Keywords);

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
            WHERE stage <> 'Classify'
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
                (SELECT count(DISTINCT document_id) FROM job WHERE status IN ('pending', 'leased') AND stage <> 'Classify') AS Processing,
                (SELECT count(DISTINCT document_id) FROM stage_status WHERE status IN ('Failed', 'Blocked') AND stage <> 'Classify') AS NeedAttention,
                (SELECT count(*) FROM page WHERE needs_ocr = 1 AND document_id NOT IN
                    (SELECT document_id FROM stage_status WHERE stage = 'Ocr' AND status IN ('Blocked', 'Failed'))) AS PagesAwaitingOcr,
                (SELECT count(DISTINCT document_id) FROM job WHERE status IN ('pending', 'leased') AND stage NOT IN ('Ocr', 'Classify')) AS Indexing,
                (SELECT count(DISTINCT document_id) FROM stage_status) AS Queued,
                (SELECT count(*) FROM page WHERE text_source = 'ocr') AS OcrPagesDone,
                (SELECT count(*) FROM stage_status WHERE stage = 'Classify' AND status IN ('Pending', 'Running')) AS ToClassify,
                (SELECT count(*) FROM stage_status WHERE stage = 'Classify' AND status IN ('Complete', 'Partial', 'Skipped')) AS Classified,
                (SELECT count(*) FROM stage_status WHERE stage = 'Classify' AND status = 'Failed') AS ClassifyFailed
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
            WHERE s.status IN ('Failed', 'Blocked') AND s.stage <> 'Classify'
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

    /// <summary>The documents with Needs review cards, by title, and how many cards each has.</summary>
    public async Task<IReadOnlyList<(long DocumentId, string Title, int Reviews)>> GetReviewDocumentsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, string? Title, long Reviews)>(new CommandDefinition(
            """
            SELECT m.document_id, coalesce(m.title, d.display_title), m.needs_review
            FROM doc_meta m LEFT JOIN doc d ON d.document_id = m.document_id
            WHERE m.needs_review > 0
            ORDER BY coalesce(m.title, d.display_title) COLLATE NOCASE, m.document_id
            """, cancellationToken: ct));
        return [.. rows.Select(r => (r.DocumentId, r.Title ?? $"Document {r.DocumentId}", (int)r.Reviews))];
    }

    /// <summary>How many Needs review cards documents have, in all.</summary>
    public async Task<long> CountReviewsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT coalesce(sum(needs_review), 0) FROM doc_meta", cancellationToken: ct));
    }

    /// <summary>A document's embedded information, or null before Probe has added it.</summary>
    public async Task<EmbeddedInfo?> GetEmbeddedInfoAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.QuerySingleOrDefaultAsync<EmbeddedInfo>(new CommandDefinition(
            "SELECT meta_title AS Title, meta_author AS Author, meta_subject AS Subject, meta_keywords AS Keywords FROM doc WHERE document_id = @documentId",
            new { documentId }, cancellationToken: ct));
    }

    /// <summary>A document's pages that have text, in order: what the classifier reads, and checks quotes against.</summary>
    public async Task<IReadOnlyList<PageTextEntry>> GetPageTextsAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long PdfPage, string Text)>(new CommandDefinition(
            "SELECT pdf_page, text FROM page WHERE document_id = @documentId AND text <> '' ORDER BY pdf_page",
            new { documentId }, cancellationToken: ct));
        return [.. rows.Select(r => new PageTextEntry((int)r.PdfPage, r.Text))];
    }

    /// <summary>A document's page count, or null before Probe has counted its pages.</summary>
    public async Task<int?> GetPageCountAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT page_count FROM doc WHERE document_id = @documentId", new { documentId }, cancellationToken: ct));
    }

    /// <summary>A document's outline (bookmarks), in order.</summary>
    public async Task<IReadOnlyList<OutlineRow>> GetOutlineAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(string Title, long PdfPage, long Depth)>(new CommandDefinition(
            "SELECT title, pdf_page, depth FROM outline WHERE document_id = @documentId ORDER BY ord",
            new { documentId }, cancellationToken: ct));
        return [.. rows.Select(r => new OutlineRow(r.Title, (int)r.PdfPage, (int)r.Depth))];
    }

    /// <summary>Where one of a document's stages stands, or null when it was never queued.</summary>
    public async Task<StageStatus?> GetStageStatusAsync(long documentId, Stage stage, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var status = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM stage_status WHERE document_id = @documentId AND stage = @stage",
            new { documentId, stage = stage.ToString() }, cancellationToken: ct));
        return status is null ? null : Enum.Parse<StageStatus>(status);
    }

    /// <summary>
    /// A document whose text the classifier could read, for Settings > AI's Test button: the first by title with text
    /// on at least <paramref name="minPages"/> pages, skipping <paramref name="excluded"/>.
    /// </summary>
    public async Task<long?> FindSampleAsync(IReadOnlyCollection<long> excluded, int minPages = 3, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT d.document_id FROM doc d
            WHERE d.format = 'pdf' AND d.document_id NOT IN (SELECT value FROM json_each(@excluded))
              AND (SELECT count(*) FROM page p WHERE p.document_id = d.document_id AND length(p.text) > 200) >= @minPages
            ORDER BY d.display_title COLLATE NOCASE
            LIMIT 1
            """,
            new { excluded = JsonSerializer.Serialize(excluded), minPages }, cancellationToken: ct));
    }

    /// <summary>
    /// PDFs a model could read, for the pilot's sample: those with text on at least <paramref name="minPages"/> pages,
    /// with their folder hint and game system (if any) to spread the sample across.
    /// </summary>
    public async Task<IReadOnlyList<PilotCandidate>> GetPilotCandidatesAsync(int minPages = 3, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. (await connection.QueryAsync<PilotCandidateRow>(new CommandDefinition(
            """
            SELECT d.document_id AS DocumentId, coalesce(d.folder_hint, '') AS Folder,
                   (SELECT min(f.value) FROM doc_facet f WHERE f.document_id = d.document_id AND f.field = 'system') AS System
            FROM doc d
            WHERE d.format = 'pdf'
              AND (SELECT count(*) FROM page p WHERE p.document_id = d.document_id AND length(p.text) > 200) >= @minPages
            ORDER BY d.document_id
            """,
            new { minPages }, cancellationToken: ct))).Select(r => new PilotCandidate(r.DocumentId, r.Folder, r.System))];
    }

    // A book with no system reads its system as NULL, which Dapper can't match to a record's constructor.
    sealed class PilotCandidateRow
    {
        public long DocumentId { get; init; }
        public string Folder { get; init; } = "";
        public string? System { get; init; }
    }

    /// <summary>Every document Probe has added, and those with projected metadata.</summary>
    public async Task<(IReadOnlyList<long> Documents, IReadOnlyList<long> WithMetadata)> GetDocumentIdsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var documents = await connection.QueryAsync<long>(new CommandDefinition("SELECT document_id FROM doc", cancellationToken: ct));
        var withMetadata = await connection.QueryAsync<long>(new CommandDefinition("SELECT document_id FROM doc_meta", cancellationToken: ct));
        return ([.. documents], [.. withMetadata]);
    }
}

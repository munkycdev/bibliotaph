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
/// optional and may wait for an endpoint for as long as the user likes. <see cref="Withheld"/> counts the documents whose
/// text won't be read by itself: locked, protected or forgotten (<see cref="TextAccess"/>), so nothing says they are still
/// being read.
/// </summary>
public sealed record IndexProgress(
    long Documents, long Searchable, long Processing, long NeedAttention, long PagesAwaitingOcr, long Indexing, long Queued = 0, long OcrPagesDone = 0,
    long ToClassify = 0, long Classified = 0, long ClassifyFailed = 0, long Withheld = 0)
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

/// <summary>A card with a given title: its kind, the document it shows (none when owned elsewhere) and its publisher.</summary>
public sealed record TitleMatch(EntryId EntryId, EntryKind Kind, long? DocumentId, string? Publisher);

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
                (SELECT count(*) FROM stage_status WHERE stage = 'Classify' AND status = 'Failed') AS ClassifyFailed,
                (SELECT count(DISTINCT document_id) FROM stage_status WHERE stage IN ('Probe', 'Text') AND (
                    (status = 'Blocked' AND reason = @locked) OR (status = 'Failed' AND reason = @protected) OR (status = 'Skipped' AND reason = @forgotten))) AS Withheld
            """, new { locked = TextAccessReasons.Locked, @protected = TextAccessReasons.Protected, forgotten = TextAccessReasons.Forgotten },
            cancellationToken: ct));
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

    /// <summary>Every page's text, in page order (empty for a page with none), for fingerprinting.</summary>
    public async Task<IReadOnlyList<string>> GetAllPageTextsAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT text FROM page WHERE document_id = @documentId ORDER BY pdf_page", new { documentId }, cancellationToken: ct))];
    }

    /// <summary>Each page's fingerprint, in page order (null for a page without one).</summary>
    public async Task<IReadOnlyList<string?>> GetFingerprintsAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. await connection.QueryAsync<string?>(new CommandDefinition(
            "SELECT fingerprint FROM page WHERE document_id = @documentId ORDER BY pdf_page", new { documentId }, cancellationToken: ct))];
    }

    /// <summary>A page's printed label and fingerprint, for a session item's page reference (slice 3 plan, choice 14). Null for a page the index doesn't have.</summary>
    public async Task<(string? Label, string? Fingerprint)?> GetPageMarkAsync(long documentId, int pdfPage, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(string? Label, string? Fingerprint)>(new CommandDefinition(
            "SELECT label, fingerprint FROM page WHERE document_id = @documentId AND pdf_page = @pdfPage", new { documentId, pdfPage }, cancellationToken: ct));
        return rows.Select(r => ((string? Label, string? Fingerprint)?)r).FirstOrDefault();
    }

    /// <summary>The pages of a document with this fingerprint, in page order: where a page went in another copy or version.</summary>
    public async Task<IReadOnlyList<int>> FindFingerprintAsync(long documentId, string fingerprint, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT pdf_page FROM page WHERE document_id = @documentId AND fingerprint = @fingerprint ORDER BY pdf_page",
            new { documentId, fingerprint }, cancellationToken: ct))];
    }

    /// <summary>
    /// Those of <paramref name="documentIds"/> whose pages have their fingerprints, or never will: Match has run, or their
    /// text couldn't be read so it won't. Until then a new version's pages aren't looked for (slice 4h plan, choice 1).
    /// </summary>
    public async Task<IReadOnlyList<long>> GetFingerprintedAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return [];
        await using var connection = database.OpenRead();
        var found = new List<long>();
        foreach (var ids in documentIds.Distinct().Chunk(500))
            found.AddRange(await connection.QueryAsync<long>(new CommandDefinition(
                """
                SELECT s.document_id FROM stage_status s
                WHERE s.document_id IN @ids AND (
                    (s.stage = 'Match' AND s.status NOT IN ('Pending', 'Running'))
                    OR (s.stage = 'Text' AND s.status IN ('Failed', 'Blocked', 'Skipped')
                        AND NOT EXISTS (SELECT 1 FROM stage_status m WHERE m.document_id = s.document_id AND m.stage = 'Match')))
                GROUP BY s.document_id
                """, new { ids }, cancellationToken: ct)));
        return found;
    }

    /// <summary>The first page of a document with this printed label, or null.</summary>
    public async Task<int?> FindLabelAsync(long documentId, string label, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT pdf_page FROM page WHERE document_id = @documentId AND label = @label ORDER BY pdf_page LIMIT 1",
            new { documentId, label }, cancellationToken: ct));
    }

    /// <summary>
    /// The bookmark a page sits under: the deepest one at or before it, nearest first. A session item takes its label
    /// from it (choice 13). Null when the book has no bookmarks before that page.
    /// </summary>
    public async Task<string?> GetBookmarkAsync(long documentId, int pdfPage, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT title FROM outline
            WHERE document_id = @documentId AND pdf_page >= 0 AND pdf_page <= @pdfPage AND trim(title) <> ''
            ORDER BY pdf_page DESC, depth DESC, ord DESC LIMIT 1
            """, new { documentId, pdfPage }, cancellationToken: ct));
    }

    /// <summary>
    /// The other documents with a page whose fingerprint one of <paramref name="documentId"/>'s pages has, with how many
    /// of its distinct fingerprints each shares, most first, and how many distinct fingerprints each has in all.
    /// </summary>
    public async Task<IReadOnlyList<(long DocumentId, int Shared, int Fingerprinted)>> GetSharingPagesAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long DocumentId, long Shared, long Fingerprinted)>(new CommandDefinition(
            """
            WITH sharing AS (
                SELECT other.document_id, count(DISTINCT other.fingerprint) AS shared
                FROM page mine JOIN page other ON other.fingerprint = mine.fingerprint AND other.document_id <> mine.document_id
                WHERE mine.document_id = @documentId AND mine.fingerprint IS NOT NULL
                GROUP BY other.document_id
            )
            SELECT s.document_id, s.shared,
                   (SELECT count(DISTINCT p.fingerprint) FROM page p WHERE p.document_id = s.document_id AND p.fingerprint IS NOT NULL)
            FROM sharing s
            ORDER BY s.shared DESC, s.document_id
            """,
            new { documentId }, cancellationToken: ct));
        return [.. rows.Select(r => (r.DocumentId, (int)r.Shared, (int)r.Fingerprinted))];
    }

    /// <summary>
    /// The documents with a page whose fingerprint is one of <paramref name="fingerprints"/>, for a file that isn't in the
    /// library (Check a download, F5 plan, choice 10): how many of them each shares, most first, and how many distinct
    /// fingerprints each has in all.
    /// </summary>
    public async Task<IReadOnlyList<(long DocumentId, int Shared, int Fingerprinted)>> GetSharingPagesAsync(IReadOnlyCollection<string> fingerprints,
        CancellationToken ct = default)
    {
        if (fingerprints.Count == 0) return [];
        await using var connection = database.OpenRead();
        var shared = new Dictionary<long, int>();
        // In batches, under SQLite's limit on bound values. Each batch holds different fingerprints, so counts add up.
        foreach (var batch in fingerprints.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var rows = await connection.QueryAsync<(long DocumentId, long Shared)>(new CommandDefinition(
                """
                SELECT document_id, count(DISTINCT fingerprint)
                FROM page WHERE fingerprint IN @batch
                GROUP BY document_id
                """,
                new { batch }, cancellationToken: ct));
            foreach (var (documentId, count) in rows) shared[documentId] = shared.GetValueOrDefault(documentId) + (int)count;
        }
        if (shared.Count == 0) return [];
        var totals = (await connection.QueryAsync<(long DocumentId, long Fingerprinted)>(new CommandDefinition(
            """
            SELECT document_id, count(DISTINCT fingerprint)
            FROM page WHERE document_id IN @ids AND fingerprint IS NOT NULL
            GROUP BY document_id
            """,
            new { ids = shared.Keys.ToArray() }, cancellationToken: ct))).ToDictionary(r => r.DocumentId, r => (int)r.Fingerprinted);
        return [.. shared.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => (s.Key, s.Value, totals.GetValueOrDefault(s.Key)))];
    }

    /// <summary>
    /// The cards with this title, ignoring case, with their kind, the document each shows (none for a book owned
    /// elsewhere) and their publisher, for Check a download (F5 plan, choice 11).
    /// </summary>
    public async Task<IReadOnlyList<TitleMatch>> GetTitleMatchesAsync(string title, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long EntryId, string Kind, long? DocumentId, string? Publisher)>(new CommandDefinition(
            """
            SELECT e.entry_id, e.kind, e.document_id, m.publisher
            FROM entry_meta m JOIN entry_doc e ON e.entry_id = m.entry_id
            WHERE lower(m.title) = lower(@title)
            ORDER BY e.entry_id
            """,
            new { title }, cancellationToken: ct));
        return [.. rows.Select(r => new TitleMatch(new EntryId(r.EntryId), Enum.Parse<EntryKind>(r.Kind), r.DocumentId, r.Publisher))];
    }

    /// <summary>
    /// The documents other entries' cards show when they have <paramref name="entryId"/>'s title and publisher, ignoring
    /// case; none when it lacks either.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetSameTitleAndPublisherAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT e.document_id
            FROM entry_meta mine
            JOIN entry_meta other ON other.entry_id <> mine.entry_id
                AND lower(other.title) = lower(mine.title) AND lower(other.publisher) = lower(mine.publisher)
            JOIN entry_doc e ON e.entry_id = other.entry_id
            WHERE mine.entry_id = @entryId AND mine.title IS NOT NULL AND mine.publisher IS NOT NULL AND e.document_id IS NOT NULL
            ORDER BY e.document_id
            """,
            new { entryId = entryId.Value }, cancellationToken: ct))];
    }

    /// <summary>
    /// The books owned elsewhere that could be <paramref name="entryId"/>'s (F5 plan, choice 6): the same title,
    /// ignoring case, and no publisher that says otherwise. A file's publisher often arrives later than its title, so
    /// one missing on either side doesn't rule a book out; the user answers the card anyway.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> GetElsewhereMatchesAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. (await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT other.entry_id
            FROM entry_meta mine
            JOIN entry_meta other ON other.entry_id <> mine.entry_id AND lower(other.title) = lower(mine.title)
                AND (other.publisher IS NULL OR mine.publisher IS NULL OR lower(other.publisher) = lower(mine.publisher))
            JOIN entry_doc e ON e.entry_id = other.entry_id
            WHERE mine.entry_id = @entryId AND mine.title IS NOT NULL AND e.kind = 'Elsewhere'
            ORDER BY other.entry_id
            """,
            new { entryId = entryId.Value }, cancellationToken: ct))).Select(id => new EntryId(id))];
    }

    /// <summary>The title each of <paramref name="entryIds"/>' cards shows: its effective title, or its document's.</summary>
    public async Task<IReadOnlyDictionary<EntryId, string>> GetEntryTitlesAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return new Dictionary<EntryId, string>();
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long EntryId, string? Title)>(new CommandDefinition(
            """
            SELECT e.entry_id, coalesce(m.title, e.name, d.display_title)
            FROM entry_doc e LEFT JOIN entry_meta m ON m.entry_id = e.entry_id LEFT JOIN doc d ON d.document_id = e.document_id
            WHERE e.entry_id IN @ids
            """,
            new { ids = entryIds.Select(e => e.Value).Distinct().ToArray() }, cancellationToken: ct));
        return rows.Where(r => r.Title is not null).ToDictionary(r => new EntryId(r.EntryId), r => r.Title!);
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

    /// <summary>
    /// The title each of these content hashes is shown by: its card's title, or its file's name. Hashes the index doesn't
    /// have are left out. For Settings, Passwords (slice 4i plan, choice 2), which knows its books only by hash.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetTitlesByHashAsync(IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        if (contentHashes.Count == 0) return new Dictionary<string, string>();
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(string Hash, string Title)>(new CommandDefinition(
            """
            SELECT d.content_hash, COALESCE(m.title, d.display_title)
            FROM doc d
            LEFT JOIN entry_doc e ON e.document_id = d.document_id
            LEFT JOIN entry_meta m ON m.entry_id = e.entry_id
            WHERE d.content_hash IN @contentHashes
            ORDER BY e.entry_id IS NULL, d.document_id
            """, new { contentHashes }, cancellationToken: ct));
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, title) in rows) titles.TryAdd(hash, title);
        return titles;
    }

    /// <summary>
    /// How many of <paramref name="entryIds"/> are searchable: the document each shows is, as
    /// <see cref="IndexProgress.Searchable"/> counts documents.
    /// </summary>
    public async Task<long> CountSearchableAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return 0;
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT count(*) FROM entry_doc e JOIN stage_status s ON s.document_id = e.document_id
            WHERE s.stage = 'Text' AND s.status IN ('Complete', 'Partial', 'Skipped') AND e.entry_id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = JsonSerializer.Serialize(entryIds) }, cancellationToken: ct));
    }

    /// <summary>
    /// The entries with Needs review cards, by title, with the document each shows (null before Probe has added it)
    /// and how many cards each has.
    /// </summary>
    public async Task<IReadOnlyList<(EntryId EntryId, long? DocumentId, string Title, int Reviews)>> GetReviewEntriesAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long EntryId, long? DocumentId, string? Title, long Reviews)>(new CommandDefinition(
            """
            SELECT m.entry_id, e.document_id, coalesce(m.title, d.display_title), m.needs_review
            FROM entry_meta m LEFT JOIN entry_doc e ON e.entry_id = m.entry_id LEFT JOIN doc d ON d.document_id = e.document_id
            WHERE m.needs_review > 0
            ORDER BY coalesce(m.title, d.display_title) COLLATE NOCASE, m.entry_id
            """, cancellationToken: ct));
        return [.. rows.Select(r => (new EntryId(r.EntryId), r.DocumentId, r.Title ?? $"Document {r.EntryId}", (int)r.Reviews))];
    }

    /// <summary>How many Needs review cards entries have, in all.</summary>
    public async Task<long> CountReviewsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT coalesce(sum(needs_review), 0) FROM entry_meta", cancellationToken: ct));
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
                   (SELECT min(f.value) FROM entry_facet f JOIN entry_doc e ON e.entry_id = f.entry_id
                    WHERE e.document_id = d.document_id AND f.field = 'system') AS System
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

    /// <summary>The entries index.db has cards for, and those with projected metadata.</summary>
    public async Task<(IReadOnlyList<EntryId> Entries, IReadOnlyList<EntryId> WithMetadata)> GetEntryIdsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var entries = await connection.QueryAsync<long>(new CommandDefinition("SELECT entry_id FROM entry_doc", cancellationToken: ct));
        var withMetadata = await connection.QueryAsync<long>(new CommandDefinition("SELECT entry_id FROM entry_meta", cancellationToken: ct));
        return ([.. entries.Select(e => new EntryId(e))], [.. withMetadata.Select(e => new EntryId(e))]);
    }
}

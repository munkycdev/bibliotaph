using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index;

/// <summary>What the Probe stage writes for a document.</summary>
public sealed record DocRow
{
    public required long DocumentId { get; init; }
    public required string ContentHash { get; init; }
    public required string Format { get; init; }
    public required string DisplayTitle { get; init; }
    public int? PageCount { get; init; }
    public int? WidthPx { get; init; }
    public int? HeightPx { get; init; }
    public bool Encrypted { get; init; }
    public bool CanCopy { get; init; } = true;
    public string? MetaTitle { get; init; }
    public string? MetaAuthor { get; init; }
    public string? MetaSubject { get; init; }
    public string? MetaKeywords { get; init; }
    public string? FolderHint { get; init; }
}

public sealed record PageRow(int PdfPage, string? Label, double WidthPt, double HeightPt);

public sealed record OutlineRow(string Title, int PdfPage, int Depth);

/// <summary>A page's text after extraction or OCR. <see cref="Error"/> explains a page with no text.</summary>
public sealed record PageTextRow(int PdfPage, string Text, string Source, double Quality, bool NeedsOcr, string? Error = null);

/// <summary>An OCR'd word with its box in PDF points, origin bottom-left.</summary>
public sealed record OcrWordRow(string Text, double Left, double Top, double Right, double Bottom);

/// <summary>One value of a vocabulary field: a term key and its label.</summary>
public sealed record DocFacetRow(string Field, string Value, string Label, bool Confirmed);

/// <summary>Whether a document's levels are known, known not to apply, or unknown.</summary>
public enum LevelState
{
    Unknown,
    Known,
    NotApplicable,
}

/// <summary>
/// A document's effective metadata as index.db holds it for listing, filtering and search. The metadata projector
/// builds these from catalog.db; nothing else writes them.
/// </summary>
public sealed record DocMetaRow
{
    public required long DocumentId { get; init; }
    public string? Title { get; init; }
    public string? Publisher { get; init; }
    public string? Series { get; init; }
    public string? Authors { get; init; }
    public int? Year { get; init; }
    public string? SystemLabel { get; init; }
    public string? KindLabel { get; init; }
    public int? LevelMin { get; init; }
    public int? LevelMax { get; init; }
    public LevelState Levels { get; init; }
    /// <summary>How many Needs review cards the document has (<see cref="Core.Metadata.MetadataReview"/>).</summary>
    public int Reviews { get; init; }
    public bool Suggested { get; init; }
    public string? Tags { get; init; }
    public string? ConfirmedText { get; init; }
    public string? ProvisionalText { get; init; }
    public IReadOnlyList<DocFacetRow> Facets { get; init; } = [];
}

/// <summary>A name a vocabulary term goes by, in comparison form, for field search.</summary>
public sealed record TermAliasRow(string Vocabulary, string Alias, string Value);

/// <summary>
/// Pipeline writes to index.db: documents, pages, page text, OCR words and covers. Everything goes through the
/// <see cref="IndexWriter"/>; page inserts and updates reuse one prepared command per batch.
/// </summary>
public sealed class IndexStore(IndexWriter writer, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Records a probed document with its pages and outline. Pages are upserted by page number, so probing a
    /// document again (after a Probe version bump) updates labels and sizes without losing extracted text.
    /// </summary>
    public Task UpsertDocumentAsync(DocRow doc, IReadOnlyList<PageRow> pages, IReadOnlyList<OutlineRow> outline, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            c.Execute(
                """
                INSERT INTO doc (document_id, content_hash, format, display_title, page_count, width_px, height_px, encrypted, can_copy,
                                 meta_title, meta_author, meta_subject, meta_keywords, folder_hint, added_utc)
                VALUES (@DocumentId, @ContentHash, @Format, @DisplayTitle, @PageCount, @WidthPx, @HeightPx, @Encrypted, @CanCopy,
                        @MetaTitle, @MetaAuthor, @MetaSubject, @MetaKeywords, @FolderHint, @now)
                ON CONFLICT (document_id) DO UPDATE SET
                    content_hash = excluded.content_hash, format = excluded.format, display_title = excluded.display_title,
                    page_count = excluded.page_count, width_px = excluded.width_px, height_px = excluded.height_px,
                    encrypted = excluded.encrypted, can_copy = excluded.can_copy, meta_title = excluded.meta_title,
                    meta_author = excluded.meta_author, meta_subject = excluded.meta_subject, meta_keywords = excluded.meta_keywords,
                    folder_hint = excluded.folder_hint
                """,
                new
                {
                    doc.DocumentId,
                    doc.ContentHash,
                    doc.Format,
                    doc.DisplayTitle,
                    doc.PageCount,
                    doc.WidthPx,
                    doc.HeightPx,
                    doc.Encrypted,
                    doc.CanCopy,
                    doc.MetaTitle,
                    doc.MetaAuthor,
                    doc.MetaSubject,
                    doc.MetaKeywords,
                    doc.FolderHint,
                    now = JobBoard.Timestamp(_clock.GetUtcNow()),
                }, t);
            RefreshDocSearch(c, t, doc.DocumentId);

            using (var insert = c.CreateCommand())
            {
                insert.Transaction = t;
                insert.CommandText =
                    """
                    INSERT INTO page (document_id, pdf_page, label, width_pt, height_pt) VALUES ($doc, $page, $label, $width, $height)
                    ON CONFLICT (document_id, pdf_page) DO UPDATE SET label = excluded.label, width_pt = excluded.width_pt, height_pt = excluded.height_pt
                    """;
                var docParam = insert.Parameters.Add("$doc", SqliteType.Integer);
                var pageParam = insert.Parameters.Add("$page", SqliteType.Integer);
                var labelParam = insert.Parameters.Add("$label", SqliteType.Text);
                var widthParam = insert.Parameters.Add("$width", SqliteType.Real);
                var heightParam = insert.Parameters.Add("$height", SqliteType.Real);
                insert.Prepare();
                docParam.Value = doc.DocumentId;
                foreach (var page in pages)
                {
                    pageParam.Value = page.PdfPage;
                    labelParam.Value = (object?)page.Label ?? DBNull.Value;
                    widthParam.Value = page.WidthPt;
                    heightParam.Value = page.HeightPt;
                    insert.ExecuteNonQuery();
                }
            }
            // A re-probe that finds fewer pages (a damaged file read differently) drops the extras.
            c.Execute("DELETE FROM page WHERE document_id = @DocumentId AND pdf_page >= @count", new { doc.DocumentId, count = pages.Count }, t);

            c.Execute("DELETE FROM outline WHERE document_id = @DocumentId", new { doc.DocumentId }, t);
            c.Execute("INSERT INTO outline (document_id, ord, title, pdf_page, depth) VALUES (@DocumentId, @Ord, @Title, @PdfPage, @Depth)",
                outline.Select((o, i) => new { doc.DocumentId, Ord = i, o.Title, o.PdfPage, o.Depth }), t);
        }, ct);

    /// <summary>
    /// Stores extracted or OCR'd text for a run of pages. The FTS triggers keep page_fts in step. A page already flagged
    /// for OCR stays flagged (Reprocess with OCR on every page flags them all first). When a page is read again, as on
    /// Reprocess, text it already has is kept while the new reading is no better: an OCR'd page that will be OCR'd again
    /// keeps its OCR text until then, and a page that can't be read this time keeps what it had, so a book never drops
    /// out of search while it is reprocessed. A page read for the first time has no text, so nothing is kept.
    /// </summary>
    public Task SetPageTextAsync(long documentId, IReadOnlyList<PageTextRow> pages, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            using var update = c.CreateCommand();
            update.Transaction = t;
            // SQLite reads every column here as it was before the update, so needs_ocr is the page's old flag.
            const string Keep = "text <> '' AND ($error IS NOT NULL OR (text_source = 'ocr' AND max(needs_ocr, $needsOcr) = 1))";
            update.CommandText =
                $"""
                UPDATE page SET
                    text = CASE WHEN {Keep} THEN text ELSE $text END,
                    text_source = CASE WHEN {Keep} THEN text_source ELSE $source END,
                    text_quality = CASE WHEN {Keep} THEN text_quality ELSE $quality END,
                    needs_ocr = max(needs_ocr, $needsOcr), error = $error
                WHERE document_id = $doc AND pdf_page = $page
                """;
            var textParam = update.Parameters.Add("$text", SqliteType.Text);
            var sourceParam = update.Parameters.Add("$source", SqliteType.Text);
            var qualityParam = update.Parameters.Add("$quality", SqliteType.Real);
            var needsOcrParam = update.Parameters.Add("$needsOcr", SqliteType.Integer);
            var errorParam = update.Parameters.Add("$error", SqliteType.Text);
            var docParam = update.Parameters.Add("$doc", SqliteType.Integer);
            var pageParam = update.Parameters.Add("$page", SqliteType.Integer);
            update.Prepare();
            docParam.Value = documentId;
            foreach (var page in pages)
            {
                textParam.Value = page.Text;
                sourceParam.Value = page.Source;
                qualityParam.Value = page.Quality;
                needsOcrParam.Value = page.NeedsOcr ? 1 : 0;
                errorParam.Value = (object?)page.Error ?? DBNull.Value;
                pageParam.Value = page.PdfPage;
                update.ExecuteNonQuery();
            }
        }, ct);

    /// <summary>Replaces one page's text with its OCR result and stores the word boxes.</summary>
    public Task SetOcrPageAsync(long documentId, int pdfPage, string text, double quality, IReadOnlyList<OcrWordRow> words, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var pageId = c.ExecuteScalar<long?>("SELECT id FROM page WHERE document_id = @documentId AND pdf_page = @pdfPage", new { documentId, pdfPage }, t);
            if (pageId is null) return;
            c.Execute("UPDATE page SET text = @text, text_source = 'ocr', text_quality = @quality, needs_ocr = 0, error = NULL WHERE id = @pageId",
                new { text, quality, pageId }, t);
            c.Execute("DELETE FROM ocr_word WHERE page_id = @pageId", new { pageId }, t);
            c.Execute("INSERT INTO ocr_word (page_id, ord, text, left_pt, top_pt, right_pt, bottom_pt) VALUES (@pageId, @Ord, @Text, @Left, @Top, @Right, @Bottom)",
                words.Select((w, i) => new { pageId, Ord = i, w.Text, w.Left, w.Top, w.Right, w.Bottom }), t);
        }, ct);

    /// <summary>Marks a page whose OCR failed, keeping whatever text it had.</summary>
    public Task SetPageErrorAsync(long documentId, int pdfPage, string error, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
            c.Execute("UPDATE page SET needs_ocr = 0, error = @error WHERE document_id = @documentId AND pdf_page = @pdfPage",
                new { documentId, pdfPage, error }, t), ct);

    public Task SetCoverAsync(long documentId, string? cover, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) => c.Execute("UPDATE doc SET cover = @cover WHERE document_id = @documentId", new { documentId, cover }, t), ct);

    /// <summary>
    /// Replaces the projected metadata of these documents: their doc_meta row (deleted when the document has no
    /// metadata), their facets, and their doc_fts row.
    /// </summary>
    public Task SetMetadataAsync(IReadOnlyList<DocMetaRow> rows, CancellationToken ct = default) =>
        rows.Count == 0 ? Task.CompletedTask : writer.WriteAsync((c, t) =>
        {
            foreach (var row in rows)
            {
                c.Execute("DELETE FROM doc_facet WHERE document_id = @DocumentId", new { row.DocumentId }, t);
                c.Execute(
                    """
                    INSERT INTO doc_meta (document_id, title, publisher, series, authors, year, system_label, kind_label, level_min, level_max,
                                          level_state, needs_review, suggested, tags, confirmed_text, provisional_text)
                    VALUES (@DocumentId, @Title, @Publisher, @Series, @Authors, @Year, @SystemLabel, @KindLabel, @LevelMin, @LevelMax,
                            @levelState, @Reviews, @Suggested, @Tags, @ConfirmedText, @ProvisionalText)
                    ON CONFLICT (document_id) DO UPDATE SET
                        title = excluded.title, publisher = excluded.publisher, series = excluded.series, authors = excluded.authors,
                        year = excluded.year, system_label = excluded.system_label, kind_label = excluded.kind_label,
                        level_min = excluded.level_min, level_max = excluded.level_max, level_state = excluded.level_state,
                        needs_review = excluded.needs_review, suggested = excluded.suggested, tags = excluded.tags,
                        confirmed_text = excluded.confirmed_text, provisional_text = excluded.provisional_text
                    """,
                    new
                    {
                        row.DocumentId,
                        row.Title,
                        row.Publisher,
                        row.Series,
                        row.Authors,
                        row.Year,
                        row.SystemLabel,
                        row.KindLabel,
                        row.LevelMin,
                        row.LevelMax,
                        levelState = LevelStateText(row.Levels),
                        row.Reviews,
                        row.Suggested,
                        row.Tags,
                        row.ConfirmedText,
                        row.ProvisionalText,
                    }, t);
                c.Execute("INSERT INTO doc_facet (document_id, field, value, label, confirmed) VALUES (@DocumentId, @Field, @Value, @Label, @Confirmed)",
                    row.Facets.DistinctBy(f => (f.Field, f.Value)).Select(f => new { row.DocumentId, f.Field, f.Value, f.Label, f.Confirmed }), t);
                RefreshDocSearch(c, t, row.DocumentId);
            }
        }, ct);

    /// <summary>
    /// Records which of <paramref name="documentIds"/> a model has read, and with which model, from
    /// <paramref name="readBy"/>; those not in it are no longer marked. With no ids, replaces every document's mark.
    /// </summary>
    public Task SetAiReadAsync(IReadOnlyCollection<long>? documentIds, IReadOnlyDictionary<long, string> readBy, CancellationToken ct = default) =>
        documentIds is { Count: 0 } ? Task.CompletedTask : writer.WriteAsync((c, t) =>
        {
            if (documentIds is null) c.Execute("DELETE FROM doc_ai", transaction: t);
            else c.Execute("DELETE FROM doc_ai WHERE document_id IN (SELECT value FROM json_each(@ids))", new { ids = JsonSerializer.Serialize(documentIds) }, t);
            c.Execute("INSERT INTO doc_ai (document_id, model) VALUES (@Key, @Value)",
                readBy.Where(r => documentIds is null || documentIds.Contains(r.Key)), t);
        }, ct);

    /// <summary>Drops the projected metadata of documents that no longer have any.</summary>
    public Task ClearMetadataAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default) =>
        documentIds.Count == 0 ? Task.CompletedTask : writer.WriteAsync((c, t) =>
        {
            foreach (var id in documentIds)
            {
                c.Execute("DELETE FROM doc_meta WHERE document_id = @id", new { id }, t);
                c.Execute("DELETE FROM doc_facet WHERE document_id = @id", new { id }, t);
                RefreshDocSearch(c, t, id);
            }
        }, ct);

    /// <summary>Replaces the term aliases that field search resolves names through.</summary>
    public Task SetTermAliasesAsync(IReadOnlyList<TermAliasRow> aliases, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            c.Execute("DELETE FROM term_alias", transaction: t);
            c.Execute("INSERT OR IGNORE INTO term_alias (vocabulary, alias, value) VALUES (@Vocabulary, @Alias, @Value)", aliases, t);
        }, ct);

    internal static string LevelStateText(LevelState state) => state switch
    {
        LevelState.Known => "known",
        LevelState.NotApplicable => "na",
        _ => "unknown",
    };

    /// <summary>
    /// Rebuilds a document's doc_fts row from its doc row and projected metadata. The effective title is the title;
    /// the file name's title, the PDF's own information and the folder names are provisional text.
    /// </summary>
    static void RefreshDocSearch(SqliteConnection c, SqliteTransaction t, long documentId)
    {
        c.Execute("DELETE FROM doc_fts WHERE rowid = @documentId", new { documentId }, t);
        c.Execute(
            """
            INSERT INTO doc_fts (rowid, title, subtitle, publisher, series, authors, tags, notes, confirmed, provisional)
            SELECT d.document_id, coalesce(m.title, d.display_title), coalesce(d.meta_subject, ''), coalesce(m.publisher, ''),
                   coalesce(m.series, ''), coalesce(m.authors, ''), coalesce(m.tags, ''), '', coalesce(m.confirmed_text, ''),
                   coalesce(m.provisional_text, '') || ' · ' || coalesce(d.meta_title, '') || ' · ' || coalesce(d.meta_author, '') || ' · '
                       || coalesce(d.meta_keywords, '') || ' · ' || coalesce(d.folder_hint, '')
                       || CASE WHEN m.title IS NOT NULL AND m.title <> d.display_title THEN ' · ' || d.display_title ELSE '' END
            FROM doc d LEFT JOIN doc_meta m ON m.document_id = d.document_id
            WHERE d.document_id = @documentId
            """,
            new { documentId }, t);
    }
}

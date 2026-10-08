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
                    now = JobQueue.Timestamp(_clock.GetUtcNow()),
                }, t);
            RefreshDocSearch(c, t, doc);

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

    /// <summary>Stores extracted or OCR'd text for a run of pages. The FTS triggers keep page_fts in step.</summary>
    public Task SetPageTextAsync(long documentId, IReadOnlyList<PageTextRow> pages, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            using var update = c.CreateCommand();
            update.Transaction = t;
            update.CommandText =
                """
                UPDATE page SET text = $text, text_source = $source, text_quality = $quality, needs_ocr = $needsOcr, error = $error
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

    /// <summary>Keeps doc_fts in step with a doc row. Until slice 2, title is the display title and the PDF's own information is provisional.</summary>
    static void RefreshDocSearch(SqliteConnection c, SqliteTransaction t, DocRow doc)
    {
        c.Execute("DELETE FROM doc_fts WHERE rowid = @DocumentId", new { doc.DocumentId }, t);
        var provisional = string.Join(" · ",
            new[] { doc.MetaTitle, doc.MetaAuthor, doc.MetaKeywords, doc.FolderHint }.Where(v => !string.IsNullOrWhiteSpace(v)));
        c.Execute(
            """
            INSERT INTO doc_fts (rowid, title, subtitle, publisher, series, tags, notes, confirmed, provisional)
            VALUES (@DocumentId, @DisplayTitle, @MetaSubject, '', '', '', '', '', @provisional)
            """,
            new { doc.DocumentId, doc.DisplayTitle, MetaSubject = doc.MetaSubject ?? "", provisional }, t);
    }
}

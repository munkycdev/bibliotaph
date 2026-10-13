using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>A chapter the AI found a value in: the book, the chapter's pages, and the page the evidence is on.</summary>
/// <param name="DocumentId">The file the chapter is in.</param>
/// <param name="EntryId">The card that shows the file, or an adventure inside it whose pages hold the value.</param>
/// <param name="BookTitle">The card's title.</param>
/// <param name="Chapter">The chapter's title.</param>
/// <param name="FirstPage">The chapter's first zero-based PDF page.</param>
/// <param name="LastPage">The chapter's last zero-based PDF page.</param>
/// <param name="PdfPage">The zero-based PDF page the evidence is on.</param>
/// <param name="Field">One of <see cref="ChapterFields.All"/>.</param>
/// <param name="Value">The term's key, or the creature's name in lower case.</param>
/// <param name="Label">The value as shown.</param>
public sealed record ChapterHit(long DocumentId, EntryId EntryId, string BookTitle, string Chapter, int FirstPage, int LastPage, int PdfPage, string Field,
    string Value, string Label);

/// <summary>A value found in chapters across the library, with how many books it was found in.</summary>
public sealed record ChapterValue(string Field, string Value, string Label, long Books);

/// <summary>
/// What the AI found reading whole books, chapter by chapter, as index.db holds it: the chapters of one file for its
/// panels, and the chapters and pages across the library that have a given setting, environment, theme or creature,
/// for questions asked of the whole library.
/// </summary>
public sealed class ChapterQueries(IndexDatabase database)
{
    /// <summary>A file's chapters in page order, with their values; none when it hasn't been scanned.</summary>
    public async Task<IReadOnlyList<ScannedChapter>> GetChaptersAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var chapters = (await connection.QueryAsync<(long Ord, string Title, long FirstPage, long LastPage, string Source, string Reading)>(new CommandDefinition(
            "SELECT ord, title, first_page, last_page, source, reading FROM chapter WHERE document_id = @documentId ORDER BY ord",
            new { documentId }, cancellationToken: ct))).ToList();
        if (chapters.Count == 0) return [];
        var tags = (await connection.QueryAsync<(long Ord, string Field, string Value, string Label, long PdfPage, string Quote)>(new CommandDefinition(
            "SELECT ord, field, value, label, pdf_page, quote FROM chapter_tag WHERE document_id = @documentId ORDER BY ord, pdf_page, label",
            new { documentId }, cancellationToken: ct))).ToLookup(g => g.Ord);
        return [.. chapters.Select(c => new ScannedChapter(c.Title, (int)c.FirstPage, (int)c.LastPage,
            Enum.TryParse<ChapterSource>(c.Source, out var source) ? source : ChapterSource.Pages,
            Enum.TryParse<ChapterReading>(c.Reading, out var reading) ? reading : ChapterReading.Waiting,
            [.. tags[c.Ord].Select(g => new ChapterTag(g.Field, g.Value, g.Label, (int)g.PdfPage, g.Quote))]))];
    }

    /// <summary>The files that have chapters in index.db, so a projection from catalog.db can take away those whose scan has gone.</summary>
    public async Task<IReadOnlySet<long>> GetScannedDocumentsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return (await connection.QueryAsync<long>(new CommandDefinition("SELECT DISTINCT document_id FROM chapter", cancellationToken: ct))).ToHashSet();
    }

    /// <summary>
    /// The chapters across the library with <paramref name="value"/> for <paramref name="field"/> (one of
    /// <see cref="ChapterFields.All"/>), book by book and in page order. A value matches a term by its key, its label
    /// or any of its names, and a creature by its name in the singular or the plural, whatever the case. Each hit is on
    /// the card that shows the file, or on an adventure inside it when the evidence is on the adventure's pages.
    /// </summary>
    public async Task<IReadOnlyList<ChapterHit>> FindAsync(string field, string value, int limit = 500, CancellationToken ct = default)
    {
        var alias = MetadataText.Normalize(value);
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<HitRow>(new CommandDefinition(
            """
            SELECT g.document_id AS DocumentId,
                   coalesce((SELECT p.entry_id FROM entry_doc p WHERE p.document_id = g.document_id AND p.first_page <= g.pdf_page
                             AND p.last_page >= g.pdf_page ORDER BY p.entry_id LIMIT 1), e.entry_id) AS EntryId,
                   coalesce(m.title, e.name, d.display_title, '') AS BookTitle, c.title AS Chapter, c.first_page AS FirstPage, c.last_page AS LastPage,
                   g.pdf_page AS PdfPage, g.field AS Field, g.value AS Value, g.label AS Label
            FROM chapter_tag g
                JOIN chapter c ON c.document_id = g.document_id AND c.ord = g.ord
                JOIN entry_doc e ON e.document_id = g.document_id AND e.first_page IS NULL
                LEFT JOIN entry_meta m ON m.entry_id = e.entry_id
                LEFT JOIN doc d ON d.document_id = g.document_id
            WHERE g.field = @field AND (g.value IN (@key, @alias, @singular) OR g.label = @value COLLATE NOCASE
                OR g.value IN (SELECT a.value FROM term_alias a WHERE a.vocabulary = g.field AND a.alias = @alias))
            ORDER BY BookTitle COLLATE NOCASE, g.document_id, c.ord, g.pdf_page
            LIMIT @limit
            """,
            new { field, value, key = value.Trim().ToLowerInvariant(), alias, singular = Singular(alias), limit }, cancellationToken: ct));
        return [.. rows.Select(r => new ChapterHit(r.DocumentId, new EntryId(r.EntryId), r.BookTitle, r.Chapter, (int)r.FirstPage, (int)r.LastPage, (int)r.PdfPage,
            r.Field, r.Value, r.Label))];
    }

    /// <summary>
    /// The values found in chapters across the library for <paramref name="field"/>, or for every field when it is
    /// null, with how many files each was found in, the most found first.
    /// </summary>
    public async Task<IReadOnlyList<ChapterValue>> GetValuesAsync(string? field = null, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. await connection.QueryAsync<ChapterValue>(new CommandDefinition(
            """
            SELECT field AS Field, value AS Value, CAST(min(label) AS TEXT) AS Label, count(DISTINCT document_id) AS Books
            FROM chapter_tag
            WHERE @field IS NULL OR field = @field
            GROUP BY field, value
            ORDER BY Books DESC, Label COLLATE NOCASE
            """,
            new { field }, cancellationToken: ct))];
    }

    // Creatures are kept by the name the book used, often in the singular, so "red dragons" finds "red dragon".
    static string Singular(string name) => name.Length > 3 && name.EndsWith('s') ? name[..^1] : name;

    // Properties rather than a constructor: SQLite doesn't type a column computed from a subquery, which Dapper's
    // constructor matching needs, while it converts each value it sets on a property.
    sealed class HitRow
    {
        public long DocumentId { get; init; }
        public long EntryId { get; init; }
        public string BookTitle { get; init; } = "";
        public string Chapter { get; init; } = "";
        public long FirstPage { get; init; }
        public long LastPage { get; init; }
        public long PdfPage { get; init; }
        public string Field { get; init; } = "";
        public string Value { get; init; } = "";
        public string Label { get; init; } = "";
    }
}

using System.Text.Json;
using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// A library card's pages that use any of a tag's words: its pages with text (its page range, for an adventure inside a
/// file), and the PDF pages, zero-based and in order, of the document it shows (<paramref name="ContentHash"/>) that
/// have the words.
/// </summary>
public sealed record WordPages(EntryId EntryId, string ContentHash, int TextPages, IReadOnlyList<int> Pages);

/// <summary>
/// What the user's own tags read from index.db: which cards' pages use a tag's words, through the same full-text index
/// as Inside documents search, so a tag never needs a book read again. A card is counted through the document it
/// shows. The FTS5 expression is built here from quoted words only, and reaches SQLite as a bound parameter.
/// </summary>
public sealed class UserTagQueries(IndexDatabase database)
{
    /// <summary>
    /// The books (whole files and adventures inside them) with a page that has any of <paramref name="words"/>, or only
    /// those showing <paramref name="documentId"/>. A word matches as in search, words that start with it included
    /// (zombie finds zombies); a phrase matches exactly.
    /// </summary>
    public async Task<IReadOnlyList<WordPages>> FindAsync(IReadOnlyCollection<string> words, long? documentId = null, CancellationToken ct = default)
    {
        if (Match(words) is not { } match) return [];
        await using var connection = database.OpenRead();
        var hits = (await connection.QueryAsync<(long EntryId, string ContentHash, long PdfPage)>(new CommandDefinition(
            """
            SELECT e.entry_id, d.content_hash, h.pdf_page
            FROM (SELECT p.document_id, p.pdf_page
                  FROM page_fts JOIN page p ON p.id = page_fts.rowid
                  WHERE page_fts MATCH @match AND (@documentId IS NULL OR p.document_id = @documentId)) h
            JOIN entry_doc e ON e.document_id = h.document_id AND e.kind IN ('Whole', 'Part')
                AND (e.first_page IS NULL OR h.pdf_page BETWEEN e.first_page AND e.last_page)
            JOIN doc d ON d.document_id = h.document_id
            ORDER BY e.entry_id, h.pdf_page
            """, new { match, documentId }, cancellationToken: ct))).ToList();
        if (hits.Count == 0) return [];

        var entries = hits.Select(h => h.EntryId).Distinct().ToList();
        var textPages = (await connection.QueryAsync<(long EntryId, long Pages)>(new CommandDefinition(
            """
            SELECT e.entry_id, count(*)
            FROM entry_doc e JOIN page p ON p.document_id = e.document_id
                AND (e.first_page IS NULL OR p.pdf_page BETWEEN e.first_page AND e.last_page)
            WHERE e.entry_id IN (SELECT value FROM json_each(@ids)) AND p.text <> ''
            GROUP BY e.entry_id
            """, new { ids = JsonSerializer.Serialize(entries) }, cancellationToken: ct))).ToDictionary(r => r.EntryId, r => (int)r.Pages);

        return [.. hits.GroupBy(h => (h.EntryId, h.ContentHash)).Select(g => new WordPages(new EntryId(g.Key.EntryId), g.Key.ContentHash,
            textPages.GetValueOrDefault(g.Key.EntryId), [.. g.Select(h => (int)h.PdfPage).Distinct()]))];
    }

    /// <summary>The books (whole files and adventures inside them) whose cards show <paramref name="documentId"/>.</summary>
    public async Task<IReadOnlyList<EntryId>> GetShowingAsync(long documentId, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return [.. (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT entry_id FROM entry_doc WHERE document_id = @documentId AND kind IN ('Whole', 'Part')", new { documentId }, cancellationToken: ct)))
            .Select(id => new EntryId(id))];
    }

    /// <summary>
    /// An FTS5 expression that finds any of the words: each quoted, a single word of <see cref="SearchPlan.MinWordStart"/>
    /// letters or more also as the start of a longer word, as the search box reads it. Null when there are no words.
    /// </summary>
    internal static string? Match(IEnumerable<string> words)
    {
        var parts = words.Select(w => w.Trim()).Where(w => w.Any(char.IsLetterOrDigit))
            .Select(w => SearchPlan.Quote(w) + (!w.Any(char.IsWhiteSpace) && w.Length >= SearchPlan.MinWordStart ? " *" : ""))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" OR ", parts);
    }
}

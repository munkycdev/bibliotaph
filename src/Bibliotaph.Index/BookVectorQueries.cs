using System.Text.Json;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// What a card's vector is made from: its words in entry_fts (title, subtitle, publisher, series, authors, tags, notes
/// and its confirmed and suggested values' names) and the names of what the AI found in its chapters, with the
/// vector it has now, if any: the model, the text version and the hash of the text it was made from.
/// </summary>
public sealed record BookProfileRow(
    long EntryId, long? DocumentId, string Title, string Subtitle, string Publisher, string Series, string Authors, string Tags, string Notes,
    string Confirmed, string Provisional, IReadOnlyList<string> Inside, string? Model, int? Version, string? Hash);

/// <summary>A card's vector by the embedding model asked for.</summary>
public sealed record BookVector(long EntryId, float[] Vector);

/// <summary>
/// The cards' vectors of what each is about, for search by meaning: what each is made from, and the vectors made so far.
/// Every value is a bound parameter.
/// </summary>
public sealed class BookVectorQueries(IndexDatabase database)
{
    /// <summary>
    /// Every card with what its vector is made from: the whole library at once, since it is a row a card and the
    /// vectors are checked against it in one pass. An adventure inside a file takes what was found in its own pages.
    /// </summary>
    public async Task<IReadOnlyList<BookProfileRow>> GetProfilesAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<ProfileRow>(new CommandDefinition(
            """
            SELECT e.entry_id AS EntryId, e.document_id AS DocumentId, e.first_page AS FirstPage, e.last_page AS LastPage,
                   coalesce(f.title, '') AS Title, coalesce(f.subtitle, '') AS Subtitle, coalesce(f.publisher, '') AS Publisher,
                   coalesce(f.series, '') AS Series, coalesce(f.authors, '') AS Authors, coalesce(f.tags, '') AS Tags, coalesce(f.notes, '') AS Notes,
                   coalesce(f.confirmed, '') AS Confirmed, coalesce(f.provisional, '') AS Provisional,
                   v.model AS Model, v.version AS Version, v.profile_hash AS Hash
            FROM entry_doc e
            LEFT JOIN entry_fts f ON f.rowid = e.entry_id
            LEFT JOIN entry_vec v ON v.entry_id = e.entry_id
            ORDER BY e.entry_id
            """, cancellationToken: ct));
        var found = (await connection.QueryAsync<(long DocumentId, long PdfPage, string Label)>(new CommandDefinition(
            "SELECT DISTINCT document_id, pdf_page, label FROM chapter_tag ORDER BY document_id, label", cancellationToken: ct)))
            .ToLookup(t => t.DocumentId);
        return [.. rows.Select(r => new BookProfileRow(r.EntryId, r.DocumentId, r.Title, r.Subtitle, r.Publisher, r.Series, r.Authors, r.Tags, r.Notes,
            r.Confirmed, r.Provisional,
            r.DocumentId is { } document
                ? [.. found[document].Where(t => t.PdfPage >= (r.FirstPage ?? 0) && t.PdfPage <= (r.LastPage ?? long.MaxValue)).Select(t => t.Label).Distinct()]
                : [],
            r.Model, (int?)r.Version, r.Hash))];
    }

    /// <summary>The vectors <paramref name="model"/> made of these cards at this text version; cards without one are left out.</summary>
    public async Task<IReadOnlyList<BookVector>> GetVectorsAsync(string model, int version, IReadOnlyCollection<long> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return [];
        await using var connection = database.OpenRead();
        var rows = await connection.QueryAsync<(long EntryId, byte[] Vector)>(new CommandDefinition(
            "SELECT entry_id, vector FROM entry_vec WHERE model = @model AND version = @version AND entry_id IN (SELECT value FROM json_each(@ids))",
            new { model, version, ids = JsonSerializer.Serialize(entryIds) }, cancellationToken: ct));
        return [.. rows.Select(r => new BookVector(r.EntryId, VectorBlob.FromBytes(r.Vector)))];
    }

    /// <summary>How many cards have a vector from this model and version.</summary>
    public async Task<int> CountAsync(string model, int version, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entry_vec WHERE model = @model AND version = @version", new { model, version }, cancellationToken: ct));
    }

    sealed class ProfileRow
    {
        public long EntryId { get; init; }
        public long? DocumentId { get; init; }
        public long? FirstPage { get; init; }
        public long? LastPage { get; init; }
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Publisher { get; init; } = "";
        public string Series { get; init; } = "";
        public string Authors { get; init; } = "";
        public string Tags { get; init; } = "";
        public string Notes { get; init; } = "";
        public string Confirmed { get; init; } = "";
        public string Provisional { get; init; } = "";
        public string? Model { get; init; }
        public long? Version { get; init; }
        public string? Hash { get; init; }
    }
}

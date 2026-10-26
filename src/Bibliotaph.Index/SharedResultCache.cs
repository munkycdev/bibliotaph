using System.Globalization;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// A result another of the user's devices worked out about a file, as taken from the web library: what it is
/// (<paramref name="Kind"/>), the prompt it was made with, its id on the device that made it, when it was made, and the
/// result itself as JSON.
/// </summary>
public sealed record CachedResult(string ContentHash, string Kind, int PromptVersion, string ResultId, DateTime FinishedUtc, string Content);

/// <summary>
/// The results taken from the web library, in index.db, by content hash: they wait here for a file with that content
/// to be in this library. Being derived, they go with an index.db rebuild, and are then taken again from the start.
/// </summary>
public sealed class SharedResultCache(IndexDatabase database, IndexWriter writer)
{
    /// <summary>Keeps results in place of older ones for the same content and kind.</summary>
    public Task PutAsync(IReadOnlyList<CachedResult> results, CancellationToken ct = default) =>
        results.Count == 0 ? Task.CompletedTask : writer.WriteAsync((c, t) => c.Execute(
            """
            INSERT INTO shared_result (content_hash, kind, prompt_version, result_id, finished_utc, content)
            VALUES (@ContentHash, @Kind, @PromptVersion, @ResultId, @finished, @Content)
            ON CONFLICT (content_hash, kind) DO UPDATE SET prompt_version = excluded.prompt_version, result_id = excluded.result_id,
                finished_utc = excluded.finished_utc, content = excluded.content
            """,
            results.Select(r => new { r.ContentHash, r.Kind, r.PromptVersion, r.ResultId, finished = Utc(r.FinishedUtc), r.Content }), t), ct);

    /// <summary>The results of <paramref name="kind"/> held for these contents, by content hash.</summary>
    public async Task<IReadOnlyDictionary<string, CachedResult>> GetAsync(string kind, IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        var found = new Dictionary<string, CachedResult>(StringComparer.Ordinal);
        if (contentHashes.Count == 0) return found;
        await using var connection = database.OpenRead();
        foreach (var hashes in contentHashes.Distinct().Chunk(500))
            foreach (var row in await connection.QueryAsync<(string Hash, long Version, string Id, string Finished, string Content)>(new CommandDefinition(
                "SELECT content_hash, prompt_version, result_id, finished_utc, content FROM shared_result WHERE kind = @kind AND content_hash IN @hashes",
                new { kind, hashes }, cancellationToken: ct)))
                found[row.Hash] = new CachedResult(row.Hash, kind, (int)row.Version, row.Id, Parse(row.Finished), row.Content);
        return found;
    }

    /// <summary>Every content hash a result of <paramref name="kind"/> is held for.</summary>
    public async Task<IReadOnlySet<string>> GetHashesAsync(string kind, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return (await connection.QueryAsync<string>(new CommandDefinition("SELECT content_hash FROM shared_result WHERE kind = @kind",
            new { kind }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT value FROM shared_state WHERE key = @key", new { key },
            cancellationToken: ct));
    }

    public Task SetStateAsync(string key, string value, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) => c.Execute(
            "INSERT INTO shared_state (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", new { key, value }, t), ct);

    /// <summary>The values kept under keys starting with <paramref name="prefix"/>, by key.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetStatesAsync(string prefix, CancellationToken ct = default)
    {
        await using var connection = database.OpenRead();
        return (await connection.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT key, value FROM shared_state WHERE substr(key, 1, length(@prefix)) = @prefix", new { prefix }, cancellationToken: ct)))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public Task DeleteStateAsync(string key, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) => c.Execute("DELETE FROM shared_state WHERE key = @key", new { key }, t), ct);

    static string Utc(DateTime when) => DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    static DateTime Parse(string when) => DateTime.Parse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

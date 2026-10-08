using Dapper;

namespace Bibliotaph.Index;

public sealed record QueueSummary(long Pending, long Failed)
{
    public bool IsIdle => Pending == 0;
}

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
}

using Azure.Storage.Blobs;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Accounts;

/// <summary>What is kept for a user outside the server's disk: the libraries' replicas and the covers.</summary>
public interface IUserBlobs
{
    Task DeleteAllAsync(string userId, CancellationToken ct);
}

/// <summary>On a developer's machine and in tests, nothing is kept elsewhere.</summary>
public sealed class NoUserBlobs : IUserBlobs
{
    public Task DeleteAllAsync(string userId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Every blob under users/{user ID}/ in each container, as Litestream and the cover store name them.</summary>
public sealed class BlobUserBlobs(IReadOnlyList<(BlobContainerClient Container, string Root)> places) : IUserBlobs
{
    public async Task DeleteAllAsync(string userId, CancellationToken ct)
    {
        foreach (var (container, root) in places)
        {
            var prefix = $"{root}users/{UserIds.Require(userId)}/";
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
                await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: ct);
        }
    }

    /// <summary>
    /// The container and path in a Litestream replica URL (abs://account@container/path), which Litestream reaches at
    /// the account's blob endpoint.
    /// </summary>
    public static (Uri Container, string Root) Replica(string url)
    {
        var replica = new Uri(url);
        if (replica.Scheme != "abs" || string.IsNullOrEmpty(replica.UserInfo))
            throw new InvalidOperationException("Server:ReplicaUrl isn't an abs://account@container address.");
        var path = replica.AbsolutePath.Trim('/');
        return (new Uri($"https://{replica.UserInfo}.blob.core.windows.net/{replica.Host}"), path.Length == 0 ? "" : path + "/");
    }
}

/// <summary>
/// Deletes what is kept for a closed account: its library's folder and backups on this disk, its replicas and its
/// covers. Litestream may still send a library's last changes for a moment after its files go, so each closed account
/// is purged again, hourly, until a purge comes <see cref="Settle"/> after it was closed; a purge that fails is tried
/// again the same way.
/// </summary>
public sealed class AccountPurge(AccountService accounts, UserLibraries libraries, IUserBlobs blobs, TimeProvider clock,
    ILogger<AccountPurge> log) : BackgroundService
{
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);

    public async Task PurgeAsync(ClosedAccount account, CancellationToken ct)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        await libraries.DeleteAsync(account.UserId);
        await blobs.DeleteAllAsync(account.UserId, ct);
        if (started - account.Closed >= Settle) await accounts.MarkPurgedAsync(account.UserId, ct);
        log.LogInformation("Deleted what was kept for closed account {User}", account.UserId);
    }

    public async Task PurgeAllAsync(CancellationToken ct)
    {
        foreach (var account in await accounts.UnpurgedAsync(ct))
        {
            try
            {
                await PurgeAsync(account, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Couldn't delete everything kept for closed account {User}; trying again within the hour", account.UserId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do await PurgeAllAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

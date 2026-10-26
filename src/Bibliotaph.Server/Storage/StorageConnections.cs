using System.Security.Cryptography;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Storage;

/// <summary>A drive or account a user connected, as one row of storage_connections, before its tokens are read.</summary>
public sealed record ConnectedAccount(string Id, string UserId, string Provider, string AccountId, string? DisplayName, string? AccountKind,
    bool NeedsReconnect, DateTime Created);

/// <summary>
/// The storage each user connected, in accounts.db. Refresh tokens are kept encrypted with Data Protection, whose key
/// ring Key Vault wraps in Azure, and are only ever decrypted here, on the server.
/// </summary>
public sealed class StorageConnections(AccountsDatabase database, IDataProtectionProvider protection, TimeProvider clock)
{
    readonly IDataProtector _tokens = protection.CreateProtector("Bibliotaph.Storage.RefreshTokens");

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Connects an account, or refreshes the tokens of one connected before (a reconnect).</summary>
    public async Task<ConnectedAccount> SaveAsync(string userId, string provider, string accountId, string? displayName, string? accountKind,
        string refreshToken, string? scopes, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var connection = await db.StorageConnections.SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == provider && c.AccountId == accountId, ct);
        if (connection is null)
        {
            connection = new StorageConnection
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Provider = provider,
                AccountId = accountId,
                ProtectedRefreshToken = "",
                NotificationSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Created = Now,
            };
            db.StorageConnections.Add(connection);
        }
        connection.DisplayName = displayName;
        connection.AccountKind = accountKind;
        connection.ProtectedRefreshToken = _tokens.Protect(refreshToken);
        connection.Scopes = scopes;
        connection.NeedsReconnect = false;
        connection.Updated = Now;
        await db.SaveChangesAsync(ct);
        return Summary(connection);
    }

    public async Task<IReadOnlyList<ConnectedAccount>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return (await db.StorageConnections.Where(c => c.UserId == userId).OrderBy(c => c.Created).ToListAsync(ct)).Select(Summary).ToList();
    }

    /// <summary>Every connection of every user, for renewing change notifications.</summary>
    public async Task<IReadOnlyList<StorageConnection>> AllAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.StorageConnections.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>Every user's connection to one account at a provider, as a provider's change notice names only the account.</summary>
    public async Task<IReadOnlyList<StorageConnection>> ForAccountAsync(string provider, string accountId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.StorageConnections.AsNoTracking().Where(c => c.Provider == provider && c.AccountId == accountId).ToListAsync(ct);
    }

    public async Task<StorageConnection?> FindAsync(string id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.StorageConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>Forgets a connection and its tokens. Returns it, so its change notifications can be cancelled.</summary>
    public async Task<StorageConnection?> RemoveAsync(string userId, string id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var connection = await db.StorageConnections.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (connection is null) return null;
        db.StorageConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return connection;
    }

    public string RefreshToken(StorageConnection connection) => _tokens.Unprotect(connection.ProtectedRefreshToken);

    /// <summary>Providers may hand out a new refresh token with each access token; the newest is the one to keep.</summary>
    public Task SaveRefreshTokenAsync(string id, string refreshToken, CancellationToken ct = default) =>
        UpdateAsync(id, c => c.ProtectedRefreshToken = _tokens.Protect(refreshToken), ct);

    public Task SetNeedsReconnectAsync(string id, CancellationToken ct = default) => UpdateAsync(id, c => c.NeedsReconnect = true, ct);

    public Task SetSubscriptionAsync(string id, string? subscriptionId, DateTimeOffset? expires, CancellationToken ct = default) =>
        UpdateAsync(id, c => (c.SubscriptionId, c.SubscriptionExpires) = (subscriptionId, expires?.UtcDateTime), ct);

    async Task UpdateAsync(string id, Action<StorageConnection> change, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        if (await db.StorageConnections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } connection) return;
        change(connection);
        connection.Updated = Now;
        await db.SaveChangesAsync(ct);
    }

    static ConnectedAccount Summary(StorageConnection c) =>
        new(c.Id, c.UserId, c.Provider, c.AccountId, c.DisplayName, c.AccountKind, c.NeedsReconnect, DateTime.SpecifyKind(c.Created, DateTimeKind.Utc));
}

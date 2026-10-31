using Bibliotaph.Server.Storage;

namespace Bibliotaph.Server.Accounts;

/// <summary>
/// Closing an account, by its owner or an admin: its drive notifications are cancelled while the tokens still work,
/// the account goes, and everything Bibliotaph keeps for it is deleted. Nothing in the user's storage is touched.
/// </summary>
public sealed class AccountClosing(AccountService accounts, StorageConnections connections, DriveNotifications notifications, DriveTokens tokens,
    AccountPurge purge, TimeProvider clock, ILogger<AccountClosing> log)
{
    /// <summary>False when there was no such account.</summary>
    public async Task<bool> CloseAsync(string userId, CancellationToken ct)
    {
        userId = UserIds.Require(userId);
        foreach (var account in await connections.ListAsync(userId, ct))
        {
            if (await connections.FindAsync(account.Id, ct) is { } connection) await notifications.UnsubscribeAsync(connection, ct);
            tokens.Forget(account.Id);
        }
        if (!await accounts.CloseAsync(userId, ct)) return false;
        try
        {
            await purge.PurgeAsync(new ClosedAccount { UserId = userId, Closed = clock.GetUtcNow().UtcDateTime }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The account is closed; what's left of it goes at the next hourly purge.
            log.LogWarning(ex, "Couldn't delete everything kept for closed account {User} yet", userId);
        }
        return true;
    }
}

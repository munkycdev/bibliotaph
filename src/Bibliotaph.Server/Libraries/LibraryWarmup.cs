using Bibliotaph.Server.Accounts;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// After a deploy every library's databases are only in Blob storage, and bringing them back can take minutes. This
/// opens the libraries of the people who signed in lately, one at a time, so someone coming back after an update finds
/// theirs open or well on its way rather than waiting on all of it. It waits first: the server this one replaced may
/// still be making its last writes, and a restore taken before them would leave them out.
/// </summary>
public sealed class LibraryWarmup(UserLibraries libraries, AccountService accounts, TimeProvider clock, ILogger<LibraryWarmup> log) : BackgroundService
{
    /// <summary>Longer than Container Apps takes to move traffic over and stop the old replica.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(2);

    static readonly TimeSpan Recent = TimeSpan.FromDays(30);

    /// <summary>Each stays open until it has been idle a while, and they share one container's memory.</summary>
    const int MostLibraries = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Delay, clock, stoppingToken);
            var since = clock.GetUtcNow().UtcDateTime - Recent;
            foreach (var user in (await accounts.UsersAsync(stoppingToken)).Where(u => u.LastSignIn >= since).Take(MostLibraries))
            {
                try
                {
                    await using var lease = await libraries.OpenAsync(user.Id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The first request tries again; one library that won't open mustn't keep the others waiting.
                    log.LogWarning(ex, "Couldn't open the library of {User} ahead of its first request", user.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping before the libraries were warm: each opens on its first request instead.
        }
    }
}

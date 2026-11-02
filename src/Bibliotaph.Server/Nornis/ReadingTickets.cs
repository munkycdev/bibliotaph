using System.Security.Cryptography;
using System.Text;
using Bibliotaph.Server.Accounts;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// The links Nornis gives a player to read one shared book. A code is 32 random bytes, of which only the SHA-256 is
/// kept; it's good for one document, once, for two minutes, since Nornis sends the player's browser to it at once.
/// </summary>
public sealed class ReadingTickets(AccountsDatabase accounts, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>How long a used or expired ticket is kept before it's forgotten.</summary>
    static readonly TimeSpan Kept = TimeSpan.FromDays(1);

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>A new ticket for the book, and its code, which is given out once and never kept.</summary>
    public async Task<(string Code, DateTime ExpiresUtc)> MintAsync(string userId, Guid worldId, long documentId, long entryId, int? startPage,
        string? viewerName, string renew, CancellationToken ct)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = Now;
        await using var db = accounts.CreateContext();
        db.ReadingTickets.Add(new ReadingTicket
        {
            CodeHash = Hash(code),
            UserId = userId,
            WorldId = worldId.ToString("D"),
            DocumentId = documentId,
            EntryId = entryId,
            StartPage = startPage,
            ViewerName = viewerName,
            Renew = renew,
            CreatedUtc = now,
            ExpiresUtc = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        var forgotten = now - Kept;
        await db.ReadingTickets.Where(t => t.ExpiresUtc < forgotten).ExecuteDeleteAsync(ct);
        return (code, now + Lifetime);
    }

    /// <summary>The ticket for a code, marked used, or null when it's unknown, used or expired: one update decides, so two tries can't both win.</summary>
    public async Task<ReadingTicket?> RedeemAsync(string code, CancellationToken ct)
    {
        if (code.Length is 0 or > 64) return null;
        var hash = Hash(code);
        var now = Now;
        await using var db = accounts.CreateContext();
        var won = await db.ReadingTickets.Where(t => t.CodeHash == hash && t.RedeemedUtc == null && t.ExpiresUtc > now)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RedeemedUtc, now), ct);
        return won == 1 ? await db.ReadingTickets.AsNoTracking().SingleAsync(t => t.CodeHash == hash, ct) : null;
    }

    static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

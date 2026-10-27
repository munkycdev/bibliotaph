using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Accounts;

/// <summary>
/// Who a sign-in provider says someone is, after the provider-specific claims are read. <see cref="Subject"/> is the
/// provider's stable ID for the person, never their email address.
/// </summary>
public sealed record ExternalIdentity(string Provider, string Subject, string? Name, string? Email);

public enum SignInResult
{
    SignedIn,

    /// <summary>A new account was made from an invitation.</summary>
    Created,

    /// <summary>The sign-in was added to the account already signed in.</summary>
    Linked,

    /// <summary>No account has this sign-in, and no invitation came with it.</summary>
    NeedsInvite,

    /// <summary>The invitation is unknown, used or expired.</summary>
    InviteNotValid,

    /// <summary>Linking was asked for, but this sign-in already belongs to another account.</summary>
    BelongsToSomeoneElse,

    /// <summary>Sign-up is open, but Bibliotaph has as many accounts as it takes; an invitation still makes one.</summary>
    Full,
}

/// <summary>
/// The "Accounts:SignUp" section: whether anyone who signs in gets an account without an invitation, and how many
/// accounts there may be before only invitations make more. Closed unless set, as the service is invite-only.
/// </summary>
public sealed record SignUpOptions
{
    public const string Section = "Accounts:SignUp";

    public bool Open { get; init; }

    /// <summary>Accounts in all, invited ones included, past which sign-up without an invitation stops.</summary>
    public int MostAccounts { get; init; } = 50;
}

public sealed record SignInOutcome(SignInResult Result, User? User);

/// <summary>
/// Accounts, sign-ins and invitations. An account starts from a one-time link, or, when sign-up is open
/// (<see cref="SignUpOptions"/>), from signing in, until there are as many accounts as the server takes.
/// </summary>
public sealed class AccountService(AccountsDatabase database, TimeProvider clock, SignUpOptions? signUp = null)
{
    readonly SignUpOptions _signUp = signUp ?? new SignUpOptions();

    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

    /// <summary>How long an account found open is taken to be open, so every request doesn't look it up.</summary>
    static readonly TimeSpan OpenFor = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, DateTimeOffset> _open = new();

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Finishes a sign-in. A known sign-in signs into its account. An unknown one links to the account already signed in
    /// (<paramref name="currentUserId"/>), or makes a new account if it came with a valid invitation, and is refused otherwise.
    /// </summary>
    public async Task<SignInOutcome> SignInAsync(ExternalIdentity identity, string? inviteCode, string? currentUserId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var login = await db.Logins.FindAsync([identity.Provider, identity.Subject], ct);
        if (login is not null)
        {
            if (currentUserId is not null && currentUserId != login.UserId) return new(SignInResult.BelongsToSomeoneElse, null);
            var known = await db.Users.FindAsync([login.UserId], ct);
            if (known is null) return new(SignInResult.NeedsInvite, null);
            known.LastSignIn = Now;
            if (identity.Email is not null) login.Email = identity.Email;
            await db.SaveChangesAsync(ct);
            return new(SignInResult.SignedIn, known);
        }

        if (currentUserId is not null && await db.Users.FindAsync([currentUserId], ct) is { } current)
        {
            db.Logins.Add(NewLogin(identity, current.Id));
            current.LastSignIn = Now;
            await db.SaveChangesAsync(ct);
            return new(SignInResult.Linked, current);
        }

        Invite? invite = null;
        if (string.IsNullOrEmpty(inviteCode))
        {
            if (!_signUp.Open) return new(SignInResult.NeedsInvite, null);
            if (await db.Users.CountAsync(ct) >= _signUp.MostAccounts) return new(SignInResult.Full, null);
        }
        else
        {
            var hash = Hash(inviteCode);
            invite = await db.Invites.SingleOrDefaultAsync(i => i.CodeHash == hash, ct);
            if (invite is null || invite.Redeemed is not null || invite.Expires <= Now) return new(SignInResult.InviteNotValid, null);
        }

        var user = new User
        {
            Id = UserIds.New(),
            Name = string.IsNullOrWhiteSpace(identity.Name) ? identity.Email ?? "Reader" : identity.Name.Trim(),
            Email = identity.Email,
            IsAdmin = invite?.Admin == true,
            Created = Now,
            LastSignIn = Now,
        };
        db.Users.Add(user);
        db.Logins.Add(NewLogin(identity, user.Id));
        if (invite is not null)
        {
            invite.Redeemed = Now;
            invite.RedeemedBy = user.Id;
        }
        await db.SaveChangesAsync(ct);
        return new(SignInResult.Created, user);
    }

    Login NewLogin(ExternalIdentity identity, string userId) =>
        new() { Provider = identity.Provider, Subject = identity.Subject, UserId = userId, Email = identity.Email, Added = Now };

    public async Task<User?> FindUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Users.FindAsync([userId], ct);
    }

    /// <summary>Every account, the most recently signed in first.</summary>
    public async Task<IReadOnlyList<User>> UsersAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Users.AsNoTracking().OrderByDescending(u => u.LastSignIn).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Login>> LoginsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Logins.Where(l => l.UserId == userId).OrderBy(l => l.Added).ToListAsync(ct);
    }

    /// <summary>
    /// Whether the account exists. A session outlives an account closed in another browser, so each request asks; the
    /// answer is kept for a minute, and closing an account forgets it at once.
    /// </summary>
    public async Task<bool> IsOpenAsync(string userId, CancellationToken ct = default)
    {
        if (_open.TryGetValue(userId, out var until) && until > clock.GetUtcNow()) return true;
        await using var db = database.CreateContext();
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) return false;
        _open[userId] = clock.GetUtcNow() + OpenFor;
        return true;
    }

    /// <summary>
    /// Closes an account: the account, its sign-ins and its storage connections go, and it is recorded as closed so
    /// its files are deleted. Returns false when there was no such account.
    /// </summary>
    public async Task<bool> CloseAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        if (await db.Users.FindAsync([userId], ct) is not { } user) return false;
        db.Users.Remove(user);
        if (await db.ClosedAccounts.FindAsync([userId], ct) is null) db.ClosedAccounts.Add(new ClosedAccount { UserId = userId, Closed = Now });
        await db.SaveChangesAsync(ct);
        _open.TryRemove(userId, out _);
        return true;
    }

    /// <summary>Closed accounts some of whose files may be left.</summary>
    public async Task<IReadOnlyList<ClosedAccount>> UnpurgedAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.ClosedAccounts.AsNoTracking().Where(c => c.Purged == null).ToListAsync(ct);
    }

    public async Task MarkPurgedAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        if (await db.ClosedAccounts.FindAsync([userId], ct) is not { } closed) return;
        closed.Purged = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Makes an invitation and returns its code, which is never stored and can't be shown again.</summary>
    public async Task<(string Code, Invite Invite)> CreateInviteAsync(string? createdBy, string? note, bool admin, CancellationToken ct = default)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var invite = new Invite
        {
            Id = Guid.NewGuid().ToString("N"),
            CodeHash = Hash(code),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Admin = admin,
            CreatedBy = createdBy,
            Created = Now,
            Expires = Now + InviteLifetime,
        };
        await using var db = database.CreateContext();
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        return (code, invite);
    }

    public async Task<IReadOnlyList<Invite>> InvitesAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Invites.OrderByDescending(i => i.Created).ToListAsync(ct);
    }

    static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

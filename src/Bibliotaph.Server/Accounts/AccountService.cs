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
}

public sealed record SignInOutcome(SignInResult Result, User? User);

/// <summary>Accounts, sign-ins and invitations. Bibliotaph is invite-only: an account starts from a one-time link.</summary>
public sealed class AccountService(AccountsDatabase database, TimeProvider clock)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(14);

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

        if (string.IsNullOrEmpty(inviteCode)) return new(SignInResult.NeedsInvite, null);
        var hash = Hash(inviteCode);
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.CodeHash == hash, ct);
        if (invite is null || invite.Redeemed is not null || invite.Expires <= Now) return new(SignInResult.InviteNotValid, null);

        var user = new User
        {
            Id = UserIds.New(),
            Name = string.IsNullOrWhiteSpace(identity.Name) ? identity.Email ?? "Reader" : identity.Name.Trim(),
            Email = identity.Email,
            IsAdmin = invite.Admin,
            Created = Now,
            LastSignIn = Now,
        };
        db.Users.Add(user);
        db.Logins.Add(NewLogin(identity, user.Id));
        invite.Redeemed = Now;
        invite.RedeemedBy = user.Id;
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

    public async Task<IReadOnlyList<Login>> LoginsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.Logins.Where(l => l.UserId == userId).OrderBy(l => l.Added).ToListAsync(ct);
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

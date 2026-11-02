using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Accounts;

/// <summary>
/// Who a sign-in provider says someone is, after the provider-specific claims are read. <see cref="Subject"/> is the
/// provider's stable ID for the person, never their email address. <see cref="FormerSubjects"/> are other spellings the
/// provider has given the same person, which a sign-in saved under one of them is moved from.
/// </summary>
public sealed record ExternalIdentity(string Provider, string Subject, string? Name, string? Email)
{
    public IReadOnlyList<string> FormerSubjects { get; init; } = [];
}

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
    public Task<SignInOutcome> SignInAsync(ExternalIdentity identity, string? inviteCode, string? currentUserId, CancellationToken ct = default) =>
        SignInAsync(identity, inviteCode, currentUserId, openSignUp: false, ct);

    /// <summary>
    /// Finishes a sign-in that came through Connect Nornis. Nornis's tokens will carry this subject, so an account it
    /// already belongs to is the one signed in, even over another session; an unknown one is linked to whoever is signed
    /// in, or makes an account without an invitation, within the most accounts.
    /// </summary>
    public async Task<SignInOutcome> SignInFromNornisAsync(ExternalIdentity identity, string? currentUserId, CancellationToken ct = default)
    {
        var known = await FindBySubjectAsync(identity.Provider, identity.Subject, identity.FormerSubjects, ct);
        return await SignInAsync(identity, null, known is null ? currentUserId : null, openSignUp: true, ct);
    }

    async Task<SignInOutcome> SignInAsync(ExternalIdentity identity, string? inviteCode, string? currentUserId, bool openSignUp, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var login = await db.Logins.FindAsync([identity.Provider, identity.Subject], ct) ?? await MoveFormerAsync(db, identity, ct);
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
            if (!_signUp.Open && !openSignUp) return new(SignInResult.NeedsInvite, null);
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
            // A used invitation's link is no use to anyone, so its code isn't kept.
            invite.Code = null;
        }
        await db.SaveChangesAsync(ct);
        return new(SignInResult.Created, user);
    }

    /// <summary>A sign-in saved under one of the person's former subjects, moved to the one the provider gives now.</summary>
    static async Task<Login?> MoveFormerAsync(AccountsDbContext db, ExternalIdentity identity, CancellationToken ct)
    {
        foreach (var former in identity.FormerSubjects)
        {
            if (await db.Logins.FindAsync([identity.Provider, former], ct) is not { } old) continue;
            // The subject is part of the key, so the row is replaced rather than changed.
            db.Logins.Remove(old);
            var moved = new Login { Provider = old.Provider, Subject = identity.Subject, UserId = old.UserId, Email = old.Email, Added = old.Added };
            db.Logins.Add(moved);
            return moved;
        }
        return null;
    }

    /// <summary>
    /// The open account a sign-in belongs to, by its subject or one of its former spellings, or null. Only reads: a
    /// token another app presents doesn't move a login, the next sign-in here does.
    /// </summary>
    public async Task<User?> FindBySubjectAsync(string provider, string subject, IReadOnlyList<string> formerSubjects, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        foreach (var candidate in formerSubjects.Prepend(subject))
        {
            if (await db.Logins.AsNoTracking().SingleOrDefaultAsync(l => l.Provider == provider && l.Subject == candidate, ct) is not { } login) continue;
            return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == login.UserId, ct) is { } user && await IsOpenAsync(user.Id, ct)
                ? user
                : null;
        }
        return null;
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

    /// <summary>
    /// Makes an invitation and returns its code, which is kept until the invitation is used, so
    /// <see cref="InviteCodeAsync"/> can give it again meanwhile.
    /// </summary>
    public async Task<(string Code, Invite Invite)> CreateInviteAsync(string? createdBy, string? note, bool admin, CancellationToken ct = default)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var invite = new Invite
        {
            Id = Guid.NewGuid().ToString("N"),
            CodeHash = Hash(code),
            Code = code,
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

    /// <summary>
    /// The code of an open invitation, for copying its link again; null for one unknown, used or expired, or one made
    /// before codes were kept.
    /// </summary>
    public async Task<string?> InviteCodeAsync(string id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        if (await db.Invites.FindAsync([id], ct) is not { Code: { } code } invite) return null;
        return invite.Redeemed is null && invite.Expires > Now ? code : null;
    }

    /// <summary>
    /// Deletes an invitation. An open one's link stops working at once; a used one's account stays. Returns whether
    /// there was one to delete.
    /// </summary>
    public async Task<bool> DeleteInviteAsync(string id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        if (await db.Invites.FindAsync([id], ct) is not { } invite) return false;
        db.Invites.Remove(invite);
        await db.SaveChangesAsync(ct);
        return true;
    }

    static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

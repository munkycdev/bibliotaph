namespace Bibliotaph.Api.Contracts;

/// <summary>A way to sign in, as the sign-in page lists it. <see cref="Id"/> is its key in the sign-in URL, such as microsoft.</summary>
public sealed record SignInProvider(string Id, string Name);

/// <summary>The signed-in user. GET /api/me answers 401 when nobody is signed in.</summary>
public sealed record CurrentUser(string UserId, string Name, string? Email, bool IsAdmin, IReadOnlyList<LinkedSignIn> SignIns);

public sealed record LinkedSignIn(string Provider, string? Email, DateTimeOffset Added);

/// <summary>How much is in the signed-in user's library.</summary>
public sealed record LibrarySummary(int Books, int Folders);

/// <summary>
/// Asks for a new invitation; only an admin may. <see cref="Note"/> says who it's for, so the admin can tell invitations
/// apart, and is never shown to the invitee. <see cref="Admin"/> lets the account it makes invite others.
/// </summary>
public sealed record CreateInvite(string? Note, bool Admin = false);

/// <summary>A new invitation. <see cref="Link"/> is shown once: only a hash of its code is kept.</summary>
public sealed record CreatedInvite(string Link, DateTimeOffset Expires);

public sealed record InviteInfo(string Id, string? Note, bool Admin, DateTimeOffset Created, DateTimeOffset Expires, DateTimeOffset? Redeemed);

/// <summary>Request headers and values the API and its clients agree on.</summary>
public static class ApiHeaders
{
    /// <summary>
    /// Sent with every API call from Bibliotaph's own pages. A form or page on another site can't add a custom header
    /// without a CORS preflight, which the server never grants, so a call without it is refused as cross-site.
    /// </summary>
    public const string Client = "X-Bibliotaph";
}

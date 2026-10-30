namespace Bibliotaph.Api.Contracts;

/// <summary>A way to sign in, as the sign-in page lists it: <see cref="Id"/> is its key in the sign-in URL, such as auth0, and <see cref="Label"/> its button's words.</summary>
public sealed record SignInProvider(string Id, string Label);

/// <summary>Signed out of Bibliotaph: where the page goes next, which ends the identity provider's session too.</summary>
public sealed record SignedOut(string Next);

/// <summary>The signed-in user. GET /api/me answers 401 when nobody is signed in.</summary>
public sealed record CurrentUser(string UserId, string Name, string? Email, bool IsAdmin, IReadOnlyList<LinkedSignIn> SignIns);

/// <summary>A way into the account. <see cref="Provider"/> names it as Settings shows it, by the connection it came through, such as Google.</summary>
public sealed record LinkedSignIn(string Provider, string? Email, DateTimeOffset Added);

/// <summary>
/// How much is in the signed-in user's library. <see cref="Totals"/> is Home's line before its link to the Summary page,
/// "1,284 books, 96 GB.", once the Library shows a book.
/// </summary>
public sealed record LibraryOverview(int Books, int Folders, string? Totals = null);

/// <summary>
/// Asks for a new invitation; only an admin may. <see cref="Note"/> says who it's for, so the admin can tell invitations
/// apart, and is never shown to the invitee. <see cref="Admin"/> lets the account it makes invite others.
/// </summary>
public sealed record CreateInvite(string? Note, bool Admin = false);

/// <summary>An invitation's link: a new one's, or an open one's again from GET /api/invites/{id}/link.</summary>
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

/// <summary>An account, as an admin's list in Settings shows it; <see cref="IsYou"/> is the admin's own.</summary>
public sealed record AccountInfo(string Id, string Name, string? Email, bool IsAdmin, DateTimeOffset Created, DateTimeOffset LastSignIn, bool IsYou);

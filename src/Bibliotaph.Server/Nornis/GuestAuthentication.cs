using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// One book a player may read: whose library, through which world, the document and its card, where Nornis renews
/// the player's hour, and when the hour ends. It never slides: Nornis checks again each hour.
/// </summary>
public sealed record GuestPass(string UserId, string WorldId, long DocumentId, long EntryId, string Renew, DateTime ExpiresUtc, int? StartPage);

/// <summary>
/// The guest scheme: a player's browser, by the __Host-bibliotaph-guest cookie a reading ticket gave it. The cookie
/// holds up to five books' passes, protected with Data Protection. It's never the default scheme and carries no user
/// claim, so it reaches nothing but the guest endpoints, and each of those only for a document it holds.
/// </summary>
public sealed class GuestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IDataProtectionProvider protection, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "guest";
    public const string Policy = "guest";
    public const string Cookie = "__Host-bibliotaph-guest";
    public const string PassClaim = "bib_guest_pass";

    /// <summary>The most books one browser holds passes for; a sixth drops the oldest.</summary>
    public const int MostBooks = 5;

    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    const string Purpose = "Bibliotaph.Nornis.GuestPasses";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var passes = Read(Request, protection, clock);
        if (passes.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(passes.Select(p => new Claim(PassClaim, JsonSerializer.Serialize(p))), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    /// <summary>The passes still good in the request's cookie; none when it's missing, expired or wasn't made here.</summary>
    public static IReadOnlyList<GuestPass> Read(HttpRequest request, IDataProtectionProvider protection, TimeProvider clock)
    {
        if (!request.Cookies.TryGetValue(Cookie, out var value) || value.Length == 0) return [];
        try
        {
            var passes = JsonSerializer.Deserialize<List<GuestPass>>(protection.CreateProtector(Purpose).Unprotect(value)) ?? [];
            var now = clock.GetUtcNow().UtcDateTime;
            return [.. passes.Where(p => p.ExpiresUtc > now)];
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Sets the cookie to these passes, the newest five, ending when the last of them does.</summary>
    public static void Write(HttpResponse response, IReadOnlyList<GuestPass> passes, IDataProtectionProvider protection)
    {
        var kept = passes.OrderByDescending(p => p.ExpiresUtc).Take(MostBooks).ToList();
        response.Cookies.Append(Cookie, protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(kept)), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = kept.Max(p => p.ExpiresUtc),
            IsEssential = true,
        });
    }
}

public static class GuestPrincipal
{
    /// <summary>The books a guest may read, or none for anyone else.</summary>
    public static IReadOnlyList<GuestPass> GuestPasses(this ClaimsPrincipal principal) =>
        [.. principal.FindAll(GuestAuthentication.PassClaim).Select(c => JsonSerializer.Deserialize<GuestPass>(c.Value)!)];

    /// <summary>The pass for this document, if the guest holds one.</summary>
    public static GuestPass? GuestPass(this ClaimsPrincipal principal, long documentId) =>
        principal.GuestPasses().FirstOrDefault(p => p.DocumentId == documentId);
}

using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Api;

/// <summary>/api: JSON for the web client. Every call needs a session; calls that change something need the client header.</summary>
public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        // Connecting a PC: partly without a session, and partly with the PC's own key instead.
        app.MapDevicesApi();
        app.MapSyncApi();

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", async (ClaimsPrincipal principal, AccountService accounts, CancellationToken ct) =>
        {
            if (principal.UserId() is not { } id || await accounts.FindUserAsync(id, ct) is not { } user) return Results.Unauthorized();
            var logins = await accounts.LoginsAsync(id, ct);
            return Results.Ok(new CurrentUser(user.Id, user.Name, user.Email, user.IsAdmin,
                [.. logins.Select(l => new LinkedSignIn(Auth0SignIn.Describe(l.Provider, l.Subject), l.Email, Utc(l.Added)))]));
        });

        // Closing an account deletes it and everything Bibliotaph keeps for it; nothing in the user's storage is touched.
        api.MapDelete("/me", async (ClaimsPrincipal principal, HttpContext context, AccountClosing closing, IConfiguration configuration,
            IOptions<ServerOptions> options, CancellationToken ct) =>
        {
            if (!await closing.CloseAsync(principal.UserId()!, ct)) return Results.NotFound();
            await context.SignOutAsync(AuthSetup.SessionScheme);
            return Results.Ok(AuthEndpoints.AfterSignOut(AuthSetup.Auth0(configuration), options.Value, context.Request));
        });

        api.MapGet("/library/summary", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await using var db = lease.Library.Catalog.CreateContext();
            return new LibrarySummary(await db.Entries.CountAsync(ct), await db.SourceRoots.CountAsync(ct));
        });

        api.MapStorageApi();
        api.MapLibraryApi();
        api.MapDetailsApi();
        api.MapEditApi();
        api.MapCollectionsApi();
        api.MapSavedSearchesApi();
        api.MapPinsApi();
        api.MapSelectionApi();
        api.MapBindersApi();
        api.MapReviewApi();
        api.MapBackupApi();
        api.MapElsewhereApi();
        api.MapReaderApi();
        api.MapPageNotesApi();
        api.MapAiApi();
        api.MapAskApi();

        var invites = api.MapGroup("/invites").RequireAuthorization(AuthSetup.AdminPolicy);

        invites.MapGet("/", async (AccountService accounts, CancellationToken ct) =>
            (await accounts.InvitesAsync(ct)).Select(i => new InviteInfo(i.Id, i.Note, i.Admin, Utc(i.Created), Utc(i.Expires), i.Redeemed is { } r ? Utc(r) : null)));

        invites.MapPost("/", async (CreateInvite request, ClaimsPrincipal principal, HttpContext context, AccountService accounts,
            IOptions<ServerOptions> options, CancellationToken ct) =>
        {
            var (code, invite) = await accounts.CreateInviteAsync(principal.UserId(), request.Note, request.Admin, ct);
            return new CreatedInvite(InviteLink(options.Value, context.Request, code), Utc(invite.Expires));
        });

        // An admin's list of accounts, and deleting one as Close account would: the account's own sessions end at
        // their next request. An admin closes their own account with Close account instead.
        var people = api.MapGroup("/accounts").RequireAuthorization(AuthSetup.AdminPolicy);

        people.MapGet("/", async (ClaimsPrincipal principal, AccountService accounts, CancellationToken ct) =>
            (await accounts.UsersAsync(ct)).Select(u => new AccountInfo(u.Id, u.Name, u.Email, u.IsAdmin, Utc(u.Created), Utc(u.LastSignIn), u.Id == principal.UserId())));

        people.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, AccountService accounts, AccountClosing closing, CancellationToken ct) =>
        {
            if (id == principal.UserId())
                return Results.Problem("Close your own account with Close account, at the top of Settings.", statusCode: StatusCodes.Status400BadRequest);
            if (await accounts.FindUserAsync(id, ct) is not { } user || !await closing.CloseAsync(id, ct)) return Results.NotFound();
            return Results.Ok(new ActionResult($"Deleted {user.Name}'s account and everything Bibliotaph kept for it."));
        });

        // An unknown API path is a 404, never the web client's page.
        api.MapFallback(() => Results.NotFound()).AllowAnonymous();
    }

    public static string InviteLink(ServerOptions options, HttpRequest? request, string code)
    {
        return $"{AuthEndpoints.PublicOrigin(options, request)}/join?invite={Uri.EscapeDataString(code)}";
    }

    static DateTimeOffset Utc(DateTime stored) => new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));
}

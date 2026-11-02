using System.Globalization;
using System.Net;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// A player reading a game master's book through Nornis: the reading ticket's link, which turns into the guest cookie,
/// and the guest reader's own few calls under /api/table, each for a document the cookie holds and still in its world.
/// </summary>
public static class TableApi
{
    public static void MapTableApi(this IEndpointRouteBuilder app)
    {
        if (NornisOptions.From(app.ServiceProvider.GetRequiredService<IConfiguration>()) is not { } nornis) return;
        var log = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bibliotaph.Server.Nornis.Table");

        app.MapGet("/table/{code}", async (string code, HttpContext context, ReadingTickets tickets, AccountService accounts,
            IDataProtectionProvider protection, TimeProvider clock, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await tickets.RedeemAsync(code, ct) is not { } ticket || !await accounts.IsOpenAsync(ticket.UserId, ct))
                return Results.Content(Expired(nornis.Origin!), "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone);
            var pass = new GuestPass(ticket.UserId, ticket.WorldId, ticket.DocumentId, ticket.EntryId, ticket.Renew,
                clock.GetUtcNow().UtcDateTime + GuestAuthentication.Hour, ticket.StartPage);
            // One pass a document: the guest pages are addressed by document, so the newest ticket for it wins.
            var passes = GuestAuthentication.Read(context.Request, protection, clock).Where(p => p.DocumentId != pass.DocumentId).Append(pass).ToList();
            GuestAuthentication.Write(context.Response, passes, protection);
            log.LogInformation("Reading ticket redeemed for user {UserId}, world {WorldId}, document {DocumentId}, viewer {Viewer}",
                ticket.UserId, ticket.WorldId, ticket.DocumentId, ticket.ViewerName ?? "");
            var at = ticket.StartPage is { } page ? $"?page={page.ToString(CultureInfo.InvariantCulture)}" : "";
            return Results.Redirect($"/guest/{ticket.DocumentId.ToString(CultureInfo.InvariantCulture)}{at}");
        }).AllowAnonymous().RequireRateLimiting(NornisAuthentication.TableLimit);

        var table = app.MapGroup("/api/table/{documentId:long}").RequireAuthorization(GuestAuthentication.Policy).RequireRateLimiting(ReadLinks.ReadLimit);

        table.MapGet("/reader", async (long documentId, int? page, ClaimsPrincipal principal, UserLibraries libraries, AccountService accounts,
            CancellationToken ct) =>
        {
            if (principal.GuestPass(documentId) is not { } pass) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(pass.UserId, ct);
            if (await WorldNameAsync(lease.Library, pass, ct) is not { } world) return Results.NotFound();
            if (await ReaderApi.OpenAsync(lease.Library, new EntryId(pass.EntryId), documentId, page ?? pass.StartPage, null, false, ct, guest: true) is not { } book)
                return Results.NotFound();
            var sharedBy = (await accounts.FindUserAsync(pass.UserId, ct))?.Name ?? "the game master";
            return Results.Ok(new GuestBook(book, sharedBy, world, pass.Renew, new DateTimeOffset(pass.ExpiresUtc, TimeSpan.Zero)));
        });

        table.MapGet("/link", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, ReadLinks links, CancellationToken ct) =>
        {
            if (principal.GuestPass(documentId) is not { } pass) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(pass.UserId, ct);
            if (await WorldNameAsync(lease.Library, pass, ct) is null) return Results.NotFound();
            return Results.Ok(await ReaderApi.LinkAsync(lease.Library, links, documentId, ct));
        });

        table.MapGet("/printed-pages", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (principal.GuestPass(documentId) is not { } pass) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(pass.UserId, ct);
            if (await WorldNameAsync(lease.Library, pass, ct) is null) return Results.NotFound();
            return Results.Ok(await ReaderApi.PrintedPagesAsync(lease.Library, documentId, ct));
        });

        table.MapGet("/find", async (long documentId, string q, int? from, int? to, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (principal.GuestPass(documentId) is not { } pass) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(pass.UserId, ct);
            if (await WorldNameAsync(lease.Library, pass, ct) is null) return Results.NotFound();
            var texts = await lease.Library.Get<IndexQueries>().GetPageTextsAsync(documentId, ct);
            return Results.Ok(new ReaderFind(ReaderApi.Find(texts.Select(t => (t.PdfPage, t.Text)), q, from, to)));
        });
    }

    /// <summary>
    /// The world's name while the pass's book is still in it, or null once the world is gone, has lost its collection or
    /// no longer holds the book: the game master's change takes effect at once, not when the hour is up.
    /// </summary>
    static async Task<string?> WorldNameAsync(UserLibrary library, GuestPass pass, CancellationToken ct)
    {
        if (!Guid.TryParse(pass.WorldId, out var worldId) || await NornisReads.WorldAsync(library, worldId, ct) is not { } world
            || !world.Pages.Holds(pass.DocumentId))
            return null;
        await using var catalog = await library.Contexts.CreateDbContextAsync(ct);
        return await catalog.NornisWorlds.AsNoTracking().Where(w => w.WorldId == pass.WorldId).Select(w => w.Name).SingleOrDefaultAsync(ct);
    }

    static string Expired(string origin) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Link used or expired · Bibliotaph</title>
          <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
          <link rel="stylesheet" href="/css/bibliotaph.css" />
        </head>
        <body class="bt-site">
          <main class="bt-policy">
            <h1>This link has been used or has expired.</h1>
            <p>Open the book again from Nornis.</p>
            <p><a class="bt-button primary" href="{WebUtility.HtmlEncode(origin)}">Go to Nornis</a></p>
          </main>
        </body>
        </html>
        """;
}

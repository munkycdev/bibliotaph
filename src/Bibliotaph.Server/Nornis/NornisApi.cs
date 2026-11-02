using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// /api/nornis/v1: what Nornis calls on a game master's behalf, with the nornis scheme only, each endpoint behind its
/// scope and every call counted against the user's limit.
/// </summary>
public static class NornisApi
{
    public static void MapNornisApi(this IEndpointRouteBuilder app)
    {
        if (NornisOptions.From(app.ServiceProvider.GetRequiredService<IConfiguration>()) is not { } options) return;
        var nornis = app.MapGroup("/api/nornis/v1").RequireRateLimiting(NornisAuthentication.Limit);
        // Made now, so it's there to apply each library's inbox as the library opens.
        app.ServiceProvider.GetRequiredService<NornisWorlds>();

        // Sent when a member opens a world, so the idle close and a deploy's restore rarely land on a question.
        nornis.MapPost("/warm", (ClaimsPrincipal principal, UserLibraries libraries) =>
        {
            libraries.BeginOpening(principal.UserId()!);
            return Results.Accepted();
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.BooksRead));

        // Nornis's outbox retries anything but a 2xx, so a batch is answered only once it's all written, or not at all.
        nornis.MapPost("/worlds/events", async (NornisWorldEvent[]? events, ClaimsPrincipal principal, NornisWorlds worlds, CancellationToken ct) =>
        {
            if (events is not { Length: > 0 and <= NornisWorldEvent.MostInBatch } || events.Any(e => e is null || !e.IsComplete))
                return NornisProblems.Refuse(StatusCodes.Status400BadRequest, "malformed",
                    $"Send 1 to {NornisWorldEvent.MostInBatch} events, each with an eventId, a known kind, a worldId, a worldName of 1 to {NornisWorldEvent.LongestName} characters and occurredAt.");
            await worlds.ReceiveAsync(principal.UserId()!, events, ct);
            return Results.Accepted();
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.WorldsWrite));

        var books = NornisAuthentication.Policy(NornisAuthentication.BooksRead);

        nornis.MapGet("/worlds/{worldId:guid}/books", async (Guid worldId, ClaimsPrincipal principal, UserLibraries libraries, HttpContext context,
            CancellationToken ct) =>
        {
            var (lease, refusal) = await NornisReads.LeaseAsync(libraries, principal.UserId()!, context, ct);
            if (lease is null) return refusal!;
            await using (lease)
            {
                if (await NornisReads.WorldAsync(lease.Library, worldId, ct) is not { } world) return Results.NotFound();
                var subtitles = await lease.Library.Get<LibraryQueries>().GetSubtitlesAsync([.. world.Entries.Select(e => e.EntryId)], ct);
                return Results.Ok(new NornisBooks([.. world.Entries.Select(e => new NornisBook(e.EntryId.Value, e.DocumentId, e.Title,
                    subtitles.GetValueOrDefault(e.EntryId), e.Kind, e.System, e.Pages?.Count ?? e.PageCount, e.Format, e.Cover))]));
            }
        }).RequireAuthorization(books);

        nornis.MapGet("/covers/{name}", async (string name, ClaimsPrincipal principal, UserLibraries libraries, HttpContext context, CancellationToken ct) =>
        {
            if (!LibraryApi.IsCoverName(name)) return Results.NotFound();
            var (lease, refusal) = await NornisReads.LeaseAsync(libraries, principal.UserId()!, context, ct);
            if (lease is null) return refusal!;
            await using (lease)
            {
                if (!(await NornisReads.CoversAsync(lease.Library, ct)).Contains(name)) return Results.NotFound();
                var path = lease.Library.Get<CoverCache>().PathFor(name);
                if (!await lease.Library.Get<CoverMirror>().RestoreAsync(name, ct)) return Results.NotFound();
                // A cover's name changes with its image; private, as it's one user's.
                context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                var modified = File.GetLastWriteTimeUtc(path);
                return Results.File(path, "image/jpeg", lastModified: modified,
                    entityTag: new EntityTagHeaderValue($"\"{modified.Ticks.ToString(CultureInfo.InvariantCulture)}\""));
            }
        }).RequireAuthorization(books);

        nornis.MapPost("/worlds/{worldId:guid}/passages", async (Guid worldId, NornisPassageQuestion? asked, ClaimsPrincipal principal,
            UserLibraries libraries, HttpContext context, CancellationToken ct) =>
        {
            var question = asked?.Question?.Trim() ?? "";
            var mode = asked?.Mode ?? NornisPassageQuestion.Ask;
            if (question.Length is 0 or > NornisPassageQuestion.LongestQuestion || asked!.Limit is < 1 or > NornisPassageQuestion.MostPages
                || mode is not (NornisPassageQuestion.Ask or NornisPassageQuestion.Search))
                return NornisProblems.Refuse(StatusCodes.Status400BadRequest, "malformed",
                    $"Ask a question of 1 to {NornisPassageQuestion.LongestQuestion} characters for 1 to {NornisPassageQuestion.MostPages} pages, in mode ask or search.");
            var (lease, refusal) = await NornisReads.LeaseAsync(libraries, principal.UserId()!, context, ct);
            if (lease is null) return refusal!;
            await using (lease)
            {
                if (await NornisReads.WorldAsync(lease.Library, worldId, ct) is not { } world) return Results.NotFound();
                var filter = asked.EntryIds is { } only
                    ? world.Filter with { Scope = [.. world.Filter.Scope!.Intersect(only.Select(id => new EntryId(id)))] }
                    : world.Filter;
                var search = mode == NornisPassageQuestion.Search;
                var findings = await lease.Library.Get<LibraryAskService>().FindAsync(question,
                    [new BookFilter(filter, Bibliotaph.Index.SearchPlan.From(Bibliotaph.Core.Search.SearchQuery.Parse("")))],
                    new LibraryAskWay(NeedsAi: false, EvenIfWeak: search, MaxPages: asked.Limit ?? NornisPassageQuestion.MostPages), ct);
                var pages = findings.Evidence
                    .Select(e => world.EntryFor(e.DocumentId, e.PdfPage) is { } entry
                        ? new NornisPassage(entry.EntryId.Value, e.DocumentId, entry.Title, e.PdfPage, e.Page, e.Text, e.Score)
                        : null)
                    .OfType<NornisPassage>().ToList();
                var meaning = findings.Meaning != AskMeaning.Off && findings.MeaningProblem is null && findings.Coverage.PagesWithVectors > 0;
                return Results.Ok(new NornisPassages(!findings.Weak && pages.Count > 0, meaning, pages));
            }
        }).RequireAuthorization(books);

        nornis.MapGet("/worlds/{worldId:guid}/documents/{documentId:long}/pages", async (Guid worldId, long documentId, int? from, int? to,
            ClaimsPrincipal principal, UserLibraries libraries, HttpContext context, CancellationToken ct) =>
        {
            if (from is not { } first || to is not { } last || first < 0 || last < first || last - first + 1 > NornisPageTexts.MostPages)
                return NornisProblems.Refuse(StatusCodes.Status400BadRequest, "malformed",
                    $"Ask for pages from and to, zero-based and inclusive, at most {NornisPageTexts.MostPages} of them.");
            var (lease, refusal) = await NornisReads.LeaseAsync(libraries, principal.UserId()!, context, ct);
            if (lease is null) return refusal!;
            await using (lease)
            {
                if (await NornisReads.WorldAsync(lease.Library, worldId, ct) is not { } world || !world.Pages.Holds(documentId)) return Results.NotFound();
                if (await lease.Library.Get<LibraryAskService>().IsKeptFromAiAsync(documentId, ct))
                    return NornisProblems.Refuse(StatusCodes.Status403Forbidden, NornisProblems.KeptFromAi, "This book is in a folder kept from AI.");
                var rows = await lease.Library.Get<LibraryAskQueries>().GetDocumentPagesAsync(documentId, first, last, ct);
                // An adventure inside a file lends only its own pages.
                return Results.Ok(new NornisPageTexts(documentId, [.. rows.Where(r => world.Pages.Contains(documentId, r.PdfPage))
                    .Select(r => new NornisPageText(r.PdfPage, PageNumbers.Display(r.PdfPage, r.Label), r.Text))]));
            }
        }).RequireAuthorization(books);

        // Nornis has checked the player may read the book; this checks the book is in the world.
        nornis.MapPost("/worlds/{worldId:guid}/reading-tickets", async (Guid worldId, NornisTicketRequest? asked, ClaimsPrincipal principal,
            UserLibraries libraries, ReadingTickets tickets, IOptions<ServerOptions> server, HttpContext context, ILoggerFactory logs,
            CancellationToken ct) =>
        {
            // The name goes into the logs, so it can't carry a line break or any other control character.
            var viewer = asked?.ViewerName is { } name
                ? new string([.. name.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Where(c => !char.IsControl(c))]).Trim()
                : null;
            if (asked is not { DocumentId: > 0 } || !options.IsOwn(asked.Renew) || asked.StartPage is < 0
                || viewer is { Length: > NornisTicketRequest.LongestViewerName })
                return NornisProblems.Refuse(StatusCodes.Status400BadRequest, "malformed",
                    $"Send a documentId, a renew address on Nornis, an optional startPage of 0 or more and a viewerName of at most {NornisTicketRequest.LongestViewerName} characters.");
            var userId = principal.UserId()!;
            var (lease, refusal) = await NornisReads.LeaseAsync(libraries, userId, context, ct);
            if (lease is null) return refusal!;
            await using (lease)
            {
                if (await NornisReads.WorldAsync(lease.Library, worldId, ct) is not { } world || !world.Pages.Holds(asked.DocumentId)) return Results.NotFound();
                // A page outside the world's pages of the file (another adventure in it) opens where the world's book starts.
                var start = asked.StartPage is { } page && world.Pages.Contains(asked.DocumentId, page) ? asked.StartPage : null;
                var entry = world.EntryFor(asked.DocumentId, start ?? -1) ?? world.Entries.First(e => e.DocumentId == asked.DocumentId);
                var (code, expires) = await tickets.MintAsync(userId, worldId, asked.DocumentId, entry.EntryId.Value, start,
                    viewer is { Length: > 0 } ? viewer : null, asked.Renew, ct);
                logs.CreateLogger("Bibliotaph.Server.Nornis.Table").LogInformation(
                    "Reading ticket minted for user {UserId}, world {WorldId}, document {DocumentId}, viewer {Viewer}", userId, worldId, asked.DocumentId, viewer ?? "");
                return Results.Ok(new NornisTicket($"{AuthEndpoints.PublicOrigin(server.Value, context.Request)}/table/{code}",
                    new DateTimeOffset(expires, TimeSpan.Zero)));
            }
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.TicketsWrite)).RequireRateLimiting(NornisAuthentication.TicketLimit);
    }
}

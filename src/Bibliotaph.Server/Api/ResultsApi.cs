using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Http.Features;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/results: what the user's devices worked out about their files, shared by content (<see cref="SharedResults"/>).
/// Only a PC's key reaches it. A PC sends its own results and asks for the others' since the last it had, the web
/// library's among them, and asks for a file's OCR by itself; results sent are given to the web library's own files
/// straight after.
/// </summary>
public static class ResultsApi
{
    /// <summary>The most taken at once: far beyond a batch of a hundred results of a few kilobytes, or one book's OCR.</summary>
    const long MaxUpload = 64L * 1024 * 1024;

    public static void MapResultsApi(this IEndpointRouteBuilder app)
    {
        var results = app.MapGroup("/api/results").RequireAuthorization(AuthSetup.DevicePolicy);

        // Asked every minute by each connected PC, so it reads sync.db without opening the library.
        results.MapGet("/", async (long? after, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            await (await libraries.SyncFilesAsync(principal.UserId()!, ct)).ReadResultsAsync(Math.Max(0, after ?? 0), principal.DeviceId()!, ct));

        // OCR, asked for by file when a PC is about to read one.
        results.MapGet("/{kind}/{hash}", async (string kind, string hash, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            SharedResultKinds.OnRequest(kind) && await (await libraries.SyncFilesAsync(principal.UserId()!, ct)).ReadResultAsync(kind, hash, ct) is { } found
                ? Results.Ok(found)
                : Results.NotFound());

        results.MapPost("/", async (HttpRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxUpload) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxUpload;
            var upload = await request.ReadFromJsonAsync<SharedResultUpload>(ct);
            if (upload?.Results is not { } sent || sent.Any(r => r is null || string.IsNullOrEmpty(r.ContentHash) || string.IsNullOrEmpty(r.Kind) || string.IsNullOrEmpty(r.Id)))
                return Results.Problem("Those aren't results Bibliotaph can keep.", statusCode: StatusCodes.Status400BadRequest);
            var userId = principal.UserId()!;
            var kept = await (await libraries.SyncFilesAsync(userId, ct)).WriteResultsAsync(principal.DeviceId()!, sent, ct);
            if (kept > 0)
            {
                await using var lease = await libraries.OpenAsync(userId, ct);
                lease.Library.Get<SharedResults>().Nudge();
            }
            return Results.Ok(new SharedResultsKept(kept));
        });
    }
}

using System.Net;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/pc: the web library as a connected PC sees it, so the books in the user's storage show on the PC beside its own
/// folders' without the PC holding a storage sign-in. Only a PC's key reaches it. The PC lists the files (the shelf),
/// takes what the server read from each instead of reading the file itself, and downloads a file only to open it.
/// </summary>
public static class PcLibraryApi
{
    /// <summary>More ISBNs than any book prints: a request with more is no book's.</summary>
    const int MostIsbns = 20;

    public static void MapPcLibraryApi(this IEndpointRouteBuilder app)
    {
        var pc = app.MapGroup("/api/pc").RequireAuthorization(AuthSetup.DevicePolicy);

        // Asked every minute by each connected PC, so it reads sync.db without opening the library, unless the library
        // has never kept a shelf there.
        pc.MapGet("/library", async (string? version, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            var files = await libraries.SyncFilesAsync(userId, ct);
            if (await files.ReadShelfAsync(ct) is not { } shelf)
            {
                await using (var lease = await libraries.OpenAsync(userId, ct)) await lease.Library.Get<WebShelfPublisher>().PublishAsync(ct);
                shelf = await files.ReadShelfAsync(ct) ?? throw new InvalidOperationException("The shelf wasn't kept.");
            }
            return shelf.Version == version ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(shelf.Content, "application/json");
        });

        // A PC's own books looked up where the server can ask: the catalogues it holds keys for, which no PC is given,
        // at the pace every library's lookups keep. Only ISBNs come, and nothing about them is kept.
        pc.MapGet("/lookups", (IBookCatalogue catalogue) => Results.Ok(new PcLookupAnswer(catalogue.Name)));
        pc.MapPost("/lookups", async (PcLookupRequest request, IBookCatalogue catalogue, CancellationToken ct) =>
        {
            if (request.Isbns is not { Count: > 0 and <= MostIsbns } isbns || !isbns.All(i => i is { Length: 13 } && i.All(char.IsAsciiDigit)))
                return Results.BadRequest();
            try
            {
                return await catalogue.FindAsync(isbns, ct) is { } r
                    ? Results.Ok(new PcLookupAnswer(catalogue.Name, new PcBookRecord(r.Source, r.Key, r.Isbn, r.Title, r.Authors, r.Publishers, r.Published)))
                    : Results.Ok(new PcLookupAnswer(catalogue.Name));
            }
            catch (CatalogueUnavailableException ex)
            {
                return Results.Json(new PcLookupAnswer(catalogue.Name, Problem: ex.Message), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        pc.MapGet("/books/{hash}/reading", async (string hash, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            // Conflict: the server couldn't read the file, so the PC reads it itself.
            return await lease.Library.Get<SharedReadings>().ReadAsync(hash, ct) switch
            {
                { Reading: { } reading } => Results.Ok(reading),
                { ReadHere: true } => Results.StatusCode(StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        pc.MapGet("/covers/{name}", async (string name, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (!LibraryApi.IsCoverName(name)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (!await lease.Library.Get<CoverMirror>().RestoreAsync(name, ct)) return Results.NotFound();
            return Results.File(lease.Library.Get<CoverCache>().PathFor(name), "image/jpeg");
        });

        // The whole file, to open: from storage that gives links anyone may read, the PC is sent there; otherwise the
        // server reads it, counted against the user's reads for the day as the browser's reader's are.
        pc.MapGet("/books/{hash}/file", async (string hash, HttpContext context, ClaimsPrincipal principal, UserLibraries libraries, ReadLinks links,
            CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            if (!ContentHash.TryParse(hash, out var content)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(userId, ct);
            var store = lease.Library.Get<LibraryStore>();
            if (await store.FindDocumentAsync(content, ct) is not { } documentId || await store.GetSourceAsync(documentId, ct) is not { } source)
                return Results.NotFound();
            foreach (var path in new[] { source.FullPath }.Concat(source.AllPaths).Distinct())
            {
                if (lease.Library.Drives.Locate(path) is not { } found) continue;
                var (drive, at) = found;
                try
                {
                    if (drive.HasReadLinks)
                    {
                        if (await drive.ReadLinkAsync(at.Path, ct) is { } url) return Results.Redirect(url);
                        continue;
                    }
                    if (await drive.ItemAsync(at.Path, ct) is not { IsFolder: false } item) continue;
                    if (!links.TryRead(userId, item.Size))
                        return Results.Problem("That's as much as Bibliotaph reads from your storage in a day. Try again tomorrow.",
                            statusCode: StatusCodes.Status429TooManyRequests);
                    var body = await drive.OpenReadAsync(at.Path, ct: ct);
                    context.Response.Headers.CacheControl = "private, no-store";
                    return Results.Stream(body, "application/octet-stream");
                }
                catch (StorageException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    // Another copy may still be there.
                }
                catch (Exception ex) when (ex is StorageException or HttpRequestException)
                {
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }
            }
            return Results.NotFound();
        });
    }
}

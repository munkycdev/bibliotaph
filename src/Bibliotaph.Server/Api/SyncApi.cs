using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Sync;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Http.Features;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/sync: a connected PC's library sync with the web library, which stands in for the folder two PCs would share.
/// Only a PC's key reaches it. The PC sends its own sync file and lists and fetches the others', the web library's
/// own among them; a file sent is merged into the web library at once, so the others see it on their next look.
/// </summary>
public static class SyncApi
{
    /// <summary>The largest sync file taken: far beyond a big library's, which is a few megabytes.</summary>
    const long MaxFile = 128L * 1024 * 1024;

    public static void MapSyncApi(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").RequireAuthorization(AuthSetup.DevicePolicy);

        // Asked every minute by each connected PC, so it lists from sync.db without opening the library.
        sync.MapGet("/", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var files = await libraries.SyncFilesAsync(principal.UserId()!, ct);
            return (await files.ListAsync(ct)).Where(f => f.Device != principal.DeviceId()).Select(f => new SyncFileInfo(f.Device, f.Machine, f.Version));
        });

        sync.MapGet("/{device}", async (string device, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var files = await libraries.SyncFilesAsync(principal.UserId()!, ct);
            return await files.ReadAsync(device, ct) is { } content ? Results.Bytes(content, "application/json") : Results.NotFound();
        });

        sync.MapPut("/mine", async (HttpRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxFile) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            // Read into memory and copied once to be kept.
            if (!UploadMemory.ReserveFor(request.HttpContext, MaxFile, copies: 2)) return UploadMemory.BusyResult(request.HttpContext);
            // Above the server's usual limit for a request, which a big library's file may pass.
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxFile;
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxFile) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            SyncFileContent content;
            try
            {
                buffer.Position = 0;
                content = await SyncFile.ReadAsync(buffer, ct);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem($"That isn't a sync file Bibliotaph can read. {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
            var userId = principal.UserId()!;
            var files = await libraries.SyncFilesAsync(userId, ct);
            await files.WriteAsync(principal.DeviceId()!, content.Machine, buffer.ToArray(), ct);
            // Merged now rather than when the web library is next opened, so the user's other devices see it soon.
            await using var lease = await libraries.OpenAsync(userId, ct);
            lease.Library.Get<ServerSyncChannel>().Received();
            await lease.Library.Get<LibrarySync>().SyncNowAsync(ct);
            return Results.NoContent();
        });
    }
}

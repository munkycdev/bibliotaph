using System.Net;
using System.Net.Http.Headers;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Short-lived links that read a book's byte ranges through the server, for storage whose own links a page on this site
/// can't read. The reader fetches without cookies, as it does a provider's link, so the link itself says whose book it
/// is: the user and the file, sealed with Data Protection, for an hour.
/// </summary>
public sealed class ReadLinks(IDataProtectionProvider protection, TimeProvider clock)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("Bibliotaph.Storage.ReadLinks").ToTimeLimitedDataProtector();

    public string For(string userId, string path) =>
        $"/storage/read/{_protector.Protect($"{userId}\n{path}", clock.GetUtcNow() + Lifetime)}";

    /// <returns>The user and file a link reads, or null for a link that's expired or wasn't made here.</returns>
    public (string UserId, string Path)? Read(string token)
    {
        try
        {
            var parts = _protector.Unprotect(token, out var expires).Split('\n', 2);
            return parts.Length == 2 && expires > clock.GetUtcNow() ? (parts[0], parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static void Map(IEndpointRouteBuilder storage) =>
        // Anonymous, as the reader sends no cookie; the link proves itself.
        storage.MapGet("/read/{token}", async (string token, HttpContext context, ReadLinks links, UserLibraries libraries, CancellationToken ct) =>
        {
            if (links.Read(token) is not { } link) return Results.StatusCode(StatusCodes.Status410Gone);
            var (userId, path) = link;
            // The reader always asks for a closed range, a chunk at a time.
            if (!RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var header) || header.Unit != "bytes"
                || header.Ranges.Count != 1 || header.Ranges.Single() is not { From: { } from, To: { } to } || from < 0 || to < from)
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);

            await using var lease = await libraries.OpenAsync(userId, ct);
            if (lease.Library.Drives.Locate(path) is not { } found) return Results.NotFound();
            Stream body;
            try
            {
                body = await found.Drive.OpenReadAsync(found.Location.Path, new ByteRange(from, to), ct);
            }
            catch (StorageException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is StorageException or HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.ContentRange = $"bytes {from}-{to}/*";
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = "application/octet-stream";
            await using (body) await body.CopyToAsync(context.Response.Body, ct);
            return Results.Empty;
        }).AllowAnonymous();
}

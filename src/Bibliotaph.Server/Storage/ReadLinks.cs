using System.Net;
using System.Net.Http.Headers;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Short-lived links that read a book's byte ranges through the server, for storage whose own links a page on this site
/// can't read. The link says whose book it is (the user and the file, sealed with Data Protection, for an hour), and
/// only that user's own session may follow it: otherwise a link copied out of the page would let anyone download the
/// book through bibliotaph.online. Each read is a few chunks at most, and a user's reads through the server are capped
/// each day, since every byte is the server's to send.
/// </summary>
public sealed class ReadLinks(IDataProtectionProvider protection, TimeProvider clock, IConfiguration configuration)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>The reader asks for at most eight 1 MB chunks at once; this leaves room and no more.</summary>
    public const long MostBytesARead = 16L * 1024 * 1024;

    /// <summary>The rate limiter policy: one user's reads in a minute.</summary>
    public const string ReadLimit = "read";

    /// <summary>A day of reading big books, several times over, unless "Storage:ReadBytesPerDay" says otherwise.</summary>
    readonly long _perDay = configuration.GetValue("Storage:ReadBytesPerDay", 10L * 1024 * 1024 * 1024);

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateOnly Day, long Bytes)> _read = new(StringComparer.Ordinal);

    readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("Bibliotaph.Storage.ReadLinks").ToTimeLimitedDataProtector();

    public string For(string userId, string path) =>
        $"/storage/read/{_protector.Protect($"{userId}\n{path}", clock.GetUtcNow() + Lifetime)}";

    /// <summary>
    /// Counts <paramref name="bytes"/> against the user's reads today; false, counting nothing, when they would go past
    /// the day's limit. Kept in memory, so a restart starts the day again.
    /// </summary>
    public bool TryRead(string userId, long bytes)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        while (true)
        {
            var had = _read.GetValueOrDefault(userId);
            var used = had.Day == today ? had.Bytes : 0;
            if (used + bytes > _perDay) return false;
            if (_read.TryGetValue(userId, out var current) ? _read.TryUpdate(userId, (today, used + bytes), current) : _read.TryAdd(userId, (today, used + bytes)))
                return true;
        }
    }

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
        // A signed-out read answers 401 itself rather than through the API's sign-in, so the reader can ask for a new link.
        storage.MapGet("/read/{token}", async (string token, HttpContext context, ReadLinks links, UserLibraries libraries, CancellationToken ct) =>
        {
            if (links.Read(token) is not { } link) return Results.StatusCode(StatusCodes.Status410Gone);
            var (userId, path) = link;
            if (context.User.UserId() is not { } reader) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (reader != userId) return Results.StatusCode(StatusCodes.Status403Forbidden);
            // The reader always asks for a closed range, a few chunks at a time.
            if (!RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var header) || header.Unit != "bytes"
                || header.Ranges.Count != 1 || header.Ranges.Single() is not { From: { } from, To: { } to } || from < 0 || to < from
                || to - from + 1 > MostBytesARead)
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            if (!links.TryRead(userId, to - from + 1))
                return Results.Problem("That's as much as Bibliotaph reads from your storage in a day. Books read already are kept in this browser.",
                    statusCode: StatusCodes.Status429TooManyRequests);

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
        }).AllowAnonymous().RequireRateLimiting(ReadLimit);
}

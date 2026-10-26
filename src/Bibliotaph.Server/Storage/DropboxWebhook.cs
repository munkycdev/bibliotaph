using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Dropbox's webhook, set in the Dropbox app's settings as /storage/notifications/dropbox. Dropbox checks the address
/// with a challenge it wants echoed, then posts the IDs of accounts whose files changed, signed with the app's secret.
/// Like Graph's, a notice only says that something changed, so each connected account's folders are scanned again.
/// </summary>
public static class DropboxWebhook
{
    const int MaxBody = 1 << 20;

    public static void Map(IEndpointRouteBuilder storage)
    {
        storage.MapGet($"/notifications/{DropboxProvider.Key}", (string? challenge) =>
            challenge is { Length: > 0 } ? Results.Text(challenge, "text/plain") : Results.BadRequest()).AllowAnonymous();

        // It carries no cookie, so it's anonymous; the signature proves it came from Dropbox.
        storage.MapPost($"/notifications/{DropboxProvider.Key}", async (HttpContext context, IConfiguration configuration, StorageConnections connections,
            DriveNotifications notifications, CancellationToken ct) =>
        {
            if (((OAuthStorageProvider)StorageProviders.Find(DropboxProvider.Key)!).App(configuration) is not { } app) return Results.NotFound();
            var body = await ReadAsync(context.Request.Body, ct);
            if (body is null || !Signed(body, context.Request.Headers["X-Dropbox-Signature"].ToString(), app.ClientSecret!)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            foreach (var account in Accounts(body))
                foreach (var connection in await connections.ForAccountAsync(DropboxProvider.Key, account, ct))
                    notifications.ChangedIn(connection);
            return Results.Ok();
        }).AllowAnonymous();
    }

    /// <summary>The signature is the body's HMAC-SHA256 with the app secret, in hex.</summary>
    internal static bool Signed(byte[] body, string signature, string secret)
    {
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        if (signature.Length != expected.Length * 2 || !signature.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature));
    }

    internal static IEnumerable<string> Accounts(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("list_folder", out var folders) && folders.TryGetProperty("accounts", out var accounts)
                ? [.. accounts.EnumerateArray().Select(a => a.GetString()).OfType<string>().Distinct()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    static async Task<byte[]?> ReadAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBody) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

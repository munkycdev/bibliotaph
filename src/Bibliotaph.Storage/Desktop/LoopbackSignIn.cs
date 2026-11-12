using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Bibliotaph.Storage.Desktop;

/// <summary>
/// The OAuth app the Windows app connects a provider through: a public client, so no secret but a value the provider
/// gives desktop apps without keeping it secret (Google's), and a loopback port when the provider matches redirects
/// exactly (Dropbox).
/// </summary>
public sealed record DesktopClient(string ClientId, string? ClientSecret = null, int? Port = null)
{
    public OAuthClient OAuth => new(ClientId, ClientSecret);
}

/// <summary>A connection a sign-in made: who the account is, the grant's refresh token and what it was allowed.</summary>
public sealed record SignedIn(StorageIdentity Identity, string RefreshToken, string Scopes);

/// <summary>
/// Signs in to a provider as a desktop app should (RFC 8252): the system browser, where the user already is signed in
/// and can see the provider's own address, a one-time listener on 127.0.0.1 for the redirect, and PKCE in place of a
/// client secret. A plain socket rather than HttpListener, which needs a URL reservation on Windows that an ordinary
/// user can't make for 127.0.0.1.
/// </summary>
public sealed class LoopbackSignIn(HttpClient http, Func<Uri, Task> openBrowser)
{
    /// <summary>How long a sign-in waits for the browser to come back.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    public async Task<SignedIn> SignInAsync(OAuthStorageKind kind, DesktopClient client, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, client.Port ?? 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new IOException($"Bibliotaph couldn't listen for {kind.Owner}'s answer on port {client.Port}: {ex.Message}", ex);
        }
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        await openBrowser(AuthorizeUri(kind, client, redirect, state, Challenge(verifier)));

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
        patience.CancelAfter(Patience);
        string code;
        try
        {
            code = await AnswerAsync(listener, kind, state, patience.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{kind.Owner} didn't answer in time. Try connecting again.");
        }

        var grant = await OAuthGrants.RedeemAsync(http, kind, client.OAuth, code, redirect, verifier, ct);
        if (grant.RefreshToken is not { } refresh)
            throw new StorageException(HttpStatusCode.BadGateway, $"{kind.Name} didn't allow Bibliotaph to keep access. Try connecting again.");
        var identity = await kind.IdentifyAsync(http, grant.AccessToken, ct);
        return new SignedIn(identity, refresh, grant.Scopes ?? string.Join(' ', kind.Scopes));
    }

    public static Uri AuthorizeUri(OAuthStorageKind kind, DesktopClient client, string redirect, string state, string challenge)
    {
        var query = HttpUtility.ParseQueryString("");
        query["response_type"] = "code";
        query["client_id"] = client.ClientId;
        query["redirect_uri"] = redirect;
        query["scope"] = string.Join(' ', kind.Scopes);
        query["state"] = state;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        foreach (var (name, value) in kind.AuthorizationParameters) query[name] = value;
        return new Uri($"{kind.AuthorizationEndpoint}?{query}");
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Waits for the browser's request that carries this sign-in's state, answering anything else (a favicon, a
    /// stray tab) with a 404, and tells the user in the tab how it went.
    /// </summary>
    static async Task<string> AnswerAsync(TcpListener listener, OAuthStorageKind kind, string state, CancellationToken ct)
    {
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await using var stream = socket.GetStream();
            var target = await RequestTargetAsync(stream, ct);
            var query = target is null ? null : HttpUtility.ParseQueryString(new Uri(new Uri("http://127.0.0.1"), target).Query);
            if (query is null || query["state"] != state)
            {
                await RespondAsync(stream, HttpStatusCode.NotFound, "Not found", ct);
                continue;
            }
            if (query["code"] is { Length: > 0 } code)
            {
                await RespondAsync(stream, HttpStatusCode.OK, $"Bibliotaph is connecting {kind.Name}. You can close this tab and go back to Bibliotaph.", ct);
                return code;
            }
            var error = query["error_description"] ?? query["error"] ?? "no answer";
            await RespondAsync(stream, HttpStatusCode.OK, $"{kind.Name} wasn't connected: {error}. You can close this tab.", ct);
            throw new OAuthRefusedException(query["error"] == "access_denied" ? $"{kind.Name} wasn't connected: access wasn't allowed." : $"{kind.Name} wasn't connected: {error}.");
        }
    }

    /// <summary>The path and query of an HTTP request's first line, reading no more than its headers need.</summary>
    static async Task<string?> RequestTargetAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        return line is ["GET", var path, ..] && path.StartsWith('/') ? path : null;
    }

    static async Task RespondAsync(Stream stream, HttpStatusCode status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Bibliotaph</title></head><body style=\"font-family:'Segoe UI',sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

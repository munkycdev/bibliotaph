using System.Net;
using System.Text.Json;

namespace Bibliotaph.Storage;

/// <summary>
/// An OAuth app a connection is made through. The web server's has a secret; the Windows app's is a public client
/// proving itself with PKCE instead, so its <see cref="ClientSecret"/> is null, or a value its provider hands to desktop
/// apps that isn't kept secret.
/// </summary>
public sealed record OAuthClient(string ClientId, string? ClientSecret);

/// <summary>What a token endpoint gave: the access token, how long it lasts, the scopes granted, and a refresh token when it sent one.</summary>
public sealed record OAuthGrant(string AccessToken, TimeSpan ExpiresIn, string? RefreshToken, string? Scopes);

/// <summary>
/// The provider refused a refresh token or a code (invalid_grant): revoked, expired, or the password changed. Only the
/// user can fix that, by connecting again.
/// </summary>
public sealed class OAuthRefusedException(string message) : IOException(message);

/// <summary>The token endpoint calls both apps make, by refresh token and, on a PC, by the code a sign-in in the browser sent back.</summary>
public static class OAuthGrants
{
    public static Task<OAuthGrant> RefreshAsync(HttpClient http, OAuthStorageKind kind, OAuthClient client, string refreshToken, CancellationToken ct)
    {
        var form = Form(client, "refresh_token");
        form["refresh_token"] = refreshToken;
        if (kind.ScopesOnRefresh) form["scope"] = string.Join(' ', kind.Scopes);
        return SendAsync(http, kind, form, ct);
    }

    /// <summary>Redeems the code from a sign-in that asked with PKCE, sending the verifier its challenge was made from.</summary>
    public static Task<OAuthGrant> RedeemAsync(HttpClient http, OAuthStorageKind kind, OAuthClient client, string code, string redirectUri,
        string codeVerifier, CancellationToken ct)
    {
        var form = Form(client, "authorization_code");
        form["code"] = code;
        form["redirect_uri"] = redirectUri;
        form["code_verifier"] = codeVerifier;
        return SendAsync(http, kind, form, ct);
    }

    static Dictionary<string, string> Form(OAuthClient client, string grant)
    {
        var form = new Dictionary<string, string> { ["client_id"] = client.ClientId, ["grant_type"] = grant };
        if (client.ClientSecret is { Length: > 0 } secret) form["client_secret"] = secret;
        return form;
    }

    static async Task<OAuthGrant> SendAsync(HttpClient http, OAuthStorageKind kind, Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(kind.TokenEndpoint, request, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (error == "invalid_grant") throw new OAuthRefusedException($"{kind.Name} needs connecting again.");
            throw new StorageException(response.StatusCode, $"Signing in to {kind.Name} failed: {error ?? response.StatusCode.ToString()}");
        }

        var token = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (token is not { Length: > 0 }) throw new StorageException(HttpStatusCode.BadGateway, $"{kind.Name} sent no access token.");
        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var s) ? s : 3600;
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rotated ? rotated : null;
        var scopes = root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String && scope.GetString() is { Length: > 0 } granted ? granted : null;
        return new OAuthGrant(token, TimeSpan.FromSeconds(seconds), refresh, scopes);
    }
}

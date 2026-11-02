using System.Net.Http;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Box's token endpoint for the Windows app. Box has no public clients: every token request needs the app's secret,
/// which a desktop app can't keep. So the PC sends its requests here and the server adds the secret and passes them
/// on, keeping nothing: no account, no token, no log of either. Only a code issued to a loopback redirect (the PC's
/// sign-in) and refresh tokens are taken, for this server's own Box app.
/// </summary>
public static class BoxTokenRelay
{
    public const string Limit = "box-token-relay";

    static readonly string[] Passed = ["client_id", "grant_type", "code", "redirect_uri", "code_verifier", "refresh_token"];

    public static void Map(IEndpointRouteBuilder storage) =>
        storage.MapPost($"/token/{BoxProvider.Key}", async (HttpContext context, IConfiguration configuration, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (StorageProviders.Find(BoxProvider.Key) is not OAuthStorageProvider box || box.App(configuration) is not { } app) return Results.NotFound();
            if (!context.Request.HasFormContentType) return Error(StatusCodes.Status400BadRequest, "invalid_request");
            var form = await context.Request.ReadFormAsync(ct);
            if (form["client_id"].ToString() != app.ClientId) return Error(StatusCodes.Status401Unauthorized, "invalid_client");
            switch (form["grant_type"].ToString())
            {
                case "authorization_code" when IsLoopback(form["redirect_uri"].ToString()):
                case "refresh_token" when form["refresh_token"].ToString().Length > 0:
                    break;
                case "authorization_code" or "refresh_token":
                    return Error(StatusCodes.Status400BadRequest, "invalid_request");
                default:
                    return Error(StatusCodes.Status400BadRequest, "unsupported_grant_type");
            }

            var fields = Passed.Where(form.ContainsKey).ToDictionary(k => k, k => form[k].ToString());
            fields["client_secret"] = app.ClientSecret!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                using var content = new FormUrlEncodedContent(fields);
                using var response = await http.CreateClient(BoxProvider.Key).PostAsync(new Uri(box.TokenEndpoint), content, ct);
                // Box's own answer, error or not, so the PC reads it as it would Box's.
                return Results.Text(await response.Content.ReadAsStringAsync(ct), response.Content.Headers.ContentType?.ToString() ?? "application/json",
                    statusCode: (int)response.StatusCode);
            }
            catch (HttpRequestException)
            {
                return Error(StatusCodes.Status502BadGateway, "temporarily_unavailable");
            }
        }).AllowAnonymous().RequireRateLimiting(Limit);

    /// <summary>The PC's sign-in redirect: http on the loopback address, as RFC 8252 has it, never a site's address.</summary>
    static bool IsLoopback(string redirect) =>
        Uri.TryCreate(redirect, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.UserInfo.Length == 0;

    static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}

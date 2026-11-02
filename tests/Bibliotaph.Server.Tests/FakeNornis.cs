using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Bibliotaph.Storage.Nornis;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Stands in for Nornis's API for Bibliotaph and its file storage, answering the contract's shapes: who the user is,
/// their worlds and a world's files a page at a time, and read-only links whose bytes honour ranges.
/// </summary>
sealed class FakeNornis : HttpMessageHandler
{
    public const string ApiHost = "api.nornis.test";
    public const string TenantHost = "auth.test";
    public const string BlobHost = "files.nornis.test";
    public const string Origin = "https://nornis.test";
    public const string ApiBase = "https://api.nornis.test/";
    public const string UserId = "nornis-user-1";
    public const string AccessToken = "nornis-access";
    const int PageSize = 2;

    readonly Dictionary<string, (string Name, List<(string Id, string Name, byte[] Bytes)> Files)> _worlds = [];

    /// <summary>Whether Nornis refuses the token, as when the user disconnected Bibliotaph there.</summary>
    public bool Refuses { get; set; }

    public int Requests { get; private set; }

    /// <summary>How many times Bibliotaph asked for the worlds to be sent again.</summary>
    public int Resyncs { get; private set; }

    public static bool Handles(Uri uri) => uri.Host is ApiHost or BlobHost or TenantHost;

    public void AddWorld(string worldId, string name) => _worlds[worldId] = (name, []);

    public void AddFile(string worldId, string fileId, string name, byte[] bytes) => _worlds[worldId].Files.Add((fileId, name, bytes));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var uri = request.RequestUri!;
        if (uri.Host == BlobHost) return Blob(request);
        // Auth0's token endpoint, refreshing the storage connection's token for Nornis's API.
        if (uri.Host == TenantHost)
            return uri.AbsolutePath == "/oauth/token" && !Refuses
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = AccessToken, expires_in = 3600 }) }
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        if (Refuses || request.Headers.Authorization?.Parameter != AccessToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cursor = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("cursor", out var c)
            ? int.Parse(c.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        await Task.Yield();
        return path switch
        {
            ["api", "bibliotaph", "v1", "resync"] when request.Method == HttpMethod.Post => Resynced(),
            ["api", "bibliotaph", "v1", "me"] => Json(new NornisMe(UserId, "Dana")),
            ["api", "bibliotaph", "v1", "worlds"] => Json(Page(_worlds.Select(w => new NornisWorld(w.Key, w.Value.Name, Updated)).ToList(), cursor,
                (items, next) => new NornisWorldPage(items, next))),
            ["api", "bibliotaph", "v1", "worlds", var world, "files"] when _worlds.TryGetValue(world, out var w) =>
                Json(Page(w.Files.Select(f => new NornisFile(f.Id, f.Name, "application/pdf", f.Bytes.Length, Updated, $"\"{f.Id}\"")).ToList(), cursor,
                    (items, next) => new NornisFilePage(items, next))),
            ["api", "bibliotaph", "v1", "files", var file, "link"] when Find(file) is not null =>
                Json(new NornisLink($"https://{BlobHost}/{file}?sig=read", Updated.AddHours(1))),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    HttpResponseMessage Resynced()
    {
        Resyncs++;
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    static readonly DateTimeOffset Updated = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    static TPage Page<TItem, TPage>(List<TItem> items, int from, Func<IReadOnlyList<TItem>, string?, TPage> make) =>
        make(items.Skip(from).Take(PageSize).ToList(), from + PageSize < items.Count ? (from + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);

    byte[]? Find(string fileId) => _worlds.Values.SelectMany(w => w.Files).Where(f => f.Id == fileId).Select(f => f.Bytes).FirstOrDefault();

    HttpResponseMessage Blob(HttpRequestMessage request)
    {
        if (Find(request.RequestUri!.AbsolutePath.Trim('/')) is not { } bytes) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from, To: { } to })
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)from..((int)to + 1)]) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: NornisClient.Json) };
}

/// <summary>Auth0's access tokens for the Bibliotaph API, signed with a key the test server is told to trust.</summary>
static class NornisTokens
{
    public const string Issuer = "https://auth.test/";
    public const string Audience = "https://bibliotaph.test/api";

    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("a test key that is long enough for HMAC-SHA256 signing"));

    public static string For(string subject, string scope = "books:read tickets:write worlds:write", string audience = Audience,
        SecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("scope", scope)]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        });
}

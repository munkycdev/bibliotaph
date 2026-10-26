using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Bibliotaph.Storage.WebDav;

/// <summary>
/// Where a WebDAV account's files are and how to sign in: the folder's address (ending in a slash), a user name and a
/// password, usually an app password made for Bibliotaph. Kept as JSON, encrypted, as other providers keep a refresh token.
/// </summary>
public sealed record WebDavCredential(string Url, string User, string Password)
{
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static WebDavCredential FromJson(string json) =>
        JsonSerializer.Deserialize<WebDavCredential>(json, Json) ?? throw new InvalidDataException("That isn't a WebDAV sign-in.");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// A file or folder a PROPFIND described, by its path under the account's folder ("" for the folder itself), a backslash
/// in a name read as ∖ as in library paths.
/// </summary>
public sealed record WebDavResource(string Path, bool IsFolder, long Size, DateTime ModifiedUtc, string? ETag);

/// <summary>
/// The few WebDAV requests Bibliotaph makes, as one account: PROPFIND for a file or a folder's contents, and GET for a
/// file's bytes. Paths are slash-separated names under the account's folder. Redirects are followed only on the same
/// server, so the password never goes anywhere else. Busy answers (429, 503) are waited out.
/// </summary>
public sealed class WebDavClient(HttpClient http, Func<CancellationToken, Task<WebDavCredential>> credential, Func<CancellationToken, Task>? refused = null,
    TimeProvider? clock = null)
{
    const int Attempts = 5;
    const int Redirects = 3;
    static readonly XNamespace Dav = "DAV:";
    static readonly HttpMethod PropFind = new("PROPFIND");

    // Only what's needed to list a folder: asking for everything (allprop) is slow on big folders.
    const string Body = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>""";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The resource at <paramref name="path"/> (depth 0) or it and what's directly in it (depth 1), itself first; null when
    /// nothing is there.
    /// </summary>
    public async Task<IReadOnlyList<WebDavResource>?> PropFindAsync(string path, int depth, bool folder, CancellationToken ct = default)
    {
        var account = await credential(ct);
        using var response = await SendAsync(account, () =>
        {
            var request = new HttpRequestMessage(PropFind, Address(account, path, folder)) { Content = new StringContent(Body, Encoding.UTF8, "application/xml") };
            request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
            return request;
        }, HttpCompletionOption.ResponseContentRead, ct);
        if (response is null) return null;
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = BasePath(account);
        var resources = new List<WebDavResource>();
        foreach (var entry in xml.Root?.Elements(Dav + "response") ?? [])
        {
            if (entry.Element(Dav + "href")?.Value is not { Length: > 0 } href) continue;
            var prop = entry.Elements(Dav + "propstat").Where(p => p.Element(Dav + "status")?.Value.Contains(" 200 ", StringComparison.Ordinal) ?? true)
                .Select(p => p.Element(Dav + "prop")).FirstOrDefault(p => p is not null);
            var full = Uri.UnescapeDataString(new Uri(new Uri(account.Url), href).AbsolutePath);
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            var inner = full[root.Length..].Trim('/').Replace('\\', '∖');
            var isFolder = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null || href.EndsWith('/');
            var size = long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : 0;
            var modified = DateTime.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : DateTime.MinValue;
            var etag = prop?.Element(Dav + "getetag")?.Value is { Length: > 0 } tag ? tag : null;
            resources.Add(new WebDavResource(inner, isFolder, size, modified, etag));
        }
        var self = path.Trim('/');
        // The resource itself first, as the protocol doesn't promise an order.
        return [.. resources.Where(r => r.Path == self).Take(1), .. resources.Where(r => r.Path != self)];
    }

    /// <summary>A file's bytes, or the range of them asked for, streamed.</summary>
    public async Task<Stream> GetAsync(string path, ByteRange? range = null, CancellationToken ct = default)
    {
        var account = await credential(ct);
        var response = await SendAsync(account, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Address(account, path, folder: false));
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct) ?? throw new StorageException(HttpStatusCode.NotFound, "The file isn't on the WebDAV server.");
        return await StorageDownloads.BodyAsync(response, range, ct);
    }

    /// <summary>The address of a path under the account's folder, each name escaped; a folder's ends in a slash.</summary>
    public static Uri Address(WebDavCredential account, string path, bool folder)
    {
        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(name => Uri.EscapeDataString(name.Replace('∖', '\\')));
        var inner = string.Join('/', names);
        return new Uri(new Uri(account.Url), inner.Length == 0 ? "" : folder ? inner + "/" : inner);
    }

    static string BasePath(WebDavCredential account) => Uri.UnescapeDataString(new Uri(account.Url).AbsolutePath).TrimEnd('/') + "/";

    /// <returns>The successful answer, or null for 404.</returns>
    async Task<HttpResponseMessage?> SendAsync(WebDavCredential account, Func<HttpRequestMessage> create, HttpCompletionOption completion, CancellationToken ct)
    {
        var origin = new Uri(account.Url);
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.User}:{account.Password}")));
        for (int attempt = 1, redirects = 0; ;)
        {
            using var request = create();
            request.Headers.Authorization = basic;
            var response = await http.SendAsync(request, completion, ct);
            var status = response.StatusCode;
            if (response.IsSuccessStatusCode) return response;

            var location = response.Headers.Location;
            var reason = response.ReasonPhrase;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            if (status == HttpStatusCode.NotFound) return null;
            if ((int)status is >= 300 and < 400 && location is not null && redirects++ < Redirects)
            {
                // A folder asked for without its slash is often redirected to the address with it: followed, but only here.
                var target = new Uri(request.RequestUri!, location);
                if (target.Scheme == origin.Scheme && target.Authority == origin.Authority)
                {
                    var next = create;
                    create = () => Retarget(next(), target);
                    continue;
                }
                throw new StorageException(status, $"The WebDAV server sent Bibliotaph to another server ({target.Authority}), which it doesn't follow.");
            }
            if (status == HttpStatusCode.Unauthorized)
            {
                if (refused is not null) await refused(ct);
                throw new StorageException(status, "The WebDAV server didn't accept the user name or app password.");
            }
            if (attempt++ < Attempts && status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway
                or HttpStatusCode.GatewayTimeout)
            {
                await Task.Delay(wait, _clock, ct);
                continue;
            }
            throw new StorageException(status, $"The WebDAV server answered {(int)status} {reason}.");
        }
    }

    static HttpRequestMessage Retarget(HttpRequestMessage request, Uri target)
    {
        request.RequestUri = target;
        return request;
    }
}

/// <summary>
/// A WebDAV account, as a library reads it: by path, which is what WebDAV knows files by. A backslash in a name, which a
/// library path can't hold, reads as ∖ (U+2216). Changes are noticed by the account folder's ETag, which Nextcloud and
/// ownCloud change whenever anything inside changes; other servers may change it only for what's directly in it.
/// </summary>
public sealed class WebDavDrive(WebDavClient client) : IStorageDrive, IStorageChangeLog
{
    public string ProviderName => "WebDAV";

    // The server wants the password with every request, which a page on this site mustn't have.
    public bool HasReadLinks => false;

    public async Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default)
    {
        var found = await client.PropFindAsync(path, 0, folder: false, ct);
        return found is [var item, ..] ? Entry(item) : null;
    }

    public async Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default)
    {
        var found = await client.PropFindAsync(path, 1, folder: true, ct);
        if (found is not [{ IsFolder: true } self, ..]) return null;
        return [.. found.Skip(1).Where(c => c.Path.Length > self.Path.Length).Select(Entry)];
    }

    public Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default) => client.GetAsync(path, range, ct);

    public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public async Task<string> StartAsync(CancellationToken ct = default) => await StampAsync(ct) ?? "";

    /// <summary>A server that gives the folder no ETag or date never reports a change here; the twice-daily look finds them.</summary>
    public async Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default)
    {
        var now = await StampAsync(ct) ?? "";
        return (now.Length > 0 && cursor.Length > 0 && now != cursor, now);
    }

    async Task<string?> StampAsync(CancellationToken ct) =>
        await client.PropFindAsync("", 0, folder: true, ct) is [var root, ..]
            ? root.ETag ?? (root.ModifiedUtc == DateTime.MinValue ? null : root.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture))
            : null;

    static StorageEntry Entry(WebDavResource resource)
    {
        var slash = resource.Path.LastIndexOf('/');
        return new StorageEntry(slash < 0 ? resource.Path : resource.Path[(slash + 1)..], resource.IsFolder, resource.Size, resource.ModifiedUtc);
    }
}

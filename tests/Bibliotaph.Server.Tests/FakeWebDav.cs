using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// A WebDAV server as Nextcloud answers: PROPFIND at depth 0 or 1 and GET with ranges, under one user's folder, with
/// Basic sign-in. A folder asked for without its slash is redirected to the address with it, and every folder's ETag
/// changes when anything inside it does.
/// </summary>
sealed class FakeWebDav : HttpMessageHandler
{
    public const string Host = "dav.example.com";
    public const string Root = "https://dav.example.com/remote.php/dav/files/dana/";
    public const string User = "dana";
    public const string Password = "app-password";
    public const string AccountId = "0a1b2c3d4e5f60718293";

    const string RootPath = "/remote.php/dav/files/dana/";

    readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    int _changes;

    public int Downloads { get; private set; }

    public static bool Handles(Uri uri) => uri.Host == Host;

    /// <summary>Adds a file at a path of names under the user's folder, making its folders.</summary>
    public void Add(string path, byte[] bytes)
    {
        lock (_lock)
        {
            _files[path] = bytes;
            _changes++;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var absolute = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (!absolute.StartsWith(RootPath.TrimEnd('/'), StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var path = absolute.Length <= RootPath.Length ? "" : absolute[RootPath.Length..];
        var asFolder = path.Length == 0 || path.EndsWith('/');
        path = path.Trim('/');

        lock (_lock)
        {
            var isFile = _files.ContainsKey(path);
            var isFolder = path.Length == 0 || _files.Keys.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal));
            if (!isFile && !isFolder) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (isFolder && !asFolder)
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri(request.RequestUri + "/");
                return moved;
            }

            if (request.Method.Method == "PROPFIND")
            {
                var depth = request.Headers.TryGetValues("Depth", out var values) ? values.Single() : "infinity";
                var body = new StringBuilder("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">");
                Describe(body, path, isFolder);
                if (depth == "1" && isFolder)
                    foreach (var (child, folder) in Children(path)) Describe(body, child, folder);
                body.Append("</d:multistatus>");
                return new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/xml") };
            }

            if (request.Method == HttpMethod.Get && isFile)
            {
                Downloads++;
                var bytes = _files[path];
                if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from, To: { } to })
                {
                    var part = new ByteArrayContent(bytes[(int)from..(int)Math.Min(to + 1, bytes.Length)]);
                    part.Headers.ContentRange = new ContentRangeHeaderValue(from, Math.Min(to, bytes.Length - 1), bytes.Length);
                    return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
        }
        await Task.CompletedTask;
        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }

    IEnumerable<(string Path, bool Folder)> Children(string folder)
    {
        var prefix = folder.Length == 0 ? "" : folder + "/";
        return _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => f[prefix.Length..])
            .Select(rest => rest.Contains('/') ? (prefix + rest[..rest.IndexOf('/')], true) : (prefix + rest, false))
            .Distinct();
    }

    void Describe(StringBuilder body, string path, bool folder)
    {
        var href = RootPath + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + (folder && path.Length > 0 ? "/" : "");
        var size = folder ? 0 : _files[path].Length;
        // As Nextcloud does, a folder's ETag changes whenever anything inside it does.
        var etag = folder ? $"\"f{_changes}-{path.Length}\"" : $"\"{_files[path].Length}\"";
        body.Append(CultureInfo.InvariantCulture, $"<d:response><d:href>{SecurityElement.Escape(href)}</d:href><d:propstat><d:prop>")
            .Append(folder ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>")
            .Append(folder ? "" : $"<d:getcontentlength>{size}</d:getcontentlength>")
            .Append("<d:getlastmodified>Thu, 01 Oct 2026 10:00:00 GMT</d:getlastmodified>")
            .Append(CultureInfo.InvariantCulture, $"<d:getetag>{SecurityElement.Escape(etag)}</d:getetag>")
            .Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;

namespace Bibliotaph.App.Services;

/// <summary>
/// A WebDAV server for the smoke test, as Nextcloud answers: one user's folder holding one made-up book in a folder of
/// its own, PROPFIND at depth 0 or 1, GET with ranges, and Basic sign-in. Connecting real storage needs a real server.
/// </summary>
sealed class SmokeWebDav : HttpMessageHandler
{
    public const string Address = "https://dav.smoke.test/remote.php/dav/files/smoke/";
    public const string User = "smoke";
    public const string Password = "smoke-app-password";
    public const string Folder = "Adventures";
    public const string Title = "The Copper Lantern";

    /// <summary>What its second page says, which a search finds once the book was downloaded and read.</summary>
    public const string Phrase = "lantern of hammered copper";

    const string RootPath = "/remote.php/dav/files/smoke/";

    static readonly byte[] Book = SmokePdfs.Plain(Title,
        [[Title, "A made-up book for the smoke test."], ["The lantern", $"A {Phrase} hangs above the door."]]);

    static readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal) { [$"{Folder}/{Title}.pdf"] = Book };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(request));

    static HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var absolute = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (!absolute.StartsWith(RootPath.TrimEnd('/'), StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var path = absolute.Length <= RootPath.Length ? "" : absolute[RootPath.Length..];
        var asFolder = path.Length == 0 || path.EndsWith('/');
        path = path.Trim('/');

        var isFile = Files.ContainsKey(path);
        var isFolder = path.Length == 0 || Files.Keys.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal));
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
            var bytes = Files[path];
            if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from, To: { } to })
            {
                var part = new ByteArrayContent(bytes[(int)from..(int)Math.Min(to + 1, bytes.Length)]);
                part.Headers.ContentRange = new ContentRangeHeaderValue(from, Math.Min(to, bytes.Length - 1), bytes.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }

    static IEnumerable<(string Path, bool Folder)> Children(string folder)
    {
        var prefix = folder.Length == 0 ? "" : folder + "/";
        return Files.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => f[prefix.Length..])
            .Select(rest => rest.Contains('/', StringComparison.Ordinal) ? (prefix + rest[..rest.IndexOf('/', StringComparison.Ordinal)], true) : (prefix + rest, false))
            .Distinct();
    }

    static void Describe(StringBuilder body, string path, bool folder)
    {
        var href = RootPath + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + (folder && path.Length > 0 ? "/" : "");
        var etag = folder ? $"\"f-{path.Length}\"" : $"\"{Files[path].Length}\"";
        body.Append(CultureInfo.InvariantCulture, $"<d:response><d:href>{SecurityElement.Escape(href)}</d:href><d:propstat><d:prop>")
            .Append(folder ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>")
            .Append(folder ? "" : string.Create(CultureInfo.InvariantCulture, $"<d:getcontentlength>{Files[path].Length}</d:getcontentlength>"))
            .Append("<d:getlastmodified>Thu, 01 Oct 2026 10:00:00 GMT</d:getlastmodified>")
            .Append(CultureInfo.InvariantCulture, $"<d:getetag>{SecurityElement.Escape(etag)}</d:getetag>")
            .Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }
}

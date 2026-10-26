using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Dropbox's API and token endpoint, answering from an in-memory account: enough for listing (two entries a page, so
/// continuing is exercised), metadata and ranged downloads. Files are added by path; folders exist because files are in them.
/// </summary>
sealed class FakeDropbox : HttpMessageHandler
{
    public const string AccountId = "dbid:AAH4f99";
    const int PageSize = 2;

    readonly Dictionary<string, (byte[] Bytes, DateTime Modified)> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _lock = new();

    public int Downloads { get; private set; }
    public int Pages { get; private set; }

    /// <summary>Every Dropbox-API-Arg and Dropbox-API-Path-Root header sent, as sent.</summary>
    public List<string> Headers { get; } = [];

    /// <summary>The account answers as a team member's whose team space is this namespace.</summary>
    public string? TeamRoot { get; set; }

    public static bool Handles(Uri uri) => uri.Host is "api.dropboxapi.com" or "content.dropboxapi.com";

    public void Add(string path, byte[] bytes, DateTime? modified = null)
    {
        lock (_lock) _files[path.Trim('/')] = (bytes, modified ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "Dropbox-API-Arg", "Dropbox-API-Path-Root" })
            if (request.Headers.TryGetValues(name, out var values)) Headers.AddRange(values);
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/oauth2/token") return Json(new { access_token = "dropbox-access", expires_in = 14400, token_type = "bearer" });
        if (path == "/2/files/download")
            return Download(Arg(JsonNode.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single())!), request.Headers.Range?.Ranges.Single());

        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        return path switch
        {
            "/2/users/get_current_account" => Json(new
            {
                account_id = AccountId,
                name = new { display_name = "Dana" },
                root_info = new Dictionary<string, string> { [".tag"] = TeamRoot is null ? "user" : "team", ["root_namespace_id"] = TeamRoot ?? "1", ["home_namespace_id"] = "1" },
            }),
            "/2/files/list_folder" => List(Arg(body!), 0),
            "/2/files/list_folder/continue" => Continue(body!["cursor"]!.GetValue<string>()),
            "/2/files/get_metadata" => Metadata(Arg(body!)),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
        };
    }

    static string Arg(JsonNode body) => body["path"]!.GetValue<string>().Trim('/');

    HttpResponseMessage Continue(string cursor)
    {
        var parts = cursor.Split('|', 2);
        return List(parts[1], int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    HttpResponseMessage List(string folder, int offset)
    {
        lock (_lock)
        {
            if (folder.Length > 0 && !IsFolder(folder)) return NotFound();
            Pages++;
            var all = Children(folder);
            var page = all.Skip(offset).Take(PageSize).ToList();
            var more = offset + PageSize < all.Count;
            return Json(new Dictionary<string, object> { ["entries"] = page, ["has_more"] = more, ["cursor"] = $"{offset + PageSize}|{folder}" });
        }
    }

    List<Dictionary<string, object>> Children(string folder)
    {
        var start = folder.Length == 0 ? "" : folder + "/";
        return [.. _files.Keys.Where(k => k.StartsWith(start, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[start.Length..])
            .GroupBy(k => k.Split('/')[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Any(k => k.Contains('/', StringComparison.Ordinal)) ? Folder(g.Key) : File(start + g.Key))];
    }

    HttpResponseMessage Metadata(string path)
    {
        lock (_lock)
        {
            if (IsFolder(path)) return Json(Folder(path.Split('/')[^1]));
            return _files.ContainsKey(path) ? Json(File(path)) : NotFound();
        }
    }

    HttpResponseMessage Download(string path, System.Net.Http.Headers.RangeItemHeaderValue? range)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(path, out var file)) return NotFound();
            Downloads++;
            if (range is null) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Bytes) };
            var from = (int)range.From!.Value;
            var to = (int)Math.Min(range.To!.Value, file.Bytes.Length - 1);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file.Bytes[from..(to + 1)]) };
        }
    }

    bool IsFolder(string path) => _files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    static Dictionary<string, object> Folder(string name) => new() { [".tag"] = "folder", ["name"] = name, ["id"] = "id:f-" + name };

    Dictionary<string, object> File(string path) => new()
    {
        [".tag"] = "file",
        ["name"] = path.Split('/')[^1],
        ["id"] = "id:" + path,
        ["size"] = _files[path].Bytes.Length,
        ["server_modified"] = _files[path].Modified.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["is_downloadable"] = true,
    };

    static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.Conflict) { Content = new StringContent("""{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""") };
}

/// <summary>Sends each request to the fake that serves its host, as the server's clients reach Graph and Dropbox.</summary>
sealed class FakeHosts(FakeGraph graph, FakeDropbox dropbox) : HttpMessageHandler
{
    readonly HttpMessageInvoker _graph = new(graph, disposeHandler: false);
    readonly HttpMessageInvoker _dropbox = new(dropbox, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        (FakeDropbox.Handles(request.RequestUri!) ? _dropbox : _graph).SendAsync(request, cancellationToken);
}

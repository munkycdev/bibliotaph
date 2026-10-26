using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Box API 2.0 and its token endpoint, answering from an in-memory account. Items are kept by ID with their parent
/// folder, two to a page so marker paging is exercised. A download is redirected to a signed address on another host,
/// as Box does, and every token handed out comes with a new refresh token.
/// </summary>
sealed class FakeBox : HttpMessageHandler
{
    public const string UserId = "11446498";
    const int PageSize = 2;

    readonly List<Item> _items = [];
    readonly Lock _lock = new();
    readonly List<string> _events = [];
    int _tokens;

    public int Downloads { get; private set; }

    /// <summary>The refresh token the last token answer handed out.</summary>
    public string? LastRefreshToken { get; private set; }

    sealed record Item(string Id, string Parent, string Name, bool Folder, byte[] Bytes);

    public static bool Handles(Uri uri) => uri.Host is "api.box.com" or "account.box.com" or "dl.boxcloud.com";

    /// <summary>Adds a file at a path of names, making its folders, and returns its ID.</summary>
    public string Add(string path, byte[] bytes)
    {
        lock (_lock)
        {
            var names = path.Split('/');
            var parent = "0";
            foreach (var folder in names[..^1]) parent = FolderIn(parent, folder);
            var id = (1000 + _items.Count).ToString(CultureInfo.InvariantCulture);
            _items.Add(new Item(id, parent, names[^1], false, bytes));
            _events.Add(id);
            return id;
        }
    }

    string FolderIn(string parent, string name)
    {
        if (_items.FirstOrDefault(i => i.Parent == parent && i.Name == name && i.Folder) is { } known) return known.Id;
        var id = (1000 + _items.Count).ToString(CultureInfo.InvariantCulture);
        _items.Add(new Item(id, parent, name, true, []));
        return id;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = QueryHelpers.ParseQuery(uri.Query);
        lock (_lock)
        {
            if (uri.AbsolutePath == "/oauth2/token")
            {
                LastRefreshToken = $"box-refresh-{++_tokens}";
                return Json(new { access_token = "box-access", expires_in = 3600, token_type = "bearer", refresh_token = LastRefreshToken });
            }
            if (uri.Host == "dl.boxcloud.com") return Download(uri.AbsolutePath.Split('/')[^1], request.Headers.Range?.Ranges.Single());
            if (request.Headers.Authorization is not { Scheme: "Bearer" }) return Status(HttpStatusCode.Unauthorized);

            var path = uri.AbsolutePath;
            if (path == "/2.0/users/me") return Json(new { type = "user", id = UserId, name = "Dana", login = "dana@example.com" });
            if (path == "/2.0/events")
            {
                var from = query["stream_position"] == "now" ? _events.Count : int.Parse(query["stream_position"]!, CultureInfo.InvariantCulture);
                var entries = _events.Skip(from).Select(id => new { type = "event", event_type = "ITEM_UPLOAD", source = new { type = "file", id } }).ToList();
                // Box gives the position as a number in JSON.
                return Json(new { chunk_size = entries.Count, next_stream_position = (long)_events.Count, entries });
            }
            if (path.StartsWith("/2.0/folders/", StringComparison.Ordinal) && path.EndsWith("/items", StringComparison.Ordinal))
            {
                var folder = path["/2.0/folders/".Length..^"/items".Length];
                if (folder != "0" && !_items.Any(i => i.Id == folder && i.Folder)) return Status(HttpStatusCode.NotFound);
                var all = _items.Where(i => i.Parent == folder).ToList();
                var offset = query.TryGetValue("marker", out var marker) ? int.Parse(marker!, CultureInfo.InvariantCulture) : 0;
                var page = all.Skip(offset).Take(PageSize).Select(i => new
                {
                    type = i.Folder ? "folder" : "file",
                    id = i.Id,
                    name = i.Name,
                    size = i.Bytes.Length,
                    modified_at = "2026-10-01T10:00:00-07:00",
                }).ToList<object>();
                if (offset == 0 && folder == "0") page.Add(new { type = "web_link", id = "9", name = "A bookmark", size = 0, modified_at = "2026-10-01T10:00:00-07:00" });
                return Json(new { entries = page, limit = PageSize, next_marker = offset + PageSize < all.Count ? (offset + PageSize).ToString(CultureInfo.InvariantCulture) : "" });
            }
            if (path.StartsWith("/2.0/files/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal))
            {
                var id = path["/2.0/files/".Length..^"/content".Length];
                if (!_items.Any(i => i.Id == id && !i.Folder)) return Status(HttpStatusCode.NotFound);
                var moved = new HttpResponseMessage(HttpStatusCode.Found);
                moved.Headers.Location = new Uri($"https://dl.boxcloud.com/d/1/signed/{id}");
                return Task.FromResult(moved);
            }
        }
        return Status(HttpStatusCode.NotFound);
    }

    Task<HttpResponseMessage> Download(string id, RangeItemHeaderValue? range)
    {
        if (_items.FirstOrDefault(i => i.Id == id && !i.Folder) is not { } item) return Status(HttpStatusCode.NotFound);
        Downloads++;
        if (range is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Bytes) });
        var to = (int)Math.Min(range.To!.Value, item.Bytes.Length - 1);
        var part = new ByteArrayContent(item.Bytes[(int)range.From!.Value..(to + 1)]);
        part.Headers.ContentRange = new ContentRangeHeaderValue(range.From!.Value, to, item.Bytes.Length);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part });
    }

    static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"type":"error"}""", Encoding.UTF8, "application/json") });

    static Task<HttpResponseMessage> Json(object value) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
}

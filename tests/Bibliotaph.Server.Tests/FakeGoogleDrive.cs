using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Drive API v3 and Google's token endpoint, answering from an in-memory drive. Items are kept by ID, each with its parent,
/// so two can share a name and a name can hold a slash, as in Drive. Two items a page, so paging is exercised.
/// </summary>
sealed class FakeGoogleDrive : HttpMessageHandler
{
    public const string PermissionId = "06101234567890123456";
    const int PageSize = 2;

    readonly List<Item> _items = [];
    readonly Lock _lock = new();
    int _changes;

    public int Downloads { get; private set; }
    public int Listings { get; private set; }

    sealed record Item(string Id, string Parent, string Name, string MimeType, byte[] Bytes, DateTime Created);

    public static bool Handles(Uri uri) => uri.Host is "www.googleapis.com" or "oauth2.googleapis.com";

    /// <summary>Adds a file at a path of names, making its folders, and returns its ID.</summary>
    public string Add(string path, byte[] bytes, string mimeType = "application/pdf", DateTime? created = null)
    {
        lock (_lock)
        {
            var names = path.Split('/');
            var parent = "root";
            foreach (var folder in names[..^1]) parent = FolderIn(parent, folder);
            var id = $"file{_items.Count}";
            _items.Add(new Item(id, parent, names[^1], mimeType, bytes, created ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_items.Count)));
            _changes++;
            return id;
        }
    }

    /// <summary>Adds a file whose own name is <paramref name="name"/>, which may hold a slash, in the folder at <paramref name="folder"/>.</summary>
    public string AddNamed(string folder, string name, byte[] bytes)
    {
        lock (_lock)
        {
            var parent = "root";
            foreach (var part in folder.Split('/')) parent = FolderIn(parent, part);
            var id = $"file{_items.Count}";
            _items.Add(new Item(id, parent, name, "application/pdf", bytes, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(_items.Count)));
            _changes++;
            return id;
        }
    }

    string FolderIn(string parent, string name)
    {
        if (_items.FirstOrDefault(i => i.Parent == parent && i.Name == name && i.MimeType == "application/vnd.google-apps.folder") is { } known) return known.Id;
        var id = $"folder{_items.Count}";
        _items.Add(new Item(id, parent, name, "application/vnd.google-apps.folder", [], DateTime.UnixEpoch));
        return id;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (uri.Host == "oauth2.googleapis.com") return Json(new { access_token = "google-access", expires_in = 3599, token_type = "Bearer" });
        var path = uri.AbsolutePath;
        lock (_lock)
        {
            if (path == "/drive/v3/about")
                return Json(new { user = new { permissionId = PermissionId, displayName = "Dana", emailAddress = "dana@example.com" } });
            if (path == "/drive/v3/changes/startPageToken") return Json(new { startPageToken = _changes.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            if (path == "/drive/v3/changes")
            {
                var since = int.Parse(query["pageToken"]!, System.Globalization.CultureInfo.InvariantCulture);
                var changes = Enumerable.Range(since, Math.Max(0, _changes - since)).Select(n => new { fileId = $"file{n}" }).ToList();
                return Json(new { changes, newStartPageToken = _changes.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (path == "/drive/v3/files") return List(query["q"].ToString(), query.TryGetValue("pageToken", out var page) ? int.Parse(page!, System.Globalization.CultureInfo.InvariantCulture) : 0);
            if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal) && query["alt"] == "media")
                return Download(Uri.UnescapeDataString(path["/drive/v3/files/".Length..]), request.Headers.Range?.Ranges.Single());
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    Task<HttpResponseMessage> List(string q, int offset)
    {
        var parent = q[1..q.IndexOf('\'', 1)];
        Listings++;
        var all = _items.Where(i => i.Parent == parent).ToList();
        var page = all.Skip(offset).Take(PageSize).Select(i => new Dictionary<string, object>
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["mimeType"] = i.MimeType,
            ["modifiedTime"] = "2026-10-01T00:00:00.000Z",
            ["createdTime"] = i.Created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            // Drive leaves size out for folders and Google's own documents.
            ["size"] = i.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) ? null! : i.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }.Where(p => p.Value is not null).ToDictionary()).ToList();
        var result = new Dictionary<string, object> { ["files"] = page };
        if (offset + PageSize < all.Count) result["nextPageToken"] = (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Json(result);
    }

    Task<HttpResponseMessage> Download(string id, System.Net.Http.Headers.RangeItemHeaderValue? range)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is not { } item) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        Downloads++;
        if (range is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Bytes) });
        var to = (int)Math.Min(range.To!.Value, item.Bytes.Length - 1);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(item.Bytes[(int)range.From!.Value..(to + 1)]) });
    }

    static Task<HttpResponseMessage> Json(object value) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
}

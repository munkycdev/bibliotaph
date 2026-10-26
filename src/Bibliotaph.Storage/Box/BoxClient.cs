using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bibliotaph.Storage.Box;

/// <summary>A Box user: their ID, which names the account, and their name.</summary>
public sealed record BoxUser(string Id, string? Name, string? Login);

/// <summary>A file or folder in a Box folder, with the ID it's read by.</summary>
public sealed record BoxEntry(string Id, StorageEntry Entry);

/// <summary>
/// The few Box API calls Bibliotaph makes, as one connected account: who it is, a folder's items, a file's bytes, and
/// the change stream. Plain HTTP and JSON rather than Box's SDK. Rate limiting (429) and a download that isn't ready yet
/// (202) are waited out.
/// </summary>
public sealed class BoxClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken, TimeProvider? clock = null)
{
    public const string ApiRoot = "https://api.box.com/2.0";
    const int Attempts = 5;
    const int PageSize = 1000;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<BoxUser> MeAsync(CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ApiRoot}/users/me?fields=id,name,login", ct);
        var user = json.RootElement;
        return new BoxUser(user.GetProperty("id").GetString()!, Text(user, "name"), Text(user, "login"));
    }

    /// <summary>A folder's files and folders, by ID ("0" for the top), every page of them. Web links aren't files, so they're left out.</summary>
    public async Task<IReadOnlyList<BoxEntry>> ItemsAsync(string folderId, CancellationToken ct = default)
    {
        var entries = new List<BoxEntry>();
        string? marker = null;
        do
        {
            var url = $"{ApiRoot}/folders/{Uri.EscapeDataString(folderId)}/items?fields=id,type,name,size,modified_at&limit={PageSize}&usemarker=true"
                + (marker is null ? "" : $"&marker={Uri.EscapeDataString(marker)}");
            using var json = await GetJsonAsync(url, ct);
            foreach (var item in json.RootElement.GetProperty("entries").EnumerateArray())
            {
                var type = Text(item, "type");
                if (type is not ("file" or "folder")) continue;
                var size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var modified = item.TryGetProperty("modified_at", out var m) && m.ValueKind == JsonValueKind.String ? m.GetDateTime().ToUniversalTime() : DateTime.MinValue;
                entries.Add(new BoxEntry(item.GetProperty("id").GetString()!, new StorageEntry(item.GetProperty("name").GetString()!, type == "folder", size, modified)));
            }
            marker = Text(json.RootElement, "next_marker") is { Length: > 0 } next ? next : null;
        }
        while (marker is not null);
        return entries;
    }

    /// <summary>
    /// A file's bytes, or the range of them asked for, streamed. Box answers with a redirect to a signed address on a
    /// download server, followed here rather than by the handler so the token isn't sent there and a handler that
    /// doesn't follow redirects still downloads.
    /// </summary>
    public async Task<Stream> DownloadAsync(string fileId, ByteRange? range = null, CancellationToken ct = default)
    {
        HttpRequestMessage Ranged(Uri address)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            return request;
        }

        var response = await SendAsync(() => Ranged(new Uri($"{ApiRoot}/files/{Uri.EscapeDataString(fileId)}/content")),
            HttpCompletionOption.ResponseHeadersRead, ct, redirects: true);
        if (response.Headers.Location is { } location)
        {
            response.Dispose();
            using var signed = Ranged(location.IsAbsoluteUri ? location : new Uri(new Uri(ApiRoot), location));
            response = await http.SendAsync(signed, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new StorageException(status, $"Box's download server answered {(int)status}.");
            }
        }
        return await StorageDownloads.BodyAsync(response, range, ct);
    }

    /// <summary>Where the account's change stream stands now, to ask later what changed since.</summary>
    public async Task<string> StreamPositionAsync(CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ApiRoot}/events?stream_type=changes&stream_position=now", ct);
        return Position(json.RootElement);
    }

    /// <summary>Whether anything changed since <paramref name="position"/>, and the position to ask from next time.</summary>
    public async Task<(bool Changed, string Next)> ChangesSinceAsync(string position, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ApiRoot}/events?stream_type=changes&limit=100&stream_position={Uri.EscapeDataString(position)}", ct);
        var root = json.RootElement;
        return (root.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0, Position(root));
    }

    static string Position(JsonElement root) =>
        root.GetProperty("next_stream_position") is { ValueKind: JsonValueKind.Number } number
            ? number.GetRawText()
            : root.GetProperty("next_stream_position").GetString()!;

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseContentRead, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, HttpCompletionOption completion, CancellationToken ct, bool redirects = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = request();
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
            var response = await http.SendAsync(message, completion, ct);
            var status = response.StatusCode;
            // 202: the file isn't ready to download yet, and Retry-After says when it will be.
            if (response.IsSuccessStatusCode && status != HttpStatusCode.Accepted) return response;
            if (redirects && (int)status is >= 300 and < 400 && response.Headers.Location is not null) return response;

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (attempt < Attempts && status is HttpStatusCode.Accepted or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                await Task.Delay(wait, _clock, ct);
                continue;
            }
            throw new StorageException(status, $"Box answered {(int)status}: {(detail.Length <= 300 ? detail : detail[..300])}");
        }
    }
}

/// <summary>
/// One Box account, as a library reads it: by path, though Box knows items only by ID. Each folder on a path is found by
/// listing its parent, and listings are kept a minute so reading a folder's books doesn't list it each time; a listing
/// asked for itself, as a scan asks, is always fresh. Box allows neither a slash nor a backslash in a name, nor two
/// items of one name in a folder, so its names are paths' names as they are.
/// </summary>
public sealed class BoxDrive(BoxClient client, TimeProvider? clock = null) : IStorageDrive, IStorageChangeLog
{
    const string Root = "0";
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (DateTimeOffset Listed, IReadOnlyList<BoxEntry> Entries)> _listings = new();

    public string ProviderName => "Box";

    // Box's download addresses come from a call that needs the token, so the server reads for the reader.
    public bool HasReadLinks => false;

    public async Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default) =>
        await FolderIdAsync(path, ct) is { } id ? [.. (await ListAsync(id, fresh: true, ct)).Select(e => e.Entry)] : null;

    public async Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default)
    {
        var (parent, name) = Split(path);
        if (name.Length == 0) return new StorageEntry("", true, 0, DateTime.MinValue);
        return await FolderIdAsync(parent, ct) is { } id ? (await FindAsync(id, name, folder: null, ct))?.Entry : null;
    }

    public async Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default)
    {
        var (parent, name) = Split(path);
        var file = await FolderIdAsync(parent, ct) is { } id ? await FindAsync(id, name, folder: false, ct) : null;
        if (file is null) throw new StorageException(HttpStatusCode.NotFound, "The file isn't in this Box account.");
        return await client.DownloadAsync(file.Id, range, ct);
    }

    public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<string> StartAsync(CancellationToken ct = default) => client.StreamPositionAsync(ct);

    public Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default) => client.ChangesSinceAsync(cursor, ct);

    async Task<string?> FolderIdAsync(string path, CancellationToken ct)
    {
        var id = Root;
        foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (await FindAsync(id, name, folder: true, ct) is not { } folder) return null;
            id = folder.Id;
        }
        return id;
    }

    /// <summary>An item in a folder by name, from the folder's last listing or, when that hasn't it, a fresh one.</summary>
    async Task<BoxEntry?> FindAsync(string folderId, string name, bool? folder, CancellationToken ct)
    {
        bool Matches(BoxEntry e) => e.Entry.Name == name && (folder is null || e.Entry.IsFolder == folder);
        return (await ListAsync(folderId, fresh: false, ct)).FirstOrDefault(Matches) ?? (await ListAsync(folderId, fresh: true, ct)).FirstOrDefault(Matches);
    }

    async Task<IReadOnlyList<BoxEntry>> ListAsync(string folderId, bool fresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!fresh && _listings.TryGetValue(folderId, out var known) && now - known.Listed < Fresh) return known.Entries;
        var entries = await client.ItemsAsync(folderId, ct);
        _listings[folderId] = (now, entries);
        return entries;
    }

    static (string Parent, string Name) Split(string path)
    {
        var inner = path.Trim('/');
        var slash = inner.LastIndexOf('/');
        return slash < 0 ? ("", inner) : (inner[..slash], inner[(slash + 1)..]);
    }
}

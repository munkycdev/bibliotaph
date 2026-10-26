using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bibliotaph.Storage.GoogleDrive;

/// <summary>A Google Drive's owner: the ID Drive gives them in permissions, which is the same for every app, and their name.</summary>
public sealed record GoogleDriveUser(string PermissionId, string? Name, string? Email);

/// <summary>A file or folder in a Google Drive folder, with the ID it's read by.</summary>
public sealed record GoogleDriveEntry(string Id, StorageEntry Entry);

/// <summary>
/// The few Drive API v3 calls Bibliotaph makes, as one connected account: who it is, a folder's contents, a file's bytes,
/// and changes. Plain HTTP and JSON rather than Google's client library. Rate limiting (429, 5xx, and 403 for rate
/// limits) is waited out.
/// </summary>
public sealed class GoogleDriveClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken, TimeProvider? clock = null)
{
    public const string ApiRoot = "https://www.googleapis.com/drive/v3";
    public const string FolderType = "application/vnd.google-apps.folder";
    const string GoogleTypes = "application/vnd.google-apps.";
    const int Attempts = 5;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<GoogleDriveUser> AboutAsync(CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ApiRoot}/about?fields=user(permissionId,displayName,emailAddress)", ct);
        var user = json!.RootElement.GetProperty("user");
        return new GoogleDriveUser(user.GetProperty("permissionId").GetString()!, Text(user, "displayName"), Text(user, "emailAddress"));
    }

    /// <summary>
    /// A folder's files and folders, by ID ("root" for My Drive's top), every page of them. Google's own documents,
    /// shortcuts and anything in the bin are left out: none is a file to read.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Name, bool IsFolder, long Size, DateTime ModifiedUtc, DateTime CreatedUtc)>> ChildrenAsync(
        string folderId, CancellationToken ct = default)
    {
        var entries = new List<(string, string, bool, long, DateTime, DateTime)>();
        var query = Uri.EscapeDataString($"'{folderId.Replace("'", "\\'", StringComparison.Ordinal)}' in parents and trashed = false");
        string? page = null;
        do
        {
            var url = $"{ApiRoot}/files?q={query}&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime,createdTime)&pageSize=1000"
                + "&supportsAllDrives=true&includeItemsFromAllDrives=true" + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var json = (await GetJsonAsync(url, ct))!;
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var type = Text(file, "mimeType") ?? "";
                var folder = type == FolderType;
                if (!folder && type.StartsWith(GoogleTypes, StringComparison.Ordinal)) continue;
                entries.Add((file.GetProperty("id").GetString()!, file.GetProperty("name").GetString()!, folder,
                    Text(file, "size") is { } size ? long.Parse(size, CultureInfo.InvariantCulture) : 0, Time(file, "modifiedTime"), Time(file, "createdTime")));
            }
            page = Text(json.RootElement, "nextPageToken");
        }
        while (page is not null);
        return entries;
    }

    /// <summary>A file's bytes, or the range of them asked for, streamed.</summary>
    public async Task<Stream> DownloadAsync(string fileId, ByteRange? range = null, CancellationToken ct = default)
    {
        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/files/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true");
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
        return await StorageDownloads.BodyAsync(response, range, ct);
    }

    /// <summary>Where the change log stands now, to ask later what changed since.</summary>
    public async Task<string> StartPageTokenAsync(CancellationToken ct = default)
    {
        using var json = (await GetJsonAsync($"{ApiRoot}/changes/startPageToken?supportsAllDrives=true", ct))!;
        return json.RootElement.GetProperty("startPageToken").GetString()!;
    }

    /// <summary>Whether anything changed since <paramref name="pageToken"/>, and the token to ask from next time.</summary>
    public async Task<(bool Changed, string Next)> ChangesSinceAsync(string pageToken, CancellationToken ct = default)
    {
        var changed = false;
        for (var page = pageToken; ;)
        {
            using var json = (await GetJsonAsync($"{ApiRoot}/changes?pageToken={Uri.EscapeDataString(page)}&pageSize=1000&fields=nextPageToken,newStartPageToken,changes(fileId)"
                + "&supportsAllDrives=true&includeItemsFromAllDrives=true", ct))!;
            var root = json.RootElement;
            changed |= root.GetProperty("changes").GetArrayLength() > 0;
            if (Text(root, "newStartPageToken") is { } next) return (changed, next);
            page = Text(root, "nextPageToken")!;
        }
    }

    static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    static DateTime Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetDateTime().ToUniversalTime() : DateTime.MinValue;

    async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseContentRead, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = request();
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
            var response = await http.SendAsync(message, completion, ct);
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            // Drive answers 403 with a rate limit reason, as well as 429, when it wants calls to slow down.
            var limited = status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                || (status == HttpStatusCode.Forbidden && detail.Contains("ateLimitExceeded", StringComparison.Ordinal));
            if (attempt < Attempts && limited)
            {
                await Task.Delay(wait, _clock, ct);
                continue;
            }
            throw new StorageException(status, $"Google Drive answered {(int)status}: {(detail.Length <= 300 ? detail : detail[..300])}");
        }
    }
}

/// <summary>
/// One Google Drive, as a library reads it: by path, though Drive itself knows files only by ID. Each folder on a path is
/// found by listing its parent, and listings are kept a minute so reading a folder's books doesn't list it each time;
/// a listing asked for itself, as a scan asks, is always fresh. Drive allows slashes in a name and two items of the
/// same name in one folder, which a path can't hold: a slash reads as ∕ (U+2215), a backslash as ∖ (U+2216), and the
/// second and later of a name, oldest first, get " (2)", " (3)" before the extension.
/// </summary>
public sealed class GoogleDriveDrive(GoogleDriveClient client, TimeProvider? clock = null) : IStorageDrive, IStorageChangeLog
{
    const string Root = "root";
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (DateTimeOffset Listed, IReadOnlyList<GoogleDriveEntry> Entries)> _listings = new();

    public string ProviderName => "Google Drive";

    // Drive's download links need the account's token, so the server reads for the reader.
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
        if (file is null) throw new StorageException(HttpStatusCode.NotFound, "The file isn't in this Google Drive.");
        return await client.DownloadAsync(file.Id, range, ct);
    }

    public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<string> StartAsync(CancellationToken ct = default) => client.StartPageTokenAsync(ct);

    public Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default) => client.ChangesSinceAsync(cursor, ct);

    /// <summary>The ID of the folder at a path, or null when there's no folder there.</summary>
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
    async Task<GoogleDriveEntry?> FindAsync(string folderId, string name, bool? folder, CancellationToken ct)
    {
        bool Matches(GoogleDriveEntry e) => e.Entry.Name == name && (folder is null || e.Entry.IsFolder == folder);
        return (await ListAsync(folderId, fresh: false, ct)).FirstOrDefault(Matches) ?? (await ListAsync(folderId, fresh: true, ct)).FirstOrDefault(Matches);
    }

    async Task<IReadOnlyList<GoogleDriveEntry>> ListAsync(string folderId, bool fresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!fresh && _listings.TryGetValue(folderId, out var known) && now - known.Listed < Fresh) return known.Entries;
        var entries = Named(await client.ChildrenAsync(folderId, ct));
        _listings[folderId] = (now, entries);
        return entries;
    }

    internal static IReadOnlyList<GoogleDriveEntry> Named(
        IReadOnlyList<(string Id, string Name, bool IsFolder, long Size, DateTime ModifiedUtc, DateTime CreatedUtc)> items) =>
        [.. items.Select(i => i with { Name = i.Name.Replace('/', '∕').Replace('\\', '∖') })
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .SelectMany(g => g.OrderBy(i => i.CreatedUtc).ThenBy(i => i.Id, StringComparer.Ordinal)
                .Select((i, n) => new GoogleDriveEntry(i.Id, new StorageEntry(n == 0 ? i.Name : Numbered(i.Name, n + 1), i.IsFolder, i.Size, i.ModifiedUtc))))];

    static string Numbered(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }

    static (string Parent, string Name) Split(string path)
    {
        var inner = path.Trim('/');
        var slash = inner.LastIndexOf('/');
        return slash < 0 ? ("", inner) : (inner[..slash], inner[(slash + 1)..]);
    }
}

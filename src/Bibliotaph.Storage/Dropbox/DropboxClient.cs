using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibliotaph.Storage.Dropbox;

/// <summary>
/// A Dropbox account: its ID, the owner's name, and, for a team's account, the team space's root, which listings start
/// from so team folders are there as well as the member's own.
/// </summary>
public sealed record DropboxAccount(string Id, string? Name, string? TeamRoot);

/// <summary>
/// The few Dropbox API v2 calls Bibliotaph makes, as one connected account: who it is, a folder's contents, an item, and
/// a file's bytes. Plain HTTP and JSON rather than the Dropbox SDK. Rate limiting (429, 503) is waited out as Dropbox
/// asks. Paths in Dropbox start with a slash and are empty for the top; library paths have neither, so the slash is
/// added here.
/// </summary>
public sealed class DropboxClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken, string? teamRoot = null, TimeProvider? clock = null)
{
    public const string ApiRoot = "https://api.dropboxapi.com/2";
    public const string ContentRoot = "https://content.dropboxapi.com/2";
    const int Attempts = 5;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DropboxAccount> CurrentAccountAsync(CancellationToken ct = default)
    {
        using var json = (await RpcAsync("users/get_current_account", null, ct))!;
        var root = json.RootElement;
        var name = root.TryGetProperty("name", out var n) && n.TryGetProperty("display_name", out var display) ? display.GetString() : null;
        string? teamRoot = null;
        if (root.TryGetProperty("root_info", out var info) && info.GetProperty(".tag").GetString() == "team"
            && info.GetProperty("root_namespace_id").GetString() is { } rootNamespace && rootNamespace != info.GetProperty("home_namespace_id").GetString())
            teamRoot = rootNamespace;
        return new DropboxAccount(root.GetProperty("account_id").GetString()!, name, teamRoot);
    }

    /// <summary>A folder's contents, every page of them, or null when the folder doesn't exist (or is a file).</summary>
    public async Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<StorageEntry>();
        using var first = await RpcAsync("files/list_folder", new JsonObject { ["path"] = DropboxPath(path), ["limit"] = 2000 }, ct, notFoundIsNull: true);
        if (first is null) return null;
        var page = first.RootElement.Clone();
        while (true)
        {
            foreach (var item in page.GetProperty("entries").EnumerateArray())
                if (Entry(item) is { } entry) entries.Add(entry);
            if (!page.GetProperty("has_more").GetBoolean()) return entries;
            using var next = (await RpcAsync("files/list_folder/continue", new JsonObject { ["cursor"] = page.GetProperty("cursor").GetString() }, ct))!;
            page = next.RootElement.Clone();
        }
    }

    /// <summary>One file or folder, or null when there's nothing at that path. The top has no metadata, so it's made up.</summary>
    public async Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default)
    {
        if (path.Trim('/').Length == 0) return new StorageEntry("", true, 0, DateTime.MinValue);
        using var json = await RpcAsync("files/get_metadata", new JsonObject { ["path"] = DropboxPath(path) }, ct, notFoundIsNull: true);
        return json is null ? null : Entry(json.RootElement);
    }

    /// <summary>A file's bytes, or the range of them asked for, streamed.</summary>
    public async Task<Stream> DownloadAsync(string path, ByteRange? range = null, CancellationToken ct = default)
    {
        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentRoot}/files/download");
            request.Headers.Add("Dropbox-API-Arg", Argument(new JsonObject { ["path"] = DropboxPath(path) }));
            if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
        return await StorageDownloads.BodyAsync(response, range, ct);
    }

    /// <summary>"/RPG/Maps" for the library's "RPG/Maps"; "" for the top, as list_folder wants it.</summary>
    internal static string DropboxPath(string path)
    {
        var inner = path.Trim('/');
        return inner.Length == 0 ? "" : "/" + inner;
    }

    /// <summary>
    /// The JSON a content call takes in a header. Headers carry ASCII only, so everything else is escaped, as Dropbox
    /// expects; System.Text.Json's default encoder escapes all of it.
    /// </summary>
    internal static string Argument(JsonObject value) => value.ToJsonString();

    // Files a user can't download, such as Paper docs, are left out: there's nothing to read.
    static StorageEntry? Entry(JsonElement item) => item.GetProperty(".tag").GetString() switch
    {
        "folder" => new StorageEntry(item.GetProperty("name").GetString()!, true, 0, DateTime.MinValue),
        "file" when !item.TryGetProperty("is_downloadable", out var downloadable) || downloadable.GetBoolean() => new StorageEntry(
            item.GetProperty("name").GetString()!, false, item.GetProperty("size").GetInt64(), item.GetProperty("server_modified").GetDateTime().ToUniversalTime()),
        _ => null,
    };

    async Task<JsonDocument?> RpcAsync(string endpoint, JsonObject? argument, CancellationToken ct, bool notFoundIsNull = false)
    {
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}/{endpoint}")
            {
                // A call that takes no argument takes no body at all; "null" or "{}" are refused for some.
                Content = argument is null ? null : new StringContent(argument.ToJsonString(), Encoding.UTF8, "application/json"),
            }, HttpCompletionOption.ResponseContentRead, ct);
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (StorageException ex) when (notFoundIsNull && ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var message = request();
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
            if (teamRoot is not null) message.Headers.Add("Dropbox-API-Path-Root", Argument(new JsonObject { [".tag"] = "root", ["root"] = teamRoot }));
            var response = await http.SendAsync(message, completion, ct);
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (attempt < Attempts && status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(wait, _clock, ct);
                continue;
            }
            throw new StorageException(Status(status, detail), $"Dropbox answered {(int)status}: {Summary(detail)}");
        }
    }

    // An endpoint's own errors come back as 409 with a summary such as "path/not_found/..", which is what callers ask about.
    static HttpStatusCode Status(HttpStatusCode status, string detail) =>
        status == HttpStatusCode.Conflict && (detail.Contains("not_found", StringComparison.Ordinal) || detail.Contains("not_folder", StringComparison.Ordinal)
            || detail.Contains("not_file", StringComparison.Ordinal))
            ? HttpStatusCode.NotFound
            : status;

    static string Summary(string detail)
    {
        try
        {
            using var json = JsonDocument.Parse(detail);
            if (json.RootElement.TryGetProperty("error_summary", out var summary) && summary.GetString() is { } text) return text;
        }
        catch (JsonException)
        {
        }
        return detail.Length <= 300 ? detail : detail[..300];
    }
}

/// <summary>One Dropbox account, as a library reads it.</summary>
public sealed class DropboxDrive(DropboxClient client) : IStorageDrive
{
    public string ProviderName => "Dropbox";

    // Dropbox's temporary links aren't documented as readable from another site's page, so the server reads for the reader.
    public bool HasReadLinks => false;

    public Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default) => client.ItemAsync(path, ct);

    public Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default) => client.ChildrenAsync(path, ct);

    public Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default) => client.DownloadAsync(path, range, ct);

    public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

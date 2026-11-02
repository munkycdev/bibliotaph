using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bibliotaph.Storage.Nornis;

/// <summary>Who a Nornis connection belongs to: GET /api/bibliotaph/v1/me.</summary>
public sealed record NornisMe(string UserId, string DisplayName);

/// <summary>A world the user created in Nornis.</summary>
public sealed record NornisWorld(string WorldId, string Name, DateTimeOffset UpdatedAt);

/// <summary>A file uploaded to a world that Nornis lets Bibliotaph read: a sourcebook, an Other file or a Prep print.</summary>
public sealed record NornisFile(string FileId, string FileName, string? ContentType, long SizeBytes, DateTimeOffset UpdatedAt, string? Etag);

/// <summary>A read-only address for a file's bytes, good until <see cref="ExpiresAt"/>.</summary>
public sealed record NornisLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>One page of the worlds a user created.</summary>
public sealed record NornisWorldPage(IReadOnlyList<NornisWorld> Worlds, string? NextCursor);

/// <summary>One page of a world's files.</summary>
public sealed record NornisFilePage(IReadOnlyList<NornisFile> Files, string? NextCursor);

/// <summary>
/// Nornis's API for Bibliotaph, as one connected user: who they are, their worlds, a world's files and a file's link.
/// The paths and shapes are the integration contract; Nornis serves them under its own API's address. Nornis refusing
/// the token (401) means only reconnecting will help, so it's reported once through <paramref name="refused"/>.
/// </summary>
public sealed class NornisClient(HttpClient http, Uri apiBase, Func<CancellationToken, Task<string>> accessToken,
    Func<CancellationToken, Task>? refused = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<NornisMe> MeAsync(CancellationToken ct = default) =>
        await GetAsync<NornisMe>("api/bibliotaph/v1/me", ct) ?? throw new StorageException(HttpStatusCode.NotFound, "Nornis didn't say who this is.");

    /// <summary>Every world the user created, across all pages.</summary>
    public async Task<IReadOnlyList<NornisWorld>> WorldsAsync(CancellationToken ct = default)
    {
        var worlds = new List<NornisWorld>();
        string? cursor = null;
        do
        {
            var page = await GetAsync<NornisWorldPage>("api/bibliotaph/v1/worlds" + Cursor(cursor), ct)
                ?? throw new StorageException(HttpStatusCode.NotFound, "Nornis didn't list the worlds.");
            worlds.AddRange(page.Worlds);
            cursor = page.NextCursor is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null);
        return worlds;
    }

    /// <summary>A world's files, across all pages, or null when there's no such world (or it isn't the user's).</summary>
    public async Task<IReadOnlyList<NornisFile>?> FilesAsync(string worldId, CancellationToken ct = default)
    {
        var files = new List<NornisFile>();
        string? cursor = null;
        do
        {
            if (await GetAsync<NornisFilePage>($"api/bibliotaph/v1/worlds/{Uri.EscapeDataString(worldId)}/files" + Cursor(cursor), ct) is not { } page)
                return null;
            files.AddRange(page.Files);
            cursor = page.NextCursor is { Length: > 0 } next ? next : null;
        }
        while (cursor is not null);
        return files;
    }

    /// <summary>A read-only link to a file's bytes, or null when the file is gone.</summary>
    public Task<NornisLink?> LinkAsync(string fileId, CancellationToken ct = default) =>
        GetAsync<NornisLink>($"api/bibliotaph/v1/files/{Uri.EscapeDataString(fileId)}/link", ct);

    /// <summary>A file's bytes, or the range asked for, from its link: the link carries its own signature, so no token goes with it.</summary>
    public async Task<Stream> DownloadAsync(string fileId, ByteRange? range = null, CancellationToken ct = default)
    {
        var link = await LinkAsync(fileId, ct) ?? throw new StorageException(HttpStatusCode.NotFound, "The file isn't in Nornis any more.");
        using var request = new HttpRequestMessage(HttpMethod.Get, link.Url);
        if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new StorageException(status, $"Nornis's file storage answered {(int)status}.");
        }
        return await StorageDownloads.BodyAsync(response, range, ct);
    }

    /// <summary>Asks Nornis to send a created event again for every world the user created, which its outbox does in its own time.</summary>
    public async Task ResyncAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, "api/bibliotaph/v1/resync"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && refused is not null) await refused(ct);
        if (!response.IsSuccessStatusCode) throw new StorageException(response.StatusCode, $"Nornis answered {(int)response.StatusCode}.");
    }

    static string Cursor(string? cursor) => cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor);

    async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized && refused is not null) await refused(ct);
        if (!response.IsSuccessStatusCode) throw new StorageException(response.StatusCode, $"Nornis answered {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}

/// <summary>
/// A Nornis connection as a library reads it. Its top level is the user's worlds, each a folder named by its world ID,
/// which a rename in Nornis doesn't change; in a world are its files by their Nornis names. Listings are kept a minute so
/// reading a world's books doesn't list it each time; a listing asked for itself, as a scan asks, is always fresh.
/// </summary>
public sealed class NornisDrive(NornisClient client, TimeProvider? clock = null) : IStorageDrive
{
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, (DateTimeOffset Listed, IReadOnlyList<NornisFile>? Files)> _listings = new();

    public string ProviderName => "Nornis";

    /// <summary>Nornis's API as this connection, for what isn't reading files.</summary>
    public NornisClient Client => client;

    // Nornis hands out read-only SAS addresses, which a browser reads byte ranges from.
    public bool HasReadLinks => true;

    public async Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default)
    {
        var parts = Parts(path);
        if (parts.Length == 0)
            return [.. (await client.WorldsAsync(ct)).Select(w => new StorageEntry(w.WorldId, true, 0, w.UpdatedAt.UtcDateTime))];
        if (parts.Length > 1) return null;
        return await ListAsync(parts[0], fresh: true, ct) is { } files ? [.. files.Select(Entry)] : null;
    }

    public async Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default)
    {
        var parts = Parts(path);
        return parts.Length switch
        {
            0 => new StorageEntry("", true, 0, DateTime.MinValue),
            1 => await ListAsync(parts[0], fresh: false, ct) is not null ? new StorageEntry(parts[0], true, 0, DateTime.MinValue) : null,
            2 => await FindAsync(parts[0], parts[1], ct) is { } file ? Entry(file) : null,
            _ => null,
        };
    }

    public async Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default) =>
        await client.DownloadAsync((await FileAsync(path, ct)).FileId, range, ct);

    public async Task<string?> ReadLinkAsync(string path, CancellationToken ct = default)
    {
        var parts = Parts(path);
        if (parts.Length != 2 || await FindAsync(parts[0], parts[1], ct) is not { } file) return null;
        return (await client.LinkAsync(file.FileId, ct))?.Url;
    }

    /// <summary>The Nornis file at a path, for matching an upload to Nornis's own row.</summary>
    public async Task<NornisFile?> FindFileAsync(string path, CancellationToken ct = default)
    {
        var parts = Parts(path);
        return parts.Length == 2 ? await FindAsync(parts[0], parts[1], ct) : null;
    }

    async Task<NornisFile> FileAsync(string path, CancellationToken ct) =>
        await FindFileAsync(path, ct) ?? throw new StorageException(HttpStatusCode.NotFound, "The file isn't in Nornis.");

    async Task<NornisFile?> FindAsync(string worldId, string name, CancellationToken ct)
    {
        bool Matches(NornisFile f) => f.FileName == name;
        return (await ListAsync(worldId, fresh: false, ct))?.FirstOrDefault(Matches) ?? (await ListAsync(worldId, fresh: true, ct))?.FirstOrDefault(Matches);
    }

    async Task<IReadOnlyList<NornisFile>?> ListAsync(string worldId, bool fresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!fresh && _listings.TryGetValue(worldId, out var known) && now - known.Listed < Fresh) return known.Files;
        var files = await client.FilesAsync(worldId, ct);
        _listings[worldId] = (now, files);
        return files;
    }

    static StorageEntry Entry(NornisFile file) => new(file.FileName, false, file.SizeBytes, file.UpdatedAt.UtcDateTime);

    static string[] Parts(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

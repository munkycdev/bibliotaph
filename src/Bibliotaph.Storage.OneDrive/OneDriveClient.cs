using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bibliotaph.Storage.OneDrive;

/// <summary>A OneDrive: its ID, whether it's personal or a work or school one, and whose it is.</summary>
public sealed record DriveSummary(string Id, string DriveType, string? OwnerName);

/// <summary>A file or folder in a drive. <see cref="Size"/> is a folder's total for a folder.</summary>
public sealed record DriveEntry(string Id, string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>A Graph change notification subscription on a drive, which must be renewed before it expires.</summary>
public sealed record DriveSubscription(string Id, DateTimeOffset Expires);

/// <summary>
/// Graph answered with an error Bibliotaph can't work around, such as a folder that's gone or access withdrawn. An
/// IOException, so indexing treats a book it couldn't download like a file it couldn't open.
/// </summary>
public sealed class OneDriveException(HttpStatusCode status, string message) : IOException(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The few Microsoft Graph calls Bibliotaph makes on a OneDrive, as one signed-in account: what the drive is, a
/// folder's contents, a file's bytes, and change notifications. Plain HTTP and JSON rather than the Graph SDK, which
/// would bring far more than this uses. Throttling (429, 503) is waited out as Graph asks.
/// </summary>
public sealed class OneDriveClient(HttpClient http, Func<CancellationToken, Task<string>> accessToken, TimeProvider? clock = null)
{
    public const string GraphRoot = "https://graph.microsoft.com/v1.0";
    const int Attempts = 5;
    const string EntryFields = "id,name,size,lastModifiedDateTime,folder,file";

    // Graph allows a driveItem subscription about 29 days; renewing a day early leaves room for a missed renewal.
    public static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(42_300);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The signed-in account's own drive.</summary>
    public async Task<DriveSummary> MyDriveAsync(CancellationToken ct = default)
    {
        using var json = (await GetJsonAsync($"{GraphRoot}/me/drive?$select=id,driveType,owner", ct))!;
        var root = json.RootElement;
        var owner = root.TryGetProperty("owner", out var o) && o.TryGetProperty("user", out var u) && u.TryGetProperty("displayName", out var name)
            ? name.GetString()
            : null;
        return new DriveSummary(root.GetProperty("id").GetString()!, root.GetProperty("driveType").GetString() ?? "personal", owner);
    }

    /// <summary>A folder's contents, every page of them. Paths in a drive are slash-separated, empty for its top.</summary>
    /// <returns>Null when the folder doesn't exist (or is a file).</returns>
    public async Task<IReadOnlyList<DriveEntry>?> ChildrenAsync(string driveId, string pathInDrive, CancellationToken ct = default)
    {
        var entries = new List<DriveEntry>();
        string? next = $"{ItemUrl(driveId, pathInDrive)}{(pathInDrive.Length == 0 ? "/children" : ":/children")}?$select={EntryFields}&$top=999";
        while (next is not null)
        {
            using var json = await GetJsonAsync(next, ct, notFoundIsNull: true);
            if (json is null) return null;
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray()) entries.Add(Entry(item));
            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return entries;
    }

    /// <summary>One file or folder, or null when there's nothing at that path.</summary>
    public async Task<DriveEntry?> ItemAsync(string driveId, string pathInDrive, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{ItemUrl(driveId, pathInDrive)}?$select={EntryFields}", ct, notFoundIsNull: true);
        return json is null ? null : Entry(json.RootElement);
    }

    /// <summary>
    /// A short-lived link that reads the file without a Graph token, for a browser to fetch byte ranges from directly,
    /// or null when there's nothing at that path. The whole item is asked for, since a business drive leaves the link
    /// out of a $select.
    /// </summary>
    public async Task<string?> DownloadLinkAsync(string driveId, string pathInDrive, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(ItemUrl(driveId, pathInDrive), ct, notFoundIsNull: true);
        if (json is null) return null;
        var item = json.RootElement;
        return item.TryGetProperty("@microsoft.graph.downloadUrl", out var link) || item.TryGetProperty("@content.downloadUrl", out link)
            ? link.GetString()
            : null;
    }

    /// <summary>
    /// A file's bytes, streamed. Graph answers with a redirect to a short-lived download link, which carries its own
    /// authorisation; HttpClient doesn't send the Graph token on to it.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string driveId, string pathInDrive, CancellationToken ct = default)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(driveId, pathInDrive)}:/content"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>Asks Graph to call <paramref name="notificationUrl"/> whenever anything in the drive changes.</summary>
    public async Task<DriveSubscription> SubscribeAsync(DriveSummary drive, string notificationUrl, string clientState, CancellationToken ct = default)
    {
        // A personal drive takes subscriptions only on the signed-in user's own drive root.
        var resource = drive.DriveType == "personal" ? "/me/drive/root" : $"/drives/{drive.Id}/root";
        var expires = _clock.GetUtcNow() + SubscriptionLifetime;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{GraphRoot}/subscriptions")
        {
            Content = JsonContent.Create(new
            {
                changeType = "updated",
                notificationUrl,
                resource,
                expirationDateTime = expires.ToString("o", CultureInfo.InvariantCulture),
                clientState,
            }),
        }, HttpCompletionOption.ResponseContentRead, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return new DriveSubscription(json.RootElement.GetProperty("id").GetString()!, expires);
    }

    /// <returns>The new expiry, or null when the subscription no longer exists and must be made again.</returns>
    public async Task<DateTimeOffset?> RenewAsync(string subscriptionId, CancellationToken ct = default)
    {
        var expires = _clock.GetUtcNow() + SubscriptionLifetime;
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{GraphRoot}/subscriptions/{Uri.EscapeDataString(subscriptionId)}")
            {
                Content = JsonContent.Create(new { expirationDateTime = expires.ToString("o", CultureInfo.InvariantCulture) }),
            }, HttpCompletionOption.ResponseContentRead, ct);
            return expires;
        }
        catch (OneDriveException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default)
    {
        try
        {
            using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{GraphRoot}/subscriptions/{Uri.EscapeDataString(subscriptionId)}"),
                HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OneDriveException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>Graph's address for an item by path: the drive's root, or <c>root:/a/b</c> with each name escaped.</summary>
    internal static string ItemUrl(string driveId, string pathInDrive)
    {
        var drive = $"{GraphRoot}/drives/{Uri.EscapeDataString(driveId)}/root";
        if (pathInDrive.Length == 0) return drive;
        var escaped = string.Join('/', pathInDrive.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"{drive}:/{escaped}";
    }

    static DriveEntry Entry(JsonElement item) => new(
        item.GetProperty("id").GetString()!,
        item.GetProperty("name").GetString()!,
        item.TryGetProperty("folder", out _),
        item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
        item.TryGetProperty("lastModifiedDateTime", out var modified) ? modified.GetDateTime().ToUniversalTime() : DateTime.MinValue);

    async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct, bool notFoundIsNull = false)
    {
        try
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseContentRead, ct);
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (OneDriveException ex) when (notFoundIsNull && ex.Status == HttpStatusCode.NotFound)
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
            var response = await http.SendAsync(message, completion, ct);
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            var detail = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (attempt < Attempts && status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                await Task.Delay(wait, _clock, ct);
                continue;
            }
            throw new OneDriveException(status, $"OneDrive answered {(int)status}: {Truncate(detail)}");
        }
    }

    static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}

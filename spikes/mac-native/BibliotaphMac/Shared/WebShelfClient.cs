using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.MacSpike;

/// <summary>Where this Mac keeps the key bibliotaph.online gave it when it was approved.</summary>
public interface IDeviceKey
{
    string? Find();
    void Remember(string key);
    void Forget();
}

/// <summary>
/// This Mac's side of bibliotaph.online's /api/devices and /api/pc endpoints: connecting as a device the way a PC does,
/// then reading the web library's shelf, a book's reading (for the name of its cover) and covers.
/// </summary>
public sealed class WebShelfClient(HttpClient http, IDeviceKey key)
{
    public static readonly Uri Address = new("https://bibliotaph.online/");

    public bool HasKey => key.Find() is not null;

    public async Task<DeviceConnectStarted?> StartAsync(string name, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/devices/connect", new ConnectDevice(name), withKey: false, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DeviceConnectStarted>(ct) : null;
    }

    /// <summary>Asks every two seconds until the request is approved (true), declined or expired (false).</summary>
    public async Task<bool> WaitAsync(DeviceConnectStarted started, CancellationToken ct = default)
    {
        while (DateTimeOffset.UtcNow < started.Expires.AddSeconds(5))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            DeviceConnectState? state;
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "api/devices/connect/poll", new DeviceConnectPoll(started.PollToken), withKey: false, ct);
                if (!response.IsSuccessStatusCode) continue;
                state = await response.Content.ReadFromJsonAsync<DeviceConnectState>(ct);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            switch (state?.State)
            {
                case DeviceConnectState.Waiting:
                    continue;
                case DeviceConnectState.Connected when state.Key is { } secret:
                    key.Remember(secret);
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>Tells the web library to forget this Mac, then forgets the key here whatever it answered.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, "api/devices/me", null, withKey: true, ct);
        }
        catch (HttpRequestException)
        {
        }
        key.Forget();
    }

    /// <summary>The web library's folders and the files it knows by content, or null when it couldn't be read.</summary>
    public async Task<WebShelf?> ReadShelfAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/pc/library", null, withKey: true, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<WebShelf>(ct) : null;
    }

    /// <summary>The cover the web library drew for this content, as JPEG bytes, or null when it has none yet.</summary>
    public async Task<byte[]?> ReadCoverAsync(string contentHash, CancellationToken ct = default)
    {
        string? name;
        using (var reading = await SendAsync(HttpMethod.Get, $"api/pc/books/{Uri.EscapeDataString(contentHash)}/reading", null, withKey: true, ct))
        {
            if (!reading.IsSuccessStatusCode) return null;
            // Only the cover's name is needed, so the reading's pages and text are skipped rather than modelled.
            using var json = await JsonDocument.ParseAsync(await reading.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            name = json.RootElement.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.String ? cover.GetString() : null;
        }
        if (name is null) return null;
        using var response = await SendAsync(HttpMethod.Get, $"api/pc/covers/{Uri.EscapeDataString(name)}", null, withKey: true, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool withKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(Address, path));
        request.Headers.Add(ApiHeaders.Client, "mac");
        if (withKey && key.Find() is { } secret) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return await http.SendAsync(request, ct);
    }
}

/// <summary>The one header every call carries; the contracts' own copy lives in a file the spike doesn't link.</summary>
static class ApiHeaders
{
    public const string Client = "X-Bibliotaph";
}

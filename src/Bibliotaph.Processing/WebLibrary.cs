using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

/// <summary>Where the app keeps this PC's key to the web library: Credential Manager, or memory in the smoke test.</summary>
public interface IWebLibraryKey
{
    string? Find();

    /// <summary>Returns false when it couldn't be stored.</summary>
    bool Remember(string key);

    void Forget();
}

/// <summary>Whether this PC is connected to a web library, and whose.</summary>
public enum WebLibraryState
{
    NotConnected,
    Connected,

    /// <summary>Connected, but the web library couldn't be reached just now.</summary>
    Unreachable,

    /// <summary>The PC was disconnected on the web, or the account closed; its key has been forgotten.</summary>
    Disconnected,
}

public sealed record WebLibraryStatus(WebLibraryState State, string? UserName = null, string? DeviceName = null);

/// <summary>How asking to connect ended.</summary>
public enum WebConnectOutcome
{
    Connected,
    Declined,
    Expired,
    Failed,
}

/// <summary>
/// This PC's connection to the user's library at bibliotaph.online. Connecting works as a TV signs in: the app asks,
/// opens the approval page in the browser, where the user is signed in, and asks again until the user answers. The
/// PC then has a key of its own, kept in Credential Manager, which the web can disconnect at any time. While connected,
/// it is also library sync's way to the web library: this PC's sync file goes up, and the web library's comes down.
/// </summary>
public sealed class WebLibrary(HttpClient http, IWebLibraryKey key, SettingsStore settings, TimeProvider clock, ILogger<WebLibrary> log) : ISyncChannel
{
    /// <summary>The other devices' files as last fetched, by device, so an unchanged one isn't fetched again.</summary>
    readonly ConcurrentDictionary<string, (long Version, SyncFileContent Content)> _peers = new(StringComparer.Ordinal);

    public static readonly Uri DefaultAddress = new("https://bibliotaph.online/");

    /// <summary>How often the app asks whether the user has answered.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    public bool HasKey => key.Find() is not null;

    /// <summary>The web library's address; a setting only so a developer can point the app at their own server.</summary>
    public async Task<Uri> AddressAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.WebAddress, ct) is { } set && Uri.TryCreate(set.TrimEnd('/') + "/", UriKind.Absolute, out var address)
            ? address : DefaultAddress;

    /// <summary>Asks to connect this PC under <paramref name="name"/>: the answer has the approval page to open and the code to show.</summary>
    public async Task<DeviceConnectStarted?> StartAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/devices/connect", new ConnectDevice(name), withKey: false, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<DeviceConnectStarted>(ct);
            log.LogWarning("Asking to connect to the web library got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogWarning(ex, "Couldn't reach the web library to connect");
        }
        return null;
    }

    /// <summary>Asks again until the user approves or declines on the web, or the request expires. On approval, keeps the key.</summary>
    public async Task<WebConnectOutcome> WaitAsync(DeviceConnectStarted started, CancellationToken ct = default)
    {
        var failures = 0;
        while (true)
        {
            await Task.Delay(PollEvery, clock, ct);
            DeviceConnectState? state = null;
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "api/devices/connect/poll", new DeviceConnectPoll(started.PollToken), withKey: false, ct);
                if (response.IsSuccessStatusCode) state = await response.Content.ReadFromJsonAsync<DeviceConnectState>(ct);
            }
            catch (Exception ex) when (Unreachable(ex, ct))
            {
                log.LogInformation(ex, "Asking whether the PC was approved failed");
            }
            // A dropped connection while the user is in the browser shouldn't end the wait; a run of them does.
            if (state is null)
            {
                if (++failures >= 5) return WebConnectOutcome.Failed;
                continue;
            }
            failures = 0;
            switch (state.State)
            {
                case DeviceConnectState.Waiting:
                    if (clock.GetUtcNow() > started.Expires + PollEvery) return WebConnectOutcome.Expired;
                    continue;
                case DeviceConnectState.Connected when state.Key is { } connected:
                    return key.Remember(connected) ? WebConnectOutcome.Connected : WebConnectOutcome.Failed;
                case DeviceConnectState.Declined:
                    return WebConnectOutcome.Declined;
                default:
                    return WebConnectOutcome.Expired;
            }
        }
    }

    /// <summary>Whose library this PC is connected to. A key the web no longer accepts is forgotten.</summary>
    public async Task<WebLibraryStatus> CheckAsync(CancellationToken ct = default)
    {
        if (!HasKey) return new WebLibraryStatus(WebLibraryState.NotConnected);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/devices/me", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                key.Forget();
                return new WebLibraryStatus(WebLibraryState.Disconnected);
            }
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<DeviceIdentity>(ct) is { } identity)
                return new WebLibraryStatus(WebLibraryState.Connected, identity.UserName, identity.Name);
            log.LogWarning("Checking the web library connection got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library");
        }
        return new WebLibraryStatus(WebLibraryState.Unreachable);
    }

    /// <summary>Disconnects this PC: the web forgets it when it can be reached, and the PC forgets its key either way.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!HasKey) return;
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, "api/devices/me", null, withKey: true, ct);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't tell the web library this PC disconnected; it stays in the web's list until removed there");
        }
        key.Forget();
    }

    public bool IsOn => HasKey;

    // The web library is asked every minute instead; it can't call the PC.
    event EventHandler? ISyncChannel.Changed
    {
        add { }
        remove { }
    }

    async Task<string> UnreachableAsync(CancellationToken ct) =>
        $"{(await AddressAsync(ct)).Host} can't be reached just now. Bibliotaph tries again in a few minutes.";

    const string DisconnectedProblem = "This PC was disconnected from your web library. Connect it again in Settings > Web library.";

    /// <summary>The other devices' files the web library keeps, or why they can't be listed.</summary>
    async Task<(IReadOnlyList<SyncFileInfo>? Files, string? Problem)> ListAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/sync", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                key.Forget();
                return (null, DisconnectedProblem);
            }
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<List<SyncFileInfo>>(ct) ?? [], null);
            log.LogWarning("Listing the web library's sync files got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library to sync");
        }
        return (null, await UnreachableAsync(ct));
    }

    public async Task<SyncChannelRead> ReadPeersAsync(CancellationToken ct)
    {
        var (files, problem) = await ListAsync(ct);
        if (files is null) return new SyncChannelRead([], problem);
        var peers = new List<SyncFileContent>();
        foreach (var file in files)
        {
            if (_peers.TryGetValue(file.Device, out var known) && known.Version == file.Version)
            {
                peers.Add(known.Content);
                continue;
            }
            try
            {
                using var response = await SendAsync(HttpMethod.Get, $"api/sync/{Uri.EscapeDataString(file.Device)}", null, withKey: true, ct);
                // Written again or gone since it was listed: the next pass has it.
                if (!response.IsSuccessStatusCode) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var content = await SyncFile.ReadAsync(stream, ct);
                _peers[file.Device] = (file.Version, content);
                peers.Add(content);
            }
            catch (InvalidDataException ex)
            {
                problem ??= $"{file.Machine}'s sync file from the web library can't be read. {ex.Message}";
            }
            catch (Exception ex) when (Unreachable(ex, ct))
            {
                log.LogInformation(ex, "Fetching a sync file from the web library failed");
                problem ??= await UnreachableAsync(ct);
            }
        }
        foreach (var gone in _peers.Keys.Except(files.Select(f => f.Device)).ToList()) _peers.TryRemove(gone, out _);
        return new SyncChannelRead(peers, problem);
    }

    public async Task<string?> WriteOwnAsync(Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await write(buffer, ct);
        buffer.Position = 0;
        try
        {
            using var content = new StreamContent(buffer);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await SendAsync(HttpMethod.Put, "api/sync/mine", content, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                key.Forget();
                return DisconnectedProblem;
            }
            if (response.IsSuccessStatusCode) return null;
            log.LogWarning("Sending this PC's sync file to the web library got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Sending this PC's sync file to the web library failed");
        }
        return await UnreachableAsync(ct);
    }

    public async Task<bool> HasChangesAsync(CancellationToken ct)
    {
        var (files, _) = await ListAsync(ct);
        return files is not null
            && (files.Count != _peers.Count || files.Any(f => !_peers.TryGetValue(f.Device, out var known) || known.Version != f.Version));
    }

    /// <summary>No answer, or none in time; the caller cancelling is not one of these.</summary>
    static bool Unreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool withKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(await AddressAsync(ct), path));
        request.Headers.Add(ApiHeaders.Client, "windows");
        if (withKey && key.Find() is { } secret) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Content = body switch { null => null, HttpContent content => content, _ => JsonContent.Create(body, body.GetType()) };
        return await http.SendAsync(request, ct);
    }
}

/// <summary>A key held in memory: the smoke test's and the tests', which must not write to Credential Manager.</summary>
public sealed class MemoryWebLibraryKey : IWebLibraryKey
{
    string? _key;

    public string? Find() => _key;

    public bool Remember(string key)
    {
        _key = key;
        return true;
    }

    public void Forget() => _key = null;
}

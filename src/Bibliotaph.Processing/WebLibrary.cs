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
/// it is also library sync's way to the web library: this PC's sync file goes up, and the web library's comes down; and
/// what the AI worked out about the PC's files goes up, and the user's other devices' comes down (<see cref="SharedResults"/>).
/// </summary>
public sealed class WebLibrary(HttpClient http, IWebLibraryKey key, SettingsStore settings, TimeProvider clock, ILogger<WebLibrary> log) : ISyncChannel, IResultChannel, IWebBooks
{
    /// <summary>The other devices' files as last fetched, by device, so an unchanged one isn't fetched again.</summary>
    readonly ConcurrentDictionary<string, (long Version, SyncFileContent Content)> _peers = new(StringComparer.Ordinal);

    public static readonly Uri DefaultAddress = new("https://bibliotaph.online/");

    /// <summary>How often the app asks whether the user has answered.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long a request waits for an answer before the web library counts as out of reach.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a lookup waits: the server asks RPGGeek at most every five seconds for every library and PC together,
    /// so a book can wait behind several others' before its turn, and that is the server busy, not out of reach.
    /// </summary>
    public static readonly TimeSpan LookUpPatience = TimeSpan.FromMinutes(3);

    public bool HasKey => key.Find() is not null;

    /// <summary>
    /// Whether the last request found the web library out of reach: no connection, or no answer in time. Any answer,
    /// even a refusal, means it can be reached. A slow lookup doesn't count, since the server answers those in turn.
    /// </summary>
    public bool IsUnreachable { get; private set; }

    /// <summary>Raised when <see cref="IsUnreachable"/> changes, on whichever thread made the request.</summary>
    public event EventHandler? ReachabilityChanged;

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

    const string DisconnectedProblem = "This PC was disconnected from your web library. Connect it again in Settings > Devices.";

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

    public async Task<SharedResultPage?> ReadResultsAsync(long after, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/results?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null,
                withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<SharedResultPage>(ct);
            log.LogWarning("Asking the web library for shared results got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library for shared results");
        }
        return null;
    }

    public async Task<bool> WriteResultsAsync(IReadOnlyList<SharedResult> results, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/results", new SharedResultUpload(results), withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.IsSuccessStatusCode) return true;
            log.LogWarning("Sending results to the web library got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library to share results");
        }
        return false;
    }

    public async Task<SharedResult?> ReadResultAsync(string kind, string contentHash, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/results/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(contentHash)}", null,
                withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<SharedResult>(ct);
            log.LogWarning("Asking the web library for a shared result got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library for a shared result");
        }
        return null;
    }

    /// <summary>
    /// The web library's files, unless the shelf is still <paramref name="version"/>: <see cref="WebShelfRead.Unchanged"/>
    /// then, or <see cref="WebShelfRead.Unreached"/> when the server didn't answer, so the PC keeps the shelf it has.
    /// </summary>
    public async Task<WebShelfRead> ReadShelfAsync(string? version, CancellationToken ct)
    {
        try
        {
            var query = version is null ? "" : $"?version={Uri.EscapeDataString(version)}";
            using var response = await SendAsync(HttpMethod.Get, $"api/pc/library{query}", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.StatusCode == HttpStatusCode.NotModified) return WebShelfRead.Unchanged;
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<WebShelf>(ct) is { } shelf) return new WebShelfRead(true, shelf);
            log.LogWarning("Asking the web library for its files got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library for its files");
        }
        return WebShelfRead.Unreached;
    }

    /// <summary>
    /// What the web library read from the file with this content: <see cref="WebReading.NotYet"/> when it hasn't yet or
    /// didn't answer, <see cref="WebReading.Unreadable"/> when this PC should read the file itself.
    /// </summary>
    public async Task<WebReading> ReadReadingAsync(string contentHash, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/pc/books/{Uri.EscapeDataString(contentHash)}/reading", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.StatusCode == HttpStatusCode.NotFound) return WebReading.NotYet;
            if (response.StatusCode == HttpStatusCode.Conflict) return WebReading.Unreadable;
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<SharedReading>(ct) is { } reading) return new WebReading(reading, false);
            log.LogWarning("Asking the web library for a book's reading got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library for a book's reading");
        }
        return WebReading.NotYet;
    }

    /// <summary>A cover the web library drew, by its file name, or null when it has none by that name or didn't answer.</summary>
    public async Task<byte[]?> ReadCoverAsync(string name, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/pc/covers/{Uri.EscapeDataString(name)}", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
            log.LogWarning("Asking the web library for a cover got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library for a cover");
        }
        return null;
    }

    /// <summary>
    /// Where the web library looks books up, or null when it didn't answer or this PC isn't connected. A key the web no
    /// longer accepts is forgotten.
    /// </summary>
    public async Task<string?> LookupCataloguesAsync(CancellationToken ct)
    {
        if (!HasKey) return null;
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/pc/lookups", null, withKey: true, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<PcLookupAnswer>(ct) is { } answer) return answer.Catalogues;
            log.LogWarning("Asking the web library where it looks books up got {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            log.LogInformation(ex, "Couldn't reach the web library to ask where it looks books up");
        }
        return null;
    }

    /// <summary>
    /// Asks the web library to look a book up by its ISBNs where it can: its answer, with <see cref="PcLookupAnswer.Problem"/>
    /// set when it couldn't ask; or null when it didn't answer.
    /// </summary>
    public async Task<PcLookupAnswer?> LookUpAsync(IReadOnlyList<string> isbns, IReadOnlyList<string> titles, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/pc/lookups", new PcLookupRequest(isbns, titles), withKey: true, ct,
                patience: LookUpPatience);
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if ((response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                && await response.Content.ReadFromJsonAsync<PcLookupAnswer>(ct) is { } answer) return answer;
            log.LogWarning("Asking the web library to look a book up got {Status}", (int)response.StatusCode);
        }
        // A 503 from something in front of the server, rather than the server, isn't its answer.
        catch (Exception ex) when (Unreachable(ex, ct) || ex is System.Text.Json.JsonException)
        {
            log.LogInformation(ex, "Couldn't reach the web library to look a book up");
        }
        return null;
    }

    public async Task<Stream> OpenFileAsync(string contentHash, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            // Storage with links anyone may read redirects there; the key isn't sent on to it.
            response = await SendAsync(HttpMethod.Get, $"api/pc/books/{Uri.EscapeDataString(contentHash)}/file", null, withKey: true, ct,
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            throw new WebLibraryUnreachableException(ex);
        }
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStreamAsync(ct);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) key.Forget();
            if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("The file isn't in your web library any more.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new IOException("That's as much as bibliotaph.online reads from your storage in a day. Try again tomorrow.");
            log.LogWarning("Downloading a book from the web library got {Status}", (int)response.StatusCode);
            throw new IOException($"bibliotaph.online couldn't give the file ({(int)response.StatusCode}).");
        }
    }

    /// <summary>No answer, or none in time; the caller cancelling is not one of these.</summary>
    static bool Unreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool withKey, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? patience = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(await AddressAsync(ct), path));
        request.Headers.Add(ApiHeaders.Client, "windows");
        if (withKey && key.Find() is { } secret) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Content = body switch { null => null, HttpContent content => content, _ => JsonContent.Create(body, body.GetType()) };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(patience ?? Patience);
        try
        {
            var response = await http.SendAsync(request, completion, limit.Token);
            Reached(true);
            return response;
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            if (ex is HttpRequestException || patience is null) Reached(false);
            throw;
        }
    }

    void Reached(bool reached)
    {
        if (IsUnreachable == !reached) return;
        IsUnreachable = !reached;
        ReachabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>bibliotaph.online didn't answer: a web library book can't be downloaded until this PC is online again.</summary>
public sealed class WebLibraryUnreachableException(Exception inner) : IOException("bibliotaph.online couldn't be reached.", inner);

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

/// <summary>
/// An answer to <see cref="WebLibrary.ReadShelfAsync"/>: <see cref="Reached"/> when the server answered, with the
/// <see cref="Shelf"/> when it changed from the version the PC has.
/// </summary>
public sealed record WebShelfRead(bool Reached, WebShelf? Shelf)
{
    public static readonly WebShelfRead Unchanged = new(true, null);
    public static readonly WebShelfRead Unreached = new(false, null);
}

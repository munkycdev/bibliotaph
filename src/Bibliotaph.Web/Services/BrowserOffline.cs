using System.Globalization;
using Bibliotaph.Api.Contracts;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// "Keep favorites and binders offline" in this browser, as the Windows and Mac apps keep them on the device: the
/// whole of each file favorites and binders need stays in the browser's books (bookcache.js), downloaded once, and
/// one no longer needed joins the other books read here. Off unless chosen for this browser, since it may be a
/// phone's. While the app is open a pass runs at the start, every <see cref="Every"/>, and on Download now; each
/// fetches only what's missing.
/// </summary>
public sealed class BrowserOffline(IJSRuntime js, ReaderClient reader) : IAsyncDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    IJSObjectReference? _cache;
    DotNetObjectReference<BrowserOffline>? _self;
    bool _started, _again;

    /// <summary>How the last or current pass went: ready, of how many, waiting for room, failed, bytes, and whether it's running.</summary>
    public (int Ready, int Total, int NoRoom, int Failed, long Bytes, bool Working)? Status { get; private set; }

    /// <summary>Raised when <see cref="Status"/> or the switch changed.</summary>
    public event Action? Changed;

    /// <summary>Starts the passes, once: the shell calls it when the user is signed in.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = RunAsync();
    }

    public async Task<bool> IsOnAsync()
    {
        try
        {
            return await CacheAsync() is { } cache && await cache.InvokeAsync<bool>("preparedOn");
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task SetOnAsync(bool on)
    {
        try
        {
            if (await CacheAsync() is { } cache) await cache.InvokeVoidAsync("setPreparedOn", on);
        }
        catch (JSException)
        {
            // This browser can't keep books.
        }
        Changed?.Invoke();
        await PassAsync();
    }

    /// <summary>Download now: a pass at once.</summary>
    public Task RefreshAsync() => PassAsync();

    async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            do await PassAsync();
            while (await timer.WaitForNextTickAsync(_stopping.Token));
        }
        catch (OperationCanceledException)
        {
            // The page is going.
        }
    }

    async Task PassAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            _again = true;
            return;
        }
        try
        {
            do
            {
                _again = false;
                if (await CacheAsync() is not { } cache) return;
                var on = await cache.InvokeAsync<bool>("preparedOn");
                // Off, only what was kept before is let go, which needs no list from the server.
                var wanted = on ? await reader.OfflineBooksAsync(_stopping.Token) : [];
                _self ??= DotNetObjectReference.Create(this);
                await cache.InvokeVoidAsync("keepPrepared", _stopping.Token, wanted, _self, on);
            }
            while (_again);
        }
        catch (Exception e) when (e is JSException or HttpRequestException or ApiProblemException or TaskCanceledException)
        {
            // Tried again at the next pass.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>bookcache.js, or null where it can't be loaded.</summary>
    async Task<IJSObjectReference?> CacheAsync()
    {
        try
        {
            return _cache ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/bookcache.js");
        }
        catch (JSException)
        {
            return null;
        }
    }

    [JSInvokable]
    public async Task<string?> Link(long documentId)
    {
        try
        {
            return (await reader.ReaderLinkAsync(documentId)).Url;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            return null;
        }
    }

    [JSInvokable]
    public void Prepared(int ready, int total, int noRoom, int failed, double bytes, bool working)
    {
        Status = (ready, total, noRoom, failed, (long)bytes, working);
        Changed?.Invoke();
    }

    /// <summary>What Settings says, in the words the Windows app's Settings › Devices uses.</summary>
    public static string Describe(bool on, (int Ready, int Total, int NoRoom, int Failed, long Bytes, bool Working)? status)
    {
        if (!on) return "Off. Favorites and binders are kept as you read them, like any other book.";
        if (status is not { } s || (s.Working && s.Total == 0)) return "Looking for favorites and binders to keep…";
        if (s.Total == 0) return "Nothing to download: no favorite or binder has a file to keep.";
        var line = s.Working && s.Ready < s.Total
            ? $"Downloading favorites and binders: {s.Ready} of {Plural(s.Total, "book", "books")} in this browser"
            : $"{Plural(s.Ready, "book", "books")} from favorites and binders kept in this browser";
        line += s.Bytes > 0 ? $", {Size(s.Bytes)}." : ".";
        if (!s.Working && s.NoRoom > 0) line += $" {s.NoRoom} {(s.NoRoom == 1 ? "waits" : "wait")} for room the browser won't give.";
        if (!s.Working && s.Failed > 0) line += $" {s.Failed} couldn't be downloaded; Download now tries again.";
        return line;
    }

    static string Plural(int count, string one, string many) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";

    static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _self?.Dispose();
        if (_cache is not null)
        {
            try
            {
                await _cache.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going anyway.
            }
        }
        _stopping.Dispose();
        _gate.Dispose();
    }
}

using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The player, as the Windows app's: a queue (an album, a track, a binder's audio) with repeat and shuffle, and one
/// ambience layer that loops a track of its own at its own volume under the music. It lives with the page, so it plays
/// on as pages change; the bar at the foot of the work area shows it. Each track's bytes come through the reader's
/// cache from the user's storage, never through the server.
/// </summary>
public sealed class Player(ReaderClient api, IJSRuntime js, NavigationManager navigation) : IAsyncDisposable
{
    /// <summary>Previous goes back a track this early in one; later it starts this one again, as players do.</summary>
    const double RestartAfterSeconds = 3;

    readonly PlayOrder _order = new();
    List<PlayerTrack> _queue = [];
    IJSObjectReference? _module;
    DotNetObjectReference<Player>? _self;
    /// <summary>The card each track in the queue is read through: one album's, or a binder's several.</summary>
    List<long> _entries = [];
    int _load;
    int _ambienceLoad;

    public event Action? Changed;

    public IReadOnlyList<PlayerTrack> Queue => _queue;

    /// <summary>The album's title, or null for a track of its own.</summary>
    public string? QueueTitle { get; private set; }

    public PlayerTrack? Current => _order.Current is { } at && at < _queue.Count ? _queue[at] : null;

    public bool IsPlaying { get; private set; }

    /// <summary>Where the track is, and how long it plays, in seconds.</summary>
    public double Position { get; private set; }

    public double Length { get; private set; }

    public RepeatMode Repeat => _order.Repeat;

    public bool Shuffle => _order.Shuffle;

    public bool CanGoNext => _order.HasNext;

    public double Volume { get; private set; } = 80;

    public PlayerTrack? Ambience { get; private set; }

    public bool IsAmbiencePlaying { get; private set; }

    public double AmbienceVolume { get; private set; } = 50;

    /// <summary>Why the last track didn't play, for a moment in the bar.</summary>
    public string? Problem { get; private set; }

    public bool IsQueueOpen { get; set; }

    public bool IsShowing => Current is not null || Ambience is not null;

    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat all",
        RepeatMode.One => "Repeat this track",
        _ => "Repeat off",
    };

    public string ShuffleLabel => Shuffle ? "Shuffle on" : "Shuffle off";

    /// <summary>"Artist · Album" under the title, whichever it has.</summary>
    public string Byline(PlayerTrack track) => string.Join(" · ", new[] { track.Artist, QueueTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Plays a card: an album from <paramref name="documentId"/> (else its first track), or the card's track.</summary>
    public async Task PlayAsync(long entryId, long? documentId = null)
    {
        PlayerTracks tracks;
        try
        {
            tracks = await api.TracksAsync(entryId, documentId);
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            Problem = "That couldn't be played: your library didn't answer.";
            Changed?.Invoke();
            return;
        }
        _queue = [.. tracks.Tracks];
        _entries = [.. tracks.Tracks.Select(_ => entryId)];
        QueueTitle = tracks.Title;
        _order.Start(_queue.Count, tracks.Start);
        await LoadAsync(play: true);
    }

    /// <summary>
    /// A binder's music as one queue, so Next and Previous step through its songs: each item's track, or its album's
    /// tracks, in the binder's order, playing from the item at <paramref name="startAt"/>.
    /// </summary>
    public async Task PlayAsync(IReadOnlyList<(long EntryId, long? DocumentId)> items, string? title, int startAt)
    {
        PlayerTracks[] found;
        try
        {
            found = await Task.WhenAll(items.Select(i => api.TracksAsync(i.EntryId, i.DocumentId)));
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            Problem = "That couldn't be played: your library didn't answer.";
            Changed?.Invoke();
            return;
        }
        var queue = new List<PlayerTrack>();
        var entries = new List<long>();
        long? first = null;
        for (var i = 0; i < found.Length; i++)
        {
            if (i == startAt && found[i].Tracks is { Count: > 0 } starting) first = starting[Math.Clamp(found[i].Start, 0, starting.Count - 1)].DocumentId;
            // A song in the binder twice plays once, where it first comes.
            foreach (var track in found[i].Tracks.Where(t => queue.TrueForAll(q => q.DocumentId != t.DocumentId)))
            {
                queue.Add(track);
                entries.Add(found[i].EntryId);
            }
        }
        if (queue.Count == 0) return;
        _queue = queue;
        _entries = entries;
        QueueTitle = title;
        _order.Start(_queue.Count, Math.Max(0, queue.FindIndex(t => t.DocumentId == first)));
        await LoadAsync(play: true);
    }

    /// <summary>A card's track or album: a file in an album on a card of its own plays from that file, in its album.</summary>
    public Task PlayAsync(BookCard card) =>
        PlayAsync(card.EntryId, card.Id != card.EntryId.ToString(System.Globalization.CultureInfo.InvariantCulture) && card.DocumentId > 0 ? card.DocumentId : null);

    /// <summary>Loops a card's track on the ambience layer, in place of the one there, while the music plays on.</summary>
    public async Task LoopAsAmbienceAsync(long entryId, PlayerTrack track)
    {
        var load = ++_ambienceLoad;
        await CallAsync("close", "ambience");
        Ambience = track;
        IsAmbiencePlaying = false;
        Changed?.Invoke();
        var problem = track.Problem ?? await AskAsync<string?>("open", "ambience", Options(entryId, track));
        if (load != _ambienceLoad || problem == "moved on") return;
        if (problem is not null)
        {
            Ambience = null;
            Problem = problem;
        }
        else
        {
            await CallAsync("volume", "ambience", AmbienceVolume / 100);
            IsAmbiencePlaying = await AskAsync<bool>("play", "ambience");
            Problem = null;
        }
        Changed?.Invoke();
    }

    public async Task PlayPauseAsync()
    {
        if (Current is null) return;
        if (IsPlaying)
        {
            await CallAsync("pause", "music");
            IsPlaying = false;
        }
        else IsPlaying = await AskAsync<bool>("play", "music");
        await ShowNowPlayingAsync();
        Changed?.Invoke();
    }

    public Task NextAsync() => StepAsync(ended: false);

    public async Task PreviousAsync()
    {
        if (Current is null) return;
        if (await AskAsync<double>("where") > RestartAfterSeconds || _order.Previous() is null)
        {
            await CallAsync("restart");
            Position = 0;
            Changed?.Invoke();
            return;
        }
        await LoadAsync(play: true);
    }

    public async Task PlayTrackAsync(int index)
    {
        IsQueueOpen = false;
        _order.Jump(index);
        await LoadAsync(play: true);
    }

    public async Task SeekAsync(double seconds)
    {
        Position = seconds;
        await CallAsync("seek", seconds);
    }

    public void CycleRepeat()
    {
        _order.Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        Changed?.Invoke();
    }

    public void ToggleShuffle()
    {
        _order.SetShuffle(!Shuffle);
        Changed?.Invoke();
    }

    public async Task SetVolumeAsync(double value)
    {
        Volume = Math.Clamp(value, 0, 100);
        await CallAsync("volume", "music", Volume / 100);
    }

    public async Task SetAmbienceVolumeAsync(double value)
    {
        AmbienceVolume = Math.Clamp(value, 0, 100);
        await CallAsync("volume", "ambience", AmbienceVolume / 100);
    }

    /// <summary>Moves the track playing to the ambience layer, and the music goes on to the next track, if there is one.</summary>
    public async Task LoopCurrentAsAmbienceAsync()
    {
        if (Current is not { } track) return;
        var entry = EntryOf(_order.Current);
        if (_queue.Count > 1) await StepAsync(ended: false);
        else await StopMusicAsync();
        await LoopAsAmbienceAsync(entry, track);
    }

    public async Task ToggleAmbienceAsync()
    {
        if (Ambience is null) return;
        if (IsAmbiencePlaying)
        {
            await CallAsync("pause", "ambience");
            IsAmbiencePlaying = false;
        }
        else IsAmbiencePlaying = await AskAsync<bool>("play", "ambience");
        Changed?.Invoke();
    }

    public async Task StopAmbienceAsync()
    {
        ++_ambienceLoad;
        await CallAsync("close", "ambience");
        Ambience = null;
        IsAmbiencePlaying = false;
        Changed?.Invoke();
    }

    /// <summary>Stops the music and the ambience and puts the bar away.</summary>
    public async Task CloseAsync()
    {
        await StopMusicAsync();
        await StopAmbienceAsync();
        Problem = null;
        Changed?.Invoke();
    }

    async Task StepAsync(bool ended)
    {
        if (Current is null) return;
        if (ended && Repeat == RepeatMode.One)
        {
            await CallAsync("restart");
            IsPlaying = await AskAsync<bool>("play", "music");
            return;
        }
        // The end of the queue: it waits at its first track, as the album was before it started.
        await LoadAsync(play: _order.Next(ended) is not null);
    }

    async Task LoadAsync(bool play, int skipped = 0)
    {
        if (Current is not { } track) return;
        var load = ++_load;
        await CallAsync("close", "music");
        Position = 0;
        Length = (track.DurationMs ?? 0) / 1000.0;
        IsPlaying = false;
        IsQueueOpen = IsQueueOpen && play;
        Changed?.Invoke();
        if (!play)
        {
            await ShowNowPlayingAsync();
            return;
        }
        var problem = track.Problem ?? await AskAsync<string?>("open", "music", Options(EntryOf(_order.Current), track));
        if (load != _load || problem == "moved on") return;
        if (problem is not null)
        {
            Problem = problem;
            // One track that won't play shouldn't stop an album; a queue where none will stops.
            if (skipped + 1 < _order.Count && _order.Next(ended: false) is not null) await LoadAsync(play: true, skipped + 1);
            else
            {
                await ShowNowPlayingAsync();
                Changed?.Invoke();
            }
            return;
        }
        Problem = null;
        await CallAsync("volume", "music", Volume / 100);
        IsPlaying = await AskAsync<bool>("play", "music");
        await ShowNowPlayingAsync();
        Changed?.Invoke();
    }

    async Task StopMusicAsync()
    {
        ++_load;
        await CallAsync("close", "music");
        _queue = [];
        _entries = [];
        _order.Clear();
        QueueTitle = null;
        IsPlaying = false;
        Position = 0;
        Length = 0;
        await ShowNowPlayingAsync();
    }

    long EntryOf(int? at) => at is { } i && i < _entries.Count ? _entries[i] : 0;

    static object Options(long entryId, PlayerTrack track) => new
    {
        key = track.ContentHash, size = track.Size, mediaType = track.MediaType, title = track.Title, entryId, documentId = track.DocumentId,
    };

    async Task ShowNowPlayingAsync()
    {
        if (Current is not { } track)
        {
            await CallAsync("nowPlaying", null, false);
            return;
        }
        var cover = track.Cover is { } name ? navigation.ToAbsoluteUri($"api/library/covers/{Uri.EscapeDataString(name)}").ToString() : null;
        await CallAsync("nowPlaying", new { title = track.Title, artist = track.Artist, album = QueueTitle, cover, canGoNext = CanGoNext }, IsPlaying);
    }

    async Task<IJSObjectReference?> ModuleAsync()
    {
        if (_module is not null) return _module;
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/player.js");
            _self = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("init", _self);
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException)
        {
            _module = null;
        }
        return _module;
    }

    /// <summary>The sound, told to do something; nothing when the page is going.</summary>
    async Task CallAsync(string name, params object?[] args)
    {
        if (await ModuleAsync() is not { } module) return;
        try
        {
            await module.InvokeVoidAsync(name, args);
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException)
        {
            // The page went as the call was made; there's nothing left to tell.
        }
    }

    async Task<T?> AskAsync<T>(string name, params object?[] args)
    {
        if (await ModuleAsync() is not { } module) return default;
        try
        {
            return await module.InvokeAsync<T>(name, args);
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException)
        {
            return default;
        }
    }

    [JSInvokable]
    public Task Ended(string layer) => layer == "music" ? StepAsync(ended: true) : Task.CompletedTask;

    [JSInvokable]
    public async Task Failed(string layer, string message)
    {
        Problem = message;
        if (layer == "ambience")
        {
            await StopAmbienceAsync();
            return;
        }
        if (_order.HasNext) await StepAsync(ended: false);
        else
        {
            IsPlaying = false;
            Changed?.Invoke();
        }
    }

    [JSInvokable]
    public void Tick(double position, double duration)
    {
        Position = position;
        if (duration > 0) Length = duration;
        Changed?.Invoke();
    }

    [JSInvokable]
    public Task Key(string key) => key switch
    {
        "play" when !IsPlaying => PlayPauseAsync(),
        "pause" when IsPlaying => PlayPauseAsync(),
        "next" => NextAsync(),
        "previous" => PreviousAsync(),
        "stop" => CloseAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>A track's link now, for the cache to read its bytes; asked for again whenever the last one has expired.</summary>
    [JSInvokable]
    public async Task<string?> Link(long documentId)
    {
        try
        {
            return (await api.ReaderLinkAsync(documentId)).Url;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { } module)
        {
            await CallAsync("close", "music");
            await CallAsync("close", "ambience");
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going.
            }
        }
        _self?.Dispose();
    }
}

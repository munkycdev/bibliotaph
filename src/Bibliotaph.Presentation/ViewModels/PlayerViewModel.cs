using System.Collections.ObjectModel;
using Bibliotaph.Audio;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.ViewModels;

/// <summary>Whether the queue starts again when it ends, or the track does.</summary>
public enum RepeatMode { Off, All, One }

/// <summary>A track in the player's queue, or the one on the ambience layer.</summary>
public sealed partial class PlayerTrackViewModel(AudioTrack track, string? album, string? albumCover) : ObservableObject
{
    public AudioTrack Track { get; } = track;

    public long DocumentId => Track.DocumentId;

    public string Title => Track.Title;

    public string? Artist => Track.Artist;

    public string? Album { get; } = album;

    /// <summary>"Artist · Album", whichever it has, under the title.</summary>
    public string Byline => string.Join(" · ", new[] { Artist, Album }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Its own cover, else its album's.</summary>
    public string? Cover { get; } = track.Cover ?? albumCover;

    /// <summary>"4:32", or empty when its length isn't known.</summary>
    public string LengthText => Track.Duration is { } length ? CardText.Length(length) : "";

    /// <summary>The track playing, or paused, in the queue.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

/// <summary>
/// The player, one for the app: a queue of tracks (an album, a track, a binder's audio) with repeat and shuffle, and
/// under it one ambience layer that loops a track of its own at its own volume, so rain or a tavern's murmur goes on
/// while the music changes. It keeps playing while books are opened and read, in any window; the bar at the bottom of
/// the main window shows it, and the system's media keys drive the music. Each layer holds its file (a copy, for one
/// in a ZIP or the web library) while it plays and lets it go when it moves on.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>Previous goes back a track this early in one; later it starts this one again, as players do.</summary>
    static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(3);

    readonly IAudioChannel _music;
    readonly IAudioChannel _ambience;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly SourceFiles _sources;
    readonly ReadingService _reading;
    readonly AppPaths _paths;
    readonly IUiThread _ui;
    readonly ILogger<PlayerViewModel> _log;
    readonly UiTimer _clock;
    readonly Random _random = new();

    // The queue's play order, as indexes into Queue: in order, or shuffled with the track playing first.
    List<int> _order = [];
    int _at = -1;
    LocalFile? _musicFile;
    LocalFile? _ambienceFile;
    // Moved on by every load, so a file still opening when another was asked for is let go rather than played.
    int _musicLoad;
    int _ambienceLoad;
    // True while the clock moves Position on, so that isn't taken for the user dragging the slider.
    bool _ticking;

    public PlayerViewModel(IAudioOutput output, LibraryStore library, LibraryQueries queries, SourceFiles sources, ReadingService reading, AppPaths paths,
        IUiThread ui, TimeProvider time, ILogger<PlayerViewModel> log)
    {
        _music = output.CreateChannel(systemControls: true);
        _ambience = output.CreateChannel(systemControls: false);
        _library = library;
        _queries = queries;
        _sources = sources;
        _reading = reading;
        _paths = paths;
        _ui = ui;
        _log = log;
        _clock = new UiTimer(ui, time, TimeSpan.FromMilliseconds(250), repeat: true, Tick);
        _music.Volume = Volume / 100;
        _ambience.Volume = AmbienceVolume / 100;
        _ambience.Loops = true;
        _music.Ended += (_, _) => _ui.Post(() => OnEndedAsync(_musicLoad));
        _music.Failed += (_, message) => _ui.Post(() => OnFailedAsync(_musicLoad, message));
        _ambience.Failed += (_, message) => _ui.Post(() => OnAmbienceFailed(_ambienceLoad, message));
        _music.KeyPressed += (_, key) => _ui.Post(() => OnKeyAsync(key));
    }

    /// <summary>The queue, in album order; shuffle changes the order it plays in, not this.</summary>
    public ObservableCollection<PlayerTrackViewModel> Queue { get; } = [];

    /// <summary>What the queue is: an album's title, a binder's, or null for a single track.</summary>
    [ObservableProperty]
    public partial string? QueueTitle { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing), nameof(HasMusic), nameof(CanGoBack), nameof(CanGoNext))]
    public partial PlayerTrackViewModel? Current { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    public partial bool IsPlaying { get; private set; }

    /// <summary>Where the track is, in seconds. Setting it seeks, as the bar's slider does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double Position { get; set; }

    /// <summary>How long the track plays, in seconds; 0 until it is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    public partial double Length { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatLabel), nameof(IsRepeating), nameof(RepeatsOne), nameof(CanGoBack), nameof(CanGoNext))]
    public partial RepeatMode Repeat { get; set; }

    public bool IsRepeating => Repeat != RepeatMode.Off;

    public bool RepeatsOne => Repeat == RepeatMode.One;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    public partial bool Shuffle { get; set; }

    /// <summary>The music's volume, from 0 to 100.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 80;

    /// <summary>Why the last track didn't play, for a moment in the bar; null when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    /// <summary>The track looping under the music, if one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing), nameof(HasAmbience))]
    public partial PlayerTrackViewModel? Ambience { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmbiencePlayPauseLabel))]
    public partial bool IsAmbiencePlaying { get; private set; }

    /// <summary>The ambience layer's volume, from 0 to 100, apart from the music's.</summary>
    [ObservableProperty]
    public partial double AmbienceVolume { get; set; } = 50;

    /// <summary>The queue's list, opened from the bar to pick a track.</summary>
    [ObservableProperty]
    public partial bool IsQueueOpen { get; set; }

    /// <summary>The bar shows while there is music or ambience to play.</summary>
    public bool IsShowing => Current is not null || Ambience is not null;

    public bool HasMusic => Current is not null;

    public bool HasAmbience => Ambience is not null;

    public bool HasProblem => Problem is not null;

    public bool CanGoBack => Current is not null;

    public bool CanGoNext => Current is not null && (_at < _order.Count - 1 || Repeat == RepeatMode.All);

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public string AmbiencePlayPauseLabel => IsAmbiencePlaying ? "Pause ambience" : "Play ambience";

    public string PositionText => CardText.Length(TimeSpan.FromSeconds(Position));

    public string LengthText => Length > 0 ? CardText.Length(TimeSpan.FromSeconds(Length)) : "";

    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat all",
        RepeatMode.One => "Repeat this track",
        _ => "Repeat off",
    };

    public string ShuffleLabel => Shuffle ? "Shuffle on" : "Shuffle off";

    /// <summary>Whether a request is for audio, which the player takes rather than the reader.</summary>
    public static bool Plays(ViewerRequest request) => request.IsAudio;

    /// <summary>
    /// Plays what a card, a binder or the reader asked to open: an album's tracks from the one asked for, or one track.
    /// The files of a pack that aren't audio are left to the reader.
    /// </summary>
    public async Task PlayAsync(ViewerRequest request)
    {
        if (request.Pack is { } pack && pack.Any(p => p.IsAudio))
        {
            var tracks = pack.Where(p => p.IsAudio).Select(p => p.DocumentId).ToList();
            await PlayAsync(tracks, request.PackTitle, Math.Max(0, tracks.IndexOf(request.DocumentId)));
        }
        else await PlayAsync([request.DocumentId], request.PackTitle, 0);
    }

    /// <summary>Puts these tracks in the queue, in this order, and plays from <paramref name="startAt"/>.</summary>
    public async Task PlayAsync(IReadOnlyList<long> documentIds, string? title, int startAt)
    {
        var found = await Task.Run(() => _queries.GetTracksAsync(documentIds));
        var tracks = documentIds.Where(found.ContainsKey).Select(id => found[id]).ToList();
        if (tracks.Count == 0) return;
        var start = Math.Clamp(startAt < documentIds.Count ? tracks.FindIndex(t => t.DocumentId == documentIds[startAt]) : 0, 0, tracks.Count - 1);
        var albumCover = tracks.FirstOrDefault(t => t.Cover is not null)?.Cover;
        Queue.Clear();
        foreach (var track in tracks) Queue.Add(new PlayerTrackViewModel(track, title, albumCover));
        QueueTitle = title;
        _order = OrderFrom(start);
        _at = 0;
        await LoadAsync(play: true);
        RecordAsync(tracks[start].DocumentId).FireAndForget();
    }

    /// <summary>Loops a track on the ambience layer, in place of the one there, while the music plays on.</summary>
    public async Task LoopAsAmbienceAsync(long documentId, string? album = null)
    {
        var found = await Task.Run(() => _queries.GetTracksAsync([documentId]));
        if (!found.TryGetValue(documentId, out var track)) return;
        var load = ++_ambienceLoad;
        Release(ref _ambienceFile, _ambience);
        Ambience = new PlayerTrackViewModel(track, album, null);
        var file = await OpenFileAsync(track, _ambience);
        if (load != _ambienceLoad)
        {
            file.File?.Dispose();
            return;
        }
        if (file.File is null)
        {
            Ambience = null;
            Problem = file.Problem;
            return;
        }
        _ambienceFile = file.File;
        _ambience.Play();
        IsAmbiencePlaying = true;
        Problem = null;
    }

    [RelayCommand]
    async Task PlayPause()
    {
        if (Current is null) return;
        if (IsPlaying)
        {
            _music.Pause();
            IsPlaying = false;
            _clock.Stop();
        }
        else if (_musicFile is null) await LoadAsync(play: true);
        else
        {
            _music.Play();
            IsPlaying = true;
            _clock.Start();
        }
        ShowNowPlaying();
    }

    [RelayCommand]
    Task Next() => StepAsync(forward: true, ended: false);

    [RelayCommand]
    Task Previous()
    {
        if (Current is null) return Task.CompletedTask;
        if (_music.Position > RestartAfter || (_at == 0 && Repeat != RepeatMode.All))
        {
            Seek(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        return StepAsync(forward: false, ended: false);
    }

    /// <summary>Plays a track from the queue's list.</summary>
    [RelayCommand]
    async Task PlayTrack(PlayerTrackViewModel track)
    {
        var index = Queue.IndexOf(track);
        if (index < 0) return;
        IsQueueOpen = false;
        _at = _order.IndexOf(index);
        await LoadAsync(play: true);
    }

    /// <summary>Repeat off, then all, then this track, then off again.</summary>
    [RelayCommand]
    void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    /// <summary>
    /// Moves the track playing to the ambience layer, to loop under whatever plays next, and the music goes on to the
    /// next track if there is one.
    /// </summary>
    [RelayCommand]
    async Task LoopCurrentAsAmbience()
    {
        if (Current is not { } track) return;
        var album = Current.Album;
        var next = _at < _order.Count - 1;
        if (next) await StepAsync(forward: true, ended: false);
        else StopMusic();
        await LoopAsAmbienceAsync(track.DocumentId, album);
    }

    [RelayCommand]
    void ToggleAmbience()
    {
        if (Ambience is null) return;
        if (IsAmbiencePlaying) _ambience.Pause();
        else _ambience.Play();
        IsAmbiencePlaying = !IsAmbiencePlaying;
    }

    [RelayCommand]
    void StopAmbience()
    {
        ++_ambienceLoad;
        Release(ref _ambienceFile, _ambience);
        Ambience = null;
        IsAmbiencePlaying = false;
    }

    /// <summary>Stops the music and the ambience and puts the bar away.</summary>
    [RelayCommand]
    void Close()
    {
        StopMusic();
        StopAmbience();
        Problem = null;
    }

    partial void OnPositionChanged(double value)
    {
        if (!_ticking && Current is not null) _music.Position = TimeSpan.FromSeconds(Math.Max(0, value));
    }

    partial void OnVolumeChanged(double value) => _music.Volume = Math.Clamp(value, 0, 100) / 100;

    partial void OnAmbienceVolumeChanged(double value) => _ambience.Volume = Math.Clamp(value, 0, 100) / 100;

    partial void OnRepeatChanged(RepeatMode value) => ShowNowPlaying();

    partial void OnShuffleChanged(bool value)
    {
        if (Queue.Count == 0) return;
        var playing = _at >= 0 && _at < _order.Count ? _order[_at] : 0;
        _order = OrderFrom(playing);
        _at = 0;
        OnPropertyChanged(nameof(CanGoNext));
    }

    /// <summary>The play order starting at <paramref name="start"/>: the queue from there on, or with shuffle on the rest in any order after it.</summary>
    List<int> OrderFrom(int start)
    {
        if (!Shuffle) return [.. Enumerable.Range(0, Queue.Count).Skip(start).Concat(Enumerable.Range(0, start))];
        var rest = Enumerable.Range(0, Queue.Count).Where(i => i != start).ToArray();
        _random.Shuffle(rest);
        return [start, .. rest];
    }

    async Task StepAsync(bool forward, bool ended)
    {
        if (Current is null) return;
        if (ended && Repeat == RepeatMode.One)
        {
            Seek(TimeSpan.Zero);
            _music.Play();
            return;
        }
        var at = _at + (forward ? 1 : -1);
        if (at >= _order.Count || at < 0)
        {
            if (Repeat != RepeatMode.All)
            {
                // The end of the queue: it waits at its first track, as the album was before it started.
                _at = 0;
                await LoadAsync(play: false);
                return;
            }
            if (Shuffle && forward) _order = OrderFrom(_order[0]);
            at = forward ? 0 : _order.Count - 1;
        }
        _at = at;
        await LoadAsync(play: true);
    }

    /// <summary>Opens the track at <see cref="_at"/> in the play order, and plays it unless <paramref name="play"/> is false.</summary>
    async Task LoadAsync(bool play, int skipped = 0)
    {
        if (_at < 0 || _at >= _order.Count) return;
        var load = ++_musicLoad;
        var track = Queue[_order[_at]];
        foreach (var queued in Queue) queued.IsCurrent = ReferenceEquals(queued, track);
        Current = track;
        Release(ref _musicFile, _music);
        _clock.Stop();
        SetPosition(0);
        Length = track.Track.Duration?.TotalSeconds ?? 0;
        IsPlaying = play;
        OnPropertyChanged(nameof(CanGoNext));
        if (!play)
        {
            ShowNowPlaying();
            return;
        }
        var opened = await OpenFileAsync(track.Track, _music);
        if (load != _musicLoad)
        {
            opened.File?.Dispose();
            return;
        }
        if (opened.File is null)
        {
            Problem = opened.Problem;
            // One track that won't play shouldn't stop an album; a queue where none will stops.
            if (skipped + 1 < _order.Count && _at < _order.Count - 1)
            {
                _at++;
                await LoadAsync(play: true, skipped + 1);
            }
            else
            {
                IsPlaying = false;
                ShowNowPlaying();
            }
            return;
        }
        _musicFile = opened.File;
        if (_music.Duration is { } duration && duration > TimeSpan.Zero) Length = duration.TotalSeconds;
        Problem = null;
        _music.Play();
        _clock.Start();
        ShowNowPlaying();
    }

    /// <summary>Holds the track's file and opens it on its layer's channel: the file, or why it can't play.</summary>
    async Task<(LocalFile? File, string? Problem)> OpenFileAsync(AudioTrack track, IAudioChannel channel)
    {
        LocalFile? file = null;
        try
        {
            if (await Task.Run(() => _library.GetSourceAsync(track.DocumentId)) is not { } source)
                return (null, $"{track.Title} can't be reached: its folder may be offline.");
            file = await Task.Run(() => _sources.OpenToReadAsync(source));
            var type = AudioFile.MediaType(source.Format) ?? "audio/mpeg";
            if (await channel.OpenAsync(file.Path, type) is { } problem)
            {
                await file.DisposeAsync();
                return (null, problem);
            }
            return (file, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Opening audio document {DocumentId} to play failed", track.DocumentId);
            if (file is not null) await file.DisposeAsync();
            return (null, ex is WebLibraryUnreachableException
                ? $"{track.Title} is only in your web library, which can't be reached right now."
                : $"{track.Title} couldn't be played. Its file may be in use, or still downloading.");
        }
    }

    void StopMusic()
    {
        ++_musicLoad;
        _clock.Stop();
        Release(ref _musicFile, _music);
        foreach (var queued in Queue) queued.IsCurrent = false;
        Queue.Clear();
        _order = [];
        _at = -1;
        Current = null;
        QueueTitle = null;
        IsPlaying = false;
        SetPosition(0);
        Length = 0;
        _music.ShowNowPlaying(null, false);
    }

    static void Release(ref LocalFile? file, IAudioChannel channel)
    {
        channel.Close();
        file?.Dispose();
        file = null;
    }

    void Seek(TimeSpan to)
    {
        _music.Position = to;
        SetPosition(to.TotalSeconds);
    }

    void SetPosition(double seconds)
    {
        _ticking = true;
        try
        {
            Position = seconds;
        }
        finally
        {
            _ticking = false;
        }
    }

    void Tick()
    {
        if (!IsPlaying) return;
        SetPosition(_music.Position.TotalSeconds);
        if (Length <= 0 && _music.Duration is { } duration && duration > TimeSpan.Zero) Length = duration.TotalSeconds;
    }

    Task OnEndedAsync(int load) => load == _musicLoad ? StepAsync(forward: true, ended: true) : Task.CompletedTask;

    async Task OnFailedAsync(int load, string message)
    {
        if (load != _musicLoad) return;
        Problem = message;
        if (_at < _order.Count - 1) await StepAsync(forward: true, ended: false);
        else
        {
            _clock.Stop();
            IsPlaying = false;
            ShowNowPlaying();
        }
    }

    void OnAmbienceFailed(int load, string message)
    {
        if (load != _ambienceLoad) return;
        Problem = message;
        StopAmbience();
    }

    Task OnKeyAsync(MediaKey key) => key switch
    {
        MediaKey.PlayPause => PlayPause(),
        MediaKey.Play when !IsPlaying => PlayPause(),
        MediaKey.Pause when IsPlaying => PlayPause(),
        MediaKey.Next => Next(),
        MediaKey.Previous => Previous(),
        MediaKey.Stop => StopAsync(),
        _ => Task.CompletedTask,
    };

    Task StopAsync()
    {
        StopMusic();
        return Task.CompletedTask;
    }

    void ShowNowPlaying()
    {
        if (Current is not { } track)
        {
            _music.ShowNowPlaying(null, false);
            return;
        }
        var cover = track.Cover is { } name ? Path.Combine(_paths.Covers, name) : null;
        _music.ShowNowPlaying(new NowPlaying(track.Title, track.Artist, track.Album, cover, CanGoBack, CanGoNext), IsPlaying);
    }

    async Task RecordAsync(long documentId)
    {
        try
        {
            await Task.Run(() => _reading.RecordOpenAsync(documentId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Recording the play of document {DocumentId} failed", documentId);
        }
    }

    public void Dispose()
    {
        _clock.Stop();
        Release(ref _musicFile, _music);
        Release(ref _ambienceFile, _ambience);
        _music.Dispose();
        _ambience.Dispose();
    }
}

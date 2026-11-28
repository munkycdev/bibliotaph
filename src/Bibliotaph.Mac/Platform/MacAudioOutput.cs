using AVFoundation;
using Bibliotaph.Presentation.Services;
using CoreMedia;
using MediaPlayer;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Sound through AVFoundation, which plays what macOS can: MP3, AAC, FLAC and WAV (Ogg Vorbis and Opus files it
/// can't, and says so). The music channel drives the Mac's Now Playing (Control Centre, the media keys, AirPods), so
/// they show the track and reach the player's own commands; the ambience channel loops gaplessly through an
/// AVPlayerLooper and stays out of them.
/// </summary>
public sealed class MacAudioOutput(ILogger<MacAudioOutput> log) : IAudioOutput
{
    public IAudioChannel CreateChannel(bool systemControls) => new Channel(log, systemControls);

    sealed class Channel : IAudioChannel
    {
        readonly ILogger _log;
        readonly bool _controls;
        readonly AVQueuePlayer _player = new();
        readonly List<NSObject> _observers = [];
        readonly List<(MPRemoteCommand Command, NSObject Target)> _commands = [];
        AVPlayerItem? _item;
        AVPlayerLooper? _looper;
        IDisposable? _status;
        bool _loops;
        string _format = "";
        double _volume = 1;

        public Channel(ILogger log, bool controls)
        {
            _log = log;
            _controls = controls;
            _player.ActionAtItemEnd = AVPlayerActionAtItemEnd.Pause;
            if (!controls) return;
            var center = MPRemoteCommandCenter.Shared;
            Hook(center.PlayCommand, MediaKey.Play);
            Hook(center.PauseCommand, MediaKey.Pause);
            Hook(center.TogglePlayPauseCommand, MediaKey.PlayPause);
            Hook(center.NextTrackCommand, MediaKey.Next);
            Hook(center.PreviousTrackCommand, MediaKey.Previous);
            Hook(center.StopCommand, MediaKey.Stop);
            var seek = center.ChangePlaybackPositionCommand.AddTarget(e =>
            {
                if (e is MPChangePlaybackPositionCommandEvent position) Position = TimeSpan.FromSeconds(position.PositionTime);
                return MPRemoteCommandHandlerStatus.Success;
            });
            _commands.Add((center.ChangePlaybackPositionCommand, seek));
        }

        void Hook(MPRemoteCommand command, MediaKey key)
        {
            var target = command.AddTarget(_ =>
            {
                KeyPressed?.Invoke(this, key);
                return MPRemoteCommandHandlerStatus.Success;
            });
            _commands.Add((command, target));
        }

        public async Task<string?> OpenAsync(string path, string mediaType, CancellationToken ct = default)
        {
            Close();
            _format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            var item = new AVPlayerItem(NSUrl.FromFilename(path));
            _item = item;
            var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _status = item.AddObserver("status", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ =>
            {
                if (item.Status == AVPlayerItemStatus.ReadyToPlay) ready.TrySetResult(null);
                else if (item.Status == AVPlayerItemStatus.Failed)
                {
                    _log.LogWarning("Opening a {Format} file to play failed: {Error}", _format, item.Error?.LocalizedDescription);
                    ready.TrySetResult(_format is "OGG" or "OPUS" ? $"macOS can't play {_format} files." : $"macOS can't play this {_format} file.");
                }
            });
            _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, OnEnded, item));
            _observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.ItemFailedToPlayToEndTimeNotification, OnStopped, item));
            if (_loops) _looper = AVPlayerLooper.FromPlayer(_player, item);
            else _player.InsertItem(item, null);
            _player.Volume = (float)_volume;
            using var registration = ct.Register(() => ready.TrySetCanceled(ct));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var waited = timeout.Token.Register(() => ready.TrySetResult("macOS took too long to open the file."));
            var problem = await ready.Task;
            if (problem is not null && ReferenceEquals(_item, item)) Close();
            return problem;
        }

        void OnEnded(NSNotification notification)
        {
            if (!_loops) Ended?.Invoke(this, EventArgs.Empty);
        }

        void OnStopped(NSNotification notification)
        {
            _log.LogWarning("Playing a {Format} file stopped part way", _format);
            Failed?.Invoke(this, "macOS stopped playing the file. It may be damaged.");
        }

        public void Play() => _player.Play();

        public void Pause() => _player.Pause();

        public void Close()
        {
            _player.Pause();
            _looper?.DisableLooping();
            _looper?.Dispose();
            _looper = null;
            _player.RemoveAllItems();
            _status?.Dispose();
            _status = null;
            foreach (var observer in _observers) NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
            _observers.Clear();
            _item?.Dispose();
            _item = null;
        }

        public TimeSpan Position
        {
            get => _player.CurrentTime is var time && time.IsInvalid ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, time.Seconds));
            set => _player.Seek(CMTime.FromSeconds(value.TotalSeconds, 600), CMTime.Zero, CMTime.Zero);
        }

        public TimeSpan? Duration => _item?.Duration is { IsInvalid: false, IsIndefinite: false } duration && duration.Seconds > 0
            ? TimeSpan.FromSeconds(duration.Seconds)
            : null;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 1);
                _player.Volume = (float)_volume;
            }
        }

        /// <summary>Set before a file opens: a looping file is played by a looper, which queues copies of it end to end.</summary>
        public bool Loops
        {
            get => _loops;
            set => _loops = value;
        }

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;

        public event EventHandler<MediaKey>? KeyPressed;

        public void ShowNowPlaying(NowPlaying? now, bool playing)
        {
            if (!_controls) return;
            var center = MPNowPlayingInfoCenter.DefaultCenter;
            if (now is null)
            {
                center.NowPlaying = null!;
                center.PlaybackState = MPNowPlayingPlaybackState.Stopped;
                return;
            }
            var remote = MPRemoteCommandCenter.Shared;
            remote.NextTrackCommand.Enabled = now.CanGoNext;
            remote.PreviousTrackCommand.Enabled = now.CanGoBack;
            var info = new MPNowPlayingInfo
            {
                Title = now.Title,
                Artist = now.Artist,
                AlbumTitle = now.Album,
                ElapsedPlaybackTime = Position.TotalSeconds,
                PlaybackRate = playing ? 1 : 0,
                MediaType = MPNowPlayingInfoMediaType.Audio,
            };
            if (Duration is { } duration) info.PlaybackDuration = duration.TotalSeconds;
            if (now.CoverPath is { } cover && File.Exists(cover) && new NSImage(cover) is { } image)
                info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
            center.NowPlaying = info;
            center.PlaybackState = playing ? MPNowPlayingPlaybackState.Playing : MPNowPlayingPlaybackState.Paused;
        }

        public void Dispose()
        {
            Close();
            foreach (var (command, target) in _commands) command.RemoveTarget(target);
            _commands.Clear();
            _player.Dispose();
        }
    }
}

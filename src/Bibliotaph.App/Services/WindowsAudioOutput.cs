using System.IO;
using Bibliotaph.Processing;
using Bibliotaph.Presentation.Services;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Bibliotaph.App.Services;

/// <summary>
/// Sound through Windows' own media player (Windows.Media.Playback), which decodes what Windows can: MP3, AAC, FLAC
/// and WAV out of the box, Ogg Vorbis and Opus with Microsoft's Web Media Extensions. The music channel drives the
/// system media transport controls by hand, so the media keys, the volume flyout and the lock screen show the track
/// and reach the player's own commands; the ambience channel stays out of them.
/// </summary>
public sealed class WindowsAudioOutput(ISourceFileReader files, ILogger<WindowsAudioOutput> log) : IAudioOutput
{
    public IAudioChannel CreateChannel(bool systemControls) => new Channel(files, log, systemControls);

    sealed class Channel : IAudioChannel
    {
        readonly ISourceFileReader _files;
        readonly ILogger _log;
        readonly bool _controls;
        MediaPlayer? _player;
        Stream? _stream;
        TaskCompletionSource<string?>? _opening;
        string _format = "";
        double _volume = 1;
        bool _loops;

        public Channel(ISourceFileReader files, ILogger log, bool controls)
        {
            _files = files;
            _log = log;
            _controls = controls;
        }

        // Made with the first file, not with the window: a Windows without its media features (an N edition, a server)
        // still starts, and only playing says it can't.
        MediaPlayer Player()
        {
            if (_player is not null) return _player;
            var player = new MediaPlayer { AutoPlay = false, AudioCategory = MediaPlayerAudioCategory.Media, Volume = _volume, IsLoopingEnabled = _loops };
            // Off, so a press of a media key comes here as a command rather than playing or pausing behind the
            // player's back; the music channel then shows its track in the controls itself.
            player.CommandManager.IsEnabled = false;
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = false;
            if (_controls) smtc.ButtonPressed += OnButtonPressed;
            player.MediaOpened += (_, _) => _opening?.TrySetResult(null);
            player.MediaFailed += OnFailed;
            player.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
            return _player = player;
        }

        public async Task<string?> OpenAsync(string path, string mediaType, CancellationToken ct = default)
        {
            Close();
            _format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            var opening = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _opening = opening;
            MediaPlayer player;
            try
            {
                player = Player();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or NotSupportedException)
            {
                _log.LogWarning(ex, "Windows' media player couldn't be made");
                return "Windows can't play audio on this PC: its media features aren't installed.";
            }
            try
            {
                // The file is read through a stream of its own, shared so another app can still open it: Windows'
                // player reads it as it plays, and the player lets it go on Close.
                _stream = _files.OpenRead(path);
                player.Source = MediaSource.CreateFromStream(_stream.AsRandomAccessStream(), mediaType);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                _log.LogWarning(ex, "Opening a {Format} file to play failed", _format);
                Close();
                return "The file couldn't be read. It may be in use, or still downloading.";
            }
            using var registration = ct.Register(() => opening.TrySetCanceled(ct));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var waited = timeout.Token.Register(() => opening.TrySetResult("Windows took too long to open the file."));
            var problem = await opening.Task;
            // Another file asked for meanwhile has the player now; this one was let go when it did.
            if (problem is not null && ReferenceEquals(_opening, opening)) Close();
            return problem;
        }

        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e)
        {
            _log.LogWarning("Playing a {Format} file failed: {Error} {Message}", _format, e.Error, e.ErrorMessage);
            var message = e.Error switch
            {
                MediaPlayerError.SourceNotSupported when _format is "OGG" or "OPUS" =>
                    $"Windows needs Microsoft's free Web Media Extensions, from the Microsoft Store, to play {_format} files.",
                MediaPlayerError.SourceNotSupported => $"Windows can't play this {_format} file.",
                MediaPlayerError.DecodingError => "The file is damaged, so Windows stopped playing it.",
                _ => "Windows stopped playing the file.",
            };
            // A file that never opened says why to OpenAsync; one that stops part way says so to the player.
            if (_opening is { Task.IsCompleted: false } opening) opening.TrySetResult(message);
            else Failed?.Invoke(this, message);
        }

        public void Play() => _player?.Play();

        public void Pause() => _player?.Pause();

        public void Close()
        {
            _opening?.TrySetResult("Closed.");
            _opening = null;
            if (_player is { } player)
            {
                player.Pause();
                var source = player.Source as IDisposable;
                player.Source = null;
                source?.Dispose();
            }
            _stream?.Dispose();
            _stream = null;
        }

        public TimeSpan Position
        {
            get => _player?.PlaybackSession.Position ?? TimeSpan.Zero;
            set
            {
                if (_player is { PlaybackSession.CanSeek: true } player) player.PlaybackSession.Position = value;
            }
        }

        public TimeSpan? Duration => _player?.PlaybackSession.NaturalDuration is { } duration && duration > TimeSpan.Zero ? duration : null;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 1);
                _player?.Volume = _volume;
            }
        }

        public bool Loops
        {
            get => _loops;
            set
            {
                _loops = value;
                _player?.IsLoopingEnabled = value;
            }
        }

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;

        public event EventHandler<MediaKey>? KeyPressed;

        void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
        {
            MediaKey? key = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => MediaKey.Play,
                SystemMediaTransportControlsButton.Pause => MediaKey.Pause,
                SystemMediaTransportControlsButton.Next => MediaKey.Next,
                SystemMediaTransportControlsButton.Previous => MediaKey.Previous,
                SystemMediaTransportControlsButton.Stop => MediaKey.Stop,
                _ => null,
            };
            if (key is { } pressed) KeyPressed?.Invoke(this, pressed);
        }

        public void ShowNowPlaying(NowPlaying? now, bool playing)
        {
            if (!_controls || _player is null) return;
            var smtc = _player.SystemMediaTransportControls;
            var display = smtc.DisplayUpdater;
            if (now is null)
            {
                display.ClearAll();
                display.Update();
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                smtc.IsEnabled = false;
                return;
            }
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = true;
            smtc.IsPreviousEnabled = now.CanGoBack;
            smtc.IsNextEnabled = now.CanGoNext;
            smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = now.Title;
            display.MusicProperties.Artist = now.Artist ?? "";
            display.MusicProperties.AlbumTitle = now.Album ?? "";
            display.Thumbnail = Thumbnail(now.CoverPath);
            display.Update();
        }

        // The cover is the app's own file in its covers folder; read into memory, so the controls hold no file open.
        RandomAccessStreamReference? Thumbnail(string? path)
        {
            if (path is null) return null;
            try
            {
                using var file = _files.OpenRead(path);
                var memory = new MemoryStream();
                file.CopyTo(memory);
                memory.Position = 0;
                return RandomAccessStreamReference.CreateFromStream(memory.AsRandomAccessStream());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "Reading a cover for the media controls failed");
                return null;
            }
        }

        public void Dispose()
        {
            Close();
            if (_player is null) return;
            if (_controls) _player.SystemMediaTransportControls.ButtonPressed -= OnButtonPressed;
            _player.Dispose();
        }
    }
}

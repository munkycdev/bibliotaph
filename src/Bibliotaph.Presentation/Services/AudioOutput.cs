namespace Bibliotaph.Presentation.Services;

/// <summary>
/// The platform's sound, as the player asks for it: a channel for each layer that plays at once. The music channel is
/// the one the system's media controls follow (media keys, the Windows media flyout, the Mac's Now Playing); the
/// ambience channel stays out of them, so a key press never pauses the rain under a fight.
/// </summary>
public interface IAudioOutput
{
    /// <param name="systemControls">The channel the system's media controls show and drive; the app makes one.</param>
    IAudioChannel CreateChannel(bool systemControls);
}

/// <summary>A press of a media key, or of a button in the system's media controls.</summary>
public enum MediaKey { PlayPause, Play, Pause, Next, Previous, Stop }

/// <summary>What the system's media controls show for the track playing: its cover is a file in the covers folder.</summary>
public sealed record NowPlaying(string Title, string? Artist, string? Album, string? CoverPath, bool CanGoBack, bool CanGoNext);

/// <summary>
/// One layer of sound, playing a file at a time. Its events come on any thread; the player takes them to the UI thread.
/// Closing it lets go of the file, which the player then releases.
/// </summary>
public interface IAudioChannel : IDisposable
{
    /// <summary>
    /// Loads the file, ready to play from the start: null once it can, else why it can't, for the player bar. Its media
    /// type, such as audio/mpeg, is for players that go by it rather than the file's name.
    /// </summary>
    Task<string?> OpenAsync(string path, string mediaType, CancellationToken ct = default);

    void Play();

    void Pause();

    /// <summary>Stops and lets go of the file.</summary>
    void Close();

    TimeSpan Position { get; set; }

    /// <summary>How long the file plays, once it is open; null when the platform doesn't know.</summary>
    TimeSpan? Duration { get; }

    /// <summary>From 0, silent, to 1, as loud as the file is.</summary>
    double Volume { get; set; }

    /// <summary>Starts again from the beginning when it ends, as near seamlessly as the platform can.</summary>
    bool Loops { get; set; }

    /// <summary>The file played to its end, and doesn't loop.</summary>
    event EventHandler? Ended;

    /// <summary>The file stopped part way, with why.</summary>
    event EventHandler<string>? Failed;

    /// <summary>A media key, for the channel made with system controls.</summary>
    event EventHandler<MediaKey>? KeyPressed;

    /// <summary>Shows the track in the system's media controls, or with null takes it away; ignored on other channels.</summary>
    void ShowNowPlaying(NowPlaying? now, bool playing);
}

/// <summary>No sound: for tests and anywhere else that has no speakers. Every file opens, and plays for as long as its tags say.</summary>
public sealed class SilentAudioOutput : IAudioOutput
{
    public IAudioChannel CreateChannel(bool systemControls) => new SilentChannel();

    /// <summary>A channel that plays nothing; a test moves it on with <see cref="End"/>.</summary>
    public sealed class SilentChannel : IAudioChannel
    {
        public string? Path { get; private set; }

        public bool Playing { get; private set; }

        public NowPlaying? Shown { get; private set; }

        public Task<string?> OpenAsync(string path, string mediaType, CancellationToken ct = default)
        {
            Path = path;
            Position = TimeSpan.Zero;
            return Task.FromResult<string?>(null);
        }

        public void Play() => Playing = true;

        public void Pause() => Playing = false;

        public void Close()
        {
            Playing = false;
            Path = null;
        }

        public TimeSpan Position { get; set; }

        public TimeSpan? Duration => null;

        public double Volume { get; set; } = 1;

        public bool Loops { get; set; }

        public event EventHandler? Ended;

        public event EventHandler<string>? Failed;

        public event EventHandler<MediaKey>? KeyPressed;

        public void ShowNowPlaying(NowPlaying? now, bool playing) => Shown = now;

        /// <summary>The file plays to its end.</summary>
        public void End() => Ended?.Invoke(this, EventArgs.Empty);

        public void Fail(string message) => Failed?.Invoke(this, message);

        public void Press(MediaKey key) => KeyPressed?.Invoke(this, key);

        public void Dispose() => Close();
    }
}

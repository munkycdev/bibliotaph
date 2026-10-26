using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>Library sync as Settings shows it: the folder, when another PC's work was last merged, and what went wrong, if anything.</summary>
public sealed record SyncStatus(string? Folder, string? LastMergedFrom, DateTime? LastMergedUtc, DateTime? LastWrittenUtc, string? Problem);

/// <summary>What one sync pass did: the PCs whose files it read, and whether it changed this catalog.</summary>
public sealed record SyncPass(IReadOnlyList<string> MergedFrom, bool Changed, bool Wrote);

/// <summary>
/// Library sync: the same user work on a second Windows PC, with no sync service. Each PC writes its own sync file,
/// <c>&lt;folder&gt;\Bibliotaph sync\&lt;PC name&gt;.json</c>, to a folder the user picks (one in OneDrive, say) a
/// few seconds after the user's work changes and when Bibliotaph closes; at the start, when another PC's file
/// changes, and every ten minutes, it reads the others' files and merges them, newest change per field winning
/// (<see cref="SyncMerge"/>). The databases never go in the folder, and the folder may be offline: then the pass
/// says so and is tried again later. Only the user's work travels; file locations, online-only marks, window places
/// and caches stay on their PC. A PC connected to the web library also swaps files through it, and the web library
/// runs this too, as one more device (<see cref="ISyncChannel"/>).
/// </summary>
public sealed partial class LibrarySync : IHostedService, IDisposable
{
    /// <summary>How long after the last write to the user's work this PC's file is written.</summary>
    public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(5);

    /// <summary>The longest a stream of writes (a long AI run, say) can hold the file back.</summary>
    public static readonly TimeSpan MaxWriteDelay = TimeSpan.FromMinutes(1);

    /// <summary>How long after another PC's file changes it is merged: the cloud app writes it in pieces.</summary>
    public static readonly TimeSpan ReadDelay = TimeSpan.FromSeconds(3);

    /// <summary>A merge this often anyway, for a folder that was offline or a change the watcher missed.</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);

    /// <summary>How often a channel is asked whether another device's file changed.</summary>
    public static readonly TimeSpan ChannelPoll = TimeSpan.FromMinutes(1);

    readonly LibrarySyncStore _store;
    readonly SettingsStore _settings;
    readonly AppPaths _paths;
    readonly CatalogWrites? _writes;
    readonly MetadataProjector? _projector;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly IReadOnlyList<ISyncChannel> _channels;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lock _lock = new();
    ITimer? _writeTimer;
    ITimer? _readTimer;
    ITimer? _recheckTimer;
    ITimer? _pollTimer;
    FileSystemWatcher? _watcher;
    DateTime? _firstUnwritten;
    SyncStatus _status = new(null, null, null, null, null);
    bool _started;

    public LibrarySync(LibrarySyncStore store, SettingsStore settings, AppPaths paths, CatalogDatabase? catalog = null, MetadataProjector? projector = null,
        TimeProvider? clock = null, ILogger<LibrarySync>? log = null, string? machineName = null, IEnumerable<ISyncChannel>? channels = null)
    {
        _store = store;
        _settings = settings;
        _paths = paths;
        _writes = catalog?.Writes;
        _projector = projector;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<LibrarySync>.Instance;
        MachineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName.Trim();
        _channels = [.. channels ?? []];
    }

    /// <summary>Whether any channel besides the folder is in use, so there is something to sync with even without a folder.</summary>
    bool ChannelsOn => _channels.Any(c => c.IsOn);

    /// <summary>This PC's name, which its sync file is called by.</summary>
    public string MachineName { get; }

    public SyncStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Raised after the status changed, on a background thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised after a merge changed this catalog (collections, binders, pins or cards), on a background thread, once it is projected.</summary>
    public event EventHandler? Merged;

    void SetStatus(Func<SyncStatus, SyncStatus> change)
    {
        lock (_lock) _status = change(_status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    sealed record LastMerge(string Machine, DateTime Utc);

    /// <summary>The folder the user picked, or null while sync is off.</summary>
    public async Task<string?> GetFolderAsync(CancellationToken ct = default) =>
        await _settings.GetAsync(SettingKeys.SyncFolder, ct) is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// Why <paramref name="folder"/> can't hold the sync files, or null when it can. Bibliotaph's own data folder, or a
    /// folder inside it, is refused: the databases must never be in a folder a cloud app syncs.
    /// </summary>
    public static string? CheckFolder(string folder, AppPaths paths)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That isn't a folder Bibliotaph can use.";
        }
        if (!Directory.Exists(full)) return "That folder can't be opened.";
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        if (string.Equals(full, data, StringComparison.OrdinalIgnoreCase) || full.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "That's Bibliotaph's own data folder, which holds its databases. They must never be in a synced folder: pick another, such as a folder in OneDrive.";
        return null;
    }

    /// <summary>
    /// Starts syncing with <paramref name="folder"/>, or stops when it is null. Returns why the folder was refused, or
    /// null; a folder taken is merged with straight away.
    /// </summary>
    public async Task<string?> SetFolderAsync(string? folder, CancellationToken ct = default)
    {
        if (folder is not null && CheckFolder(folder, _paths) is { } problem) return problem;
        var full = folder is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        await _settings.SetAsync(SettingKeys.SyncFolder, full ?? "", ct);
        SetStatus(s => s with { Folder = full, Problem = null, LastWrittenUtc = null });
        Watch(full);
        if (full is not null) await SyncNowAsync(ct);
        return null;
    }

    /// <summary>A merge now: what Sync now in Settings does, and what the start, a changed file and the recheck run.</summary>
    public Task<SyncPass> SyncNowAsync(CancellationToken ct = default) => RunAsync(merge: true, ct);

    /// <summary>Writes this PC's file with what changed here, without reading the others': at close.</summary>
    public Task<SyncPass> WriteNowAsync(CancellationToken ct = default) => RunAsync(merge: false, ct);

    async Task<SyncPass> RunAsync(bool merge, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            lock (_lock) _firstUnwritten = null;
            return await PassAsync(merge, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the status, starts watching the folder and the catalog, and merges once Bibliotaph has started.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var folder = await GetFolderAsync(cancellationToken);
        LastMerge? last = null;
        try
        {
            if (await _settings.GetAsync(SettingKeys.SyncLastMerge, cancellationToken) is { Length: > 0 } json)
                last = JsonSerializer.Deserialize<LastMerge>(json, SyncJson.Options);
        }
        catch (JsonException)
        {
            // An unreadable status only means none is shown until the next merge.
        }
        SetStatus(s => s with { Folder = folder, LastMergedFrom = last?.Machine, LastMergedUtc = last?.Utc });
        lock (_lock)
        {
            _started = true;
            _writeTimer = _clock.CreateTimer(_ => Fire(merge: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _readTimer = _clock.CreateTimer(_ => Fire(merge: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _recheckTimer = _clock.CreateTimer(_ => Fire(merge: true), null, ReadDelay, Recheck);
            if (_channels.Count > 0) _pollTimer = _clock.CreateTimer(_ => Poll(), null, ChannelPoll, ChannelPoll);
        }
        foreach (var channel in _channels) channel.Changed += OnChannelChanged;
        _writes?.Written += OnCatalogWritten;
        Watch(folder);
    }

    /// <summary>Stops watching, and writes this PC's file one last time so the other PC sees everything done here.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _writes?.Written -= OnCatalogWritten;
        foreach (var channel in _channels) channel.Changed -= OnChannelChanged;
        lock (_lock)
        {
            _started = false;
            _writeTimer?.Dispose();
            _readTimer?.Dispose();
            _recheckTimer?.Dispose();
            _pollTimer?.Dispose();
            _watcher?.Dispose();
            _writeTimer = _readTimer = _recheckTimer = _pollTimer = null;
            _watcher = null;
        }
        try
        {
            if (await GetFolderAsync(cancellationToken) is not null || ChannelsOn) await WriteNowAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Writing the library sync file at close failed");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writeTimer?.Dispose();
            _readTimer?.Dispose();
            _recheckTimer?.Dispose();
            _pollTimer?.Dispose();
            _watcher?.Dispose();
        }
        _gate.Dispose();
    }

    /// <summary>The user's work changed: the file is written once the writes stop for a moment, or a minute after the first.</summary>
    void OnCatalogWritten(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_started || (_status.Folder is null && !ChannelsOn)) return;
            var now = _clock.GetUtcNow().UtcDateTime;
            _firstUnwritten ??= now;
            var wait = now - _firstUnwritten.Value >= MaxWriteDelay ? TimeSpan.Zero : WriteDelay;
            _writeTimer?.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire(bool merge) => _ = Task.Run(async () =>
    {
        try
        {
            if (await GetFolderAsync() is not null || ChannelsOn) await RunAsync(merge, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Library sync failed");
            SetStatus(s => s with { Problem = "Syncing stopped with an error; it's tried again in a few minutes. The details are in the log." });
        }
    });

    /// <summary>Another device's file changed in a channel that says so at once: merged shortly, as a changed file in the folder is.</summary>
    void OnChannelChanged(object? sender, EventArgs e)
    {
        lock (_lock) _readTimer?.Change(ReadDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Asks each channel in use whether another device's file changed, and merges when one did.</summary>
    void Poll() => _ = Task.Run(async () =>
    {
        try
        {
            foreach (var channel in _channels.Where(c => c.IsOn))
                if (await channel.HasChangesAsync(CancellationToken.None))
                {
                    OnChannelChanged(this, EventArgs.Empty);
                    return;
                }
        }
        catch (Exception ex)
        {
            // Asked again in a minute; the recheck merges anyway.
            _log.LogInformation(ex, "Asking a sync channel for changes failed");
        }
    });

    /// <summary>Watches the folder for other PCs' files changing. A folder that can't be watched (offline) is merged on the recheck instead.</summary>
    void Watch(string? folder)
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
            if (!_started || folder is null) return;
            try
            {
                var files = Path.Combine(folder, SyncFile.FolderName);
                Directory.CreateDirectory(files);
                var own = SyncFile.FileName(MachineName);
                var watcher = new FileSystemWatcher(files, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                void Changed(object? sender, FileSystemEventArgs e)
                {
                    if (string.Equals(e.Name, own, StringComparison.OrdinalIgnoreCase)) return;
                    lock (_lock) _readTimer?.Change(ReadDelay, Timeout.InfiniteTimeSpan);
                }
                watcher.Created += Changed;
                watcher.Changed += Changed;
                watcher.Renamed += (sender, e) => Changed(sender, e);
                watcher.Error += (_, _) => { lock (_lock) _readTimer?.Change(ReadDelay, Timeout.InfiniteTimeSpan); };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _log.LogInformation(ex, "Not watching the library sync folder {Folder}", folder);
            }
        }
    }

    /// <summary>"Last merged from DESKTOP-2 at 09:14", or what is happening instead, for Settings.</summary>
    public static string Describe(SyncStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (status.Folder is null) return "Not syncing.";
        if (status.Problem is { } problem) return problem;
        if (LastMerged(status, now, zone) is { } merged) return merged;
        return status.LastWrittenUtc is null ? "Waiting to write this PC's sync file."
            : "No other PC has written to this folder yet. Pick the same folder in Bibliotaph on your other PC.";
    }

    /// <summary>The same for Settings > Web library, on a PC connected to the web library.</summary>
    public static string DescribeWeb(SyncStatus status, DateTimeOffset now, TimeZoneInfo zone) =>
        status.Problem ?? LastMerged(status, now, zone) ?? "Your work is merged with your web library a few seconds after it changes.";

    static string? LastMerged(SyncStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (status.LastMergedFrom is not { } from || status.LastMergedUtc is not { } at) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var when = local.Date == today ? local.ToString("'at' HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("'on' d MMMM 'at' HH:mm", CultureInfo.CurrentCulture);
        return $"Last merged from {from} {when}.";
    }
}

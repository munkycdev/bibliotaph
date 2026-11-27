using System.Text.Json;
using Bibliotaph.Catalog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// How keeping favorites and binders offline is going: whether it's on, how many of their files only storage or the web
/// library has (<see cref="Books"/>), how many of those are here, waiting for disk space, or couldn't be had, their size
/// here, and whether downloads are running.
/// </summary>
public sealed record OfflineKeepStatus(bool On, int Books, int Ready, int NoRoom, int Failed, long Bytes, bool Working)
{
    public static readonly OfflineKeepStatus Starting = new(true, 0, 0, 0, 0, 0, Working: true);
}

/// <summary>
/// Keeps on this device the files that favorites and binders need (<see cref="OfflineBooks"/>), so they open without a
/// connection. A book only the web library or storage connected here has is downloaded to <see cref="WebLibraryCopies"/>
/// and held there; a cloud file in a library folder (OneDrive's Files On-Demand) is kept on the device as "Always keep
/// on this device" does (<see cref="ICloudFiles.KeepOnDevice"/>), remembering which it did so as to give back only
/// those. A file with a copy on a disk here needs nothing.
/// <para>
/// Each file is downloaded once. A pass runs at the start, whenever a favorite or binder changes here or through sync,
/// and every <see cref="Every"/> for a book whose content changed; it fetches only what's missing, one book at a time.
/// One that couldn't be had waits for <see cref="Refresh"/> (Download now) or the next start, rather than being
/// tried on every pass; one that would leave less than the free-space reserve waits for room. A file no longer
/// needed is given back: a copy joins the other downloads, going once their room is needed, and a cloud file goes
/// back to its sync app's own choice. Off, every one is given back.
/// </para>
/// </summary>
public sealed class OfflineKeeper(OfflineBooks books, LibraryStore library, WebLibraryCopies copies, ICloudFiles cloud, SettingsStore settings,
    IDiskSpace disk, FavoritesService favorites, SessionsService sessions, TimeProvider? clock = null, ILogger<OfflineKeeper>? log = null,
    LibrarySync? sync = null, WebLibrary? web = null, IndexingOptions? options = null) : IHostedService, IDisposable
{
    /// <summary>"False" when favorites and binders aren't kept offline on this device; on otherwise.</summary>
    public const string OnKey = "offline.favorites-binders";

    /// <summary>The cloud files this device keeps for favorites and binders, as paths: only these are given back.</summary>
    public const string PinnedKey = SettingKeys.OfflinePinned;

    /// <summary>How long after the start the first pass runs: after the start's own work.</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(20);

    /// <summary>How long a change waits for the ones after it, as when a binder is filled one book at a time.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>How often a pass runs with nothing to start it, for a book whose file changed in storage.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<OfflineKeeper>.Instance;
    readonly long _reserveBytes = (options ?? new IndexingOptions()).OnlineOnlyReserveBytes;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    readonly Lock _lock = new();
    // Files that couldn't be had, by content hash or path, so a pass doesn't try them again until asked to.
    readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    ITimer? _timer;
    bool _retry;
    OfflineKeepStatus _status = OfflineKeepStatus.Starting;
    /// <summary>Why each held book is kept, by content hash; replaced, never changed, so it's read without a lock.</summary>
    IReadOnlyDictionary<string, string> _why = new Dictionary<string, string>();

    public OfflineKeepStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Raised after <see cref="Status"/> changed, on a background thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Why the book with this content is kept offline, to follow "because it's" ("a favorite"), or null when it isn't
    /// held for a favorite or binder, or no pass has run yet.
    /// </summary>
    public string? WhyKept(string contentHash) => Volatile.Read(ref _why).GetValueOrDefault(contentHash);

    public async Task<bool> IsOnAsync(CancellationToken ct = default) => await settings.GetAsync(OnKey, ct) != bool.FalseString;

    /// <summary><see cref="Status"/>, saying whether it's on before the first pass has run.</summary>
    public async Task<OfflineKeepStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = Status;
        return ReferenceEquals(status, OfflineKeepStatus.Starting) && !await IsOnAsync(ct) ? status with { On = false, Working = false } : status;
    }

    public async Task SetOnAsync(bool on, CancellationToken ct = default)
    {
        await settings.SetAsync(OnKey, on.ToString(), ct);
        lock (_lock) _retry = true;
        Nudge(TimeSpan.Zero);
    }

    /// <summary>Download now: a pass at once that tries again the files that couldn't be had.</summary>
    public void Refresh()
    {
        lock (_lock) _retry = true;
        Nudge(TimeSpan.Zero);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        favorites.Changed += OnChanged;
        sessions.Changed += OnChanged;
        if (sync is not null) sync.Merged += OnChanged;
        if (web is not null) web.ReachabilityChanged += OnReachabilityChanged;
        lock (_lock) _timer = _clock.CreateTimer(_ => Fire(), null, First, Every);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        favorites.Changed -= OnChanged;
        sessions.Changed -= OnChanged;
        if (sync is not null) sync.Merged -= OnChanged;
        if (web is not null) web.ReachabilityChanged -= OnReachabilityChanged;
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        await _stopping.CancelAsync();
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    void OnChanged(object? sender, EventArgs e) => Nudge(Settle);

    // Back in reach, the books that couldn't be had then may be had now.
    void OnReachabilityChanged(object? sender, EventArgs e)
    {
        if (web is { IsUnreachable: true }) return;
        lock (_lock) _retry = true;
        Nudge(Settle);
    }

    void Nudge(TimeSpan after)
    {
        lock (_lock) _timer?.Change(after, Every);
    }

    void Fire() => _ = Task.Run(async () =>
    {
        try
        {
            await KeepNowAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // Tried again at the next pass.
            _log.LogWarning(ex, "Keeping favorites and binders offline failed");
        }
    });

    /// <summary>One pass: what favorites and binders need is held and downloaded, and what they no longer need given back.</summary>
    public async Task KeepNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            bool retry;
            lock (_lock) (retry, _retry) = (_retry, false);
            if (retry) lock (_lock) _failed.Clear();
            await PassAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task PassAsync(CancellationToken ct)
    {
        var pinned = await PinnedAsync(ct);
        if (!await IsOnAsync(ct))
        {
            Volatile.Write(ref _why, new Dictionary<string, string>());
            await copies.HoldForPreparationAsync([], ct);
            foreach (var path in pinned.ToList()) LetGo(path, pinned);
            await SavePinnedAsync(pinned, ct);
            Publish(new OfflineKeepStatus(false, 0, 0, 0, 0, 0, false));
            return;
        }

        var toCopy = new List<DocumentSource>();
        var toPin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var why = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in await books.ListAsync(ct))
        {
            // Every place it was is missing, or in a folder that can't be reached: nothing to keep.
            if (await library.GetSourceAsync(book.DocumentId, ct) is not { } source) continue;
            if (copies.Holds(source, toRead: true))
            {
                if (why.TryAdd(source.ContentHash, book.Why)) toCopy.Add(source);
                continue;
            }
            var path = source.ArchivePath ?? source.FullPath;
            if (pinned.Contains(path) || cloud.CanKeepOnDevice(path)) toPin.TryAdd(path, source.ContentHash);
        }
        // Held before downloading, so a download's eviction can't take another book that's needed.
        await copies.HoldForPreparationAsync([.. toCopy.Select(s => s.ContentHash)], ct);
        Volatile.Write(ref _why, why);
        foreach (var path in pinned.Where(p => !toPin.ContainsKey(p)).ToList()) LetGo(path, pinned);
        await SavePinnedAsync(pinned, ct);

        var total = toCopy.Count + toPin.Count;
        var (ready, noRoom, failed) = (0, 0, 0);
        void Show(bool working) => Publish(new OfflineKeepStatus(true, total, ready, noRoom, failed, 0, working));
        Show(working: true);
        foreach (var source in toCopy)
        {
            ct.ThrowIfCancellationRequested();
            switch (await CopyAsync(source, ct))
            {
                case Outcome.Ready: ready++; break;
                case Outcome.NoRoom: noRoom++; break;
                default: failed++; break;
            }
            Show(working: true);
        }
        foreach (var (path, _) in toPin)
        {
            ct.ThrowIfCancellationRequested();
            switch (await PinAsync(path, pinned, ct))
            {
                case Outcome.Ready: ready++; break;
                case Outcome.NoRoom: noRoom++; break;
                case Outcome.Failed: failed++; break;
                default: total--; break;
            }
            Show(working: true);
        }

        var bytes = (await copies.SummarizeAsync(ct)).KeptForPreparationBytes + pinned.Sum(SizeOf);
        Publish(new OfflineKeepStatus(true, total, ready, noRoom, failed, bytes, false));
        if (toCopy.Count + toPin.Count > 0)
            _log.LogInformation("Favorites and binders kept offline: {Ready} of {Total}, {NoRoom} waiting for room, {Failed} failed", ready, total, noRoom, failed);
    }

    enum Outcome
    {
        Ready,
        NoRoom,
        Failed,
        // A cloud file the user since chose to let go of themselves: theirs to decide.
        LeftToUser,
    }

    async Task<Outcome> CopyAsync(DocumentSource source, CancellationToken ct)
    {
        if (copies.Has(source.ContentHash)) return Outcome.Ready;
        lock (_lock)
            if (_failed.Contains(source.ContentHash)) return Outcome.Failed;
        try
        {
            await copies.FetchAsync(source, ct);
            return Outcome.Ready;
        }
        catch (NoRoomForCopyException)
        {
            return Outcome.NoRoom;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "A favorite's or binder's book couldn't be downloaded to keep offline");
            lock (_lock) _failed.Add(source.ContentHash);
            return Outcome.Failed;
        }
    }

    async Task<Outcome> PinAsync(string path, HashSet<string> pinned, CancellationToken ct)
    {
        if (pinned.Contains(path)) return cloud.StateOf(path) == CloudFileState.Pinned ? Outcome.Ready : Outcome.LeftToUser;
        lock (_lock)
            if (_failed.Contains(path)) return Outcome.Failed;
        if (cloud.StateOf(path) == CloudFileState.OnlineOnly && disk.FreeBytes(path) is { } free && free - SizeOf(path) < _reserveBytes) return Outcome.NoRoom;
        try
        {
            // The download blocks until it's done, so it waits on a thread of its own.
            await Task.Run(() => cloud.KeepOnDevice(path), ct);
            pinned.Add(path);
            await SavePinnedAsync(pinned, ct);
            return Outcome.Ready;
        }
        catch (IOException ex)
        {
            _log.LogInformation(ex, "A favorite's or binder's cloud file couldn't be kept on this device");
            lock (_lock) _failed.Add(path);
            // Pinned but not yet downloaded, it's the sync app's to download in its own time.
            if (cloud.StateOf(path) == CloudFileState.Pinned)
            {
                pinned.Add(path);
                await SavePinnedAsync(pinned, ct);
            }
            return Outcome.Failed;
        }
    }

    void LetGo(string path, HashSet<string> pinned)
    {
        try
        {
            // One the user let go of themselves since, or that has gone, is left as it is.
            if (cloud.StateOf(path) == CloudFileState.Pinned) cloud.LetGoOnDevice(path);
        }
        catch (IOException ex)
        {
            _log.LogInformation(ex, "A cloud file kept for a favorite or binder couldn't be given back");
            return;
        }
        pinned.Remove(path);
    }

    static long SizeOf(string path)
    {
        try
        {
            // Reading the size never downloads a cloud file.
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    void Publish(OfflineKeepStatus status)
    {
        lock (_lock)
        {
            if (_status == status) return;
            _status = status;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task<HashSet<string>> PinnedAsync(CancellationToken ct)
    {
        try
        {
            if (await settings.GetAsync(PinnedKey, ct) is { Length: > 0 } json && JsonSerializer.Deserialize<string[]>(json) is { } paths)
                return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The list of cloud files kept for favorites and binders couldn't be read");
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    Task SavePinnedAsync(HashSet<string> pinned, CancellationToken ct) =>
        settings.SetAsync(PinnedKey, JsonSerializer.Serialize(pinned.Order(StringComparer.OrdinalIgnoreCase).ToArray()), ct);
}

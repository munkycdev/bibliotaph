using System.Collections.Concurrent;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Ai;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// One user's library: the same catalog.db, index.db, stores and indexing the Windows app runs, in that user's folder,
/// with its own services so nothing is shared between users. Only <see cref="UserLibraries"/> opens and closes it;
/// endpoints borrow it through a <see cref="LibraryLease"/>.
/// </summary>
public sealed class UserLibrary
{
    internal UserLibrary(string userId, AppPaths paths, ServiceProvider services)
    {
        UserId = userId;
        Paths = paths;
        Services = services;
    }

    public string UserId { get; }
    public AppPaths Paths { get; }
    internal ServiceProvider Services { get; }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public CatalogDatabase Catalog => Get<CatalogDatabase>();
    public IndexDatabase Index => Get<IndexDatabase>();
    public IndexWriter Writer => Get<IndexWriter>();
    public IndexingService Indexing => Get<IndexingService>();
    public UserDrives Drives => Get<UserDrives>();

    /// <summary>For the Catalog stores, which take a context factory as the Windows app's DI gives them.</summary>
    public IDbContextFactory<CatalogDbContext> Contexts => Get<IDbContextFactory<CatalogDbContext>>();

    /// <summary>
    /// Whether indexing has work in hand: a scan, files still to read, scanned pages, or books for the AI to catalogue
    /// while it's answering. A busy library stays open after its last request, so indexing finishes even when nobody is
    /// looking.
    /// </summary>
    public async Task<bool> IsBusyAsync(CancellationToken ct = default)
    {
        var indexing = Indexing;
        if (indexing.IsScanning || indexing.Hashing is not null) return true;
        if (indexing.IsPaused(Lane.Index)) return false;
        var counts = await Get<LibraryStore>().GetCountsAsync(ct);
        // Files that couldn't be read are tried again after the next scan, not now.
        if (counts.Unhashed > indexing.Unreadable.Count) return true;
        // Scanned pages are read, and books catalogued and scanned whole, to the end even with nobody signed in.
        var progress = await Get<IndexQueries>().GetProgressAsync(ct);
        if (!indexing.IsPaused(Lane.Ocr) && progress.PagesAwaitingOcr > 0) return true;
        var ai = Get<AiSettings>();
        if (ai.Setup.IsReady && !indexing.IsPaused(Lane.Classify) && indexing.Unavailable(Lane.Classify) is null
            && (progress.ToClassify > 0 || ai.ScanWholeBooks && progress.ToScanWhole > 0)) return true;
        // Reading the library for meaning, too, carries on to the end; one whose model stopped answering is left until next time.
        return Get<LibraryVectors>().Status is { State: LibraryVectorsState.Reading or LibraryVectorsState.Waiting } vectors && vectors.WithVectors < vectors.Pages;
    }
}

/// <summary>A borrowed library. Disposing it returns the library, which may then be closed once it has been idle.</summary>
public sealed class LibraryLease : IAsyncDisposable
{
    readonly Action _release;
    int _released;

    internal LibraryLease(UserLibrary library, Action release)
    {
        Library = library;
        _release = release;
    }

    public UserLibrary Library { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Opens each user's library on its first use, restoring its files from the replica if this disk doesn't have them,
/// and closes it after <see cref="ServerOptions.LibraryIdle"/> without use. SQLite wants one writing process per file,
/// so this one server process is the only writer of every library; anything else (the indexer job) goes through the API.
/// </summary>
public sealed class UserLibraries(ServerPaths paths, IDatabaseRestore restore, LibraryHost host) : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, Slot> _slots = new();
    readonly ILogger _log = host.Logs.CreateLogger<UserLibraries>();
    readonly TimeProvider clock = host.Clock;

    /// <summary>Libraries open now, for tests and diagnostics.</summary>
    public int OpenCount => _slots.Count;

    public async Task<LibraryLease> OpenAsync(string userId, CancellationToken ct = default)
    {
        UserIds.Require(userId);
        while (true)
        {
            var slot = _slots.GetOrAdd(userId, id => new Slot(new Lazy<Task<UserLibrary>>(() => OpenLibraryAsync(id))));
            Task? closing;
            lock (slot)
            {
                closing = slot.Closed;
                if (closing is null) slot.Leases++;
            }
            if (closing is not null)
            {
                // Closing just now: wait until its files are free, then open it afresh.
                await closing.WaitAsync(ct);
                continue;
            }

            try
            {
                var library = await slot.Opening.Value.WaitAsync(ct);
                return new LibraryLease(library, () => Release(slot));
            }
            catch
            {
                Release(slot);
                // A library that failed to open isn't kept, so the next request tries again.
                if (slot.Opening.Value.IsFaulted) _slots.TryRemove(new KeyValuePair<string, Slot>(userId, slot));
                throw;
            }
        }
    }

    /// <summary>
    /// The user connected or disconnected a drive: an open library picks up the change and looks at its folders again.
    /// A closed one reads the connections when it next opens.
    /// </summary>
    public async Task DrivesChangedAsync(string userId)
    {
        if (!_slots.TryGetValue(userId, out var slot) || !slot.Opening.IsValueCreated || !slot.Opening.Value.IsCompletedSuccessfully) return;
        var library = await slot.Opening.Value;
        await library.Drives.ReloadAsync();
        library.Indexing.RequestScan();
    }

    /// <summary>
    /// Something changed in one of a user's drives. An open library scans the folders on that drive; a closed one is
    /// opened, which scans every folder, and stays open while it reads what's new.
    /// </summary>
    public async Task DriveChangedAsync(string userId, string driveId, CancellationToken ct = default)
    {
        var wasOpen = _slots.ContainsKey(userId);
        await using var lease = await OpenAsync(userId, ct);
        if (wasOpen) lease.Library.Get<DriveChangeFeed>().Notify(driveId);
    }

    void Release(Slot slot)
    {
        lock (slot)
        {
            slot.Leases--;
            slot.LastUsed = clock.GetUtcNow();
        }
    }

    /// <summary>Closes every library nobody holds and nobody has used for <paramref name="idle"/>. Returns how many.</summary>
    public async Task<int> CloseIdleAsync(TimeSpan idle)
    {
        var now = clock.GetUtcNow();
        var closed = 0;
        foreach (var (userId, slot) in _slots)
        {
            if (!IsIdle(slot, now, idle)) continue;
            try
            {
                if (await (await slot.Opening.Value).IsBusyAsync()) continue;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Couldn't tell whether the library of {User} is busy; closing it", userId);
            }
            if (!TryBeginClose(slot, s => IsIdle(s, now, idle), out var done)) continue;
            await CloseAsync(userId, slot, done);
            closed++;
        }
        return closed;
    }

    static bool IsIdle(Slot slot, DateTimeOffset now, TimeSpan idle)
    {
        if (!slot.Opening.IsValueCreated || !slot.Opening.Value.IsCompletedSuccessfully) return false;
        lock (slot) return slot.Closed is null && slot.Leases == 0 && now - slot.LastUsed >= idle;
    }

    static bool TryBeginClose(Slot slot, Func<Slot, bool> when, out TaskCompletionSource done)
    {
        done = null!;
        if (!slot.Opening.IsValueCreated || !slot.Opening.Value.IsCompletedSuccessfully) return false;
        lock (slot)
        {
            if (slot.Closed is not null || !when(slot)) return false;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            slot.Closed = done.Task;
            return true;
        }
    }

    async Task CloseAsync(string userId, Slot slot, TaskCompletionSource done)
    {
        try
        {
            var library = await slot.Opening.Value;
            // Indexing first, so it can still record the job it was on; then the writer, as the Windows app stops them.
            await library.Get<LibraryVectors>().StopAsync(CancellationToken.None);
            await library.Indexing.StopAsync(CancellationToken.None);
            await library.Writer.StopAsync(CancellationToken.None);
            var index = library.Index;
            var catalog = library.Catalog;
            await library.Services.DisposeAsync();
            // Pooled connections keep the files open; a library opened again later must not share them.
            index.ClearPools();
            catalog.ClearPool();
            DeleteFetched(library.Paths);
            _log.LogInformation("Closed the library of {User}", userId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Closing the library of {User} failed", userId);
        }
        finally
        {
            _slots.TryRemove(new KeyValuePair<string, Slot>(userId, slot));
            done.TrySetResult();
        }
    }

    async Task<UserLibrary> OpenLibraryAsync(string userId)
    {
        var root = paths.UserRoot(userId);
        Directory.CreateDirectory(root);
        var appPaths = new AppPaths(root);
        // Copies of books left by a server that stopped mid-read.
        DeleteFetched(appPaths);
        await restore.RestoreAsync($"users/{userId}/catalog.db", appPaths.CatalogDatabase, CancellationToken.None);
        await restore.RestoreAsync($"users/{userId}/index.db", appPaths.IndexDatabase, CancellationToken.None);

        var services = LibraryServices.Build(userId, appPaths, paths.UserBackups(userId), host);
        try
        {
            var library = new UserLibrary(userId, appPaths, services);
            var migrated = await library.Catalog.MigrateAsync();
            if (migrated.AppliedMigrations.Count > 0)
                _log.LogInformation("Applied {Count} catalog migration(s) for {User}", migrated.AppliedMigrations.Count, userId);
            await library.Get<VocabularyStore>().SeedAsync();

            var opened = await library.Index.InitializeAsync();
            if (opened != IndexOpenResult.Opened) _log.LogInformation("index.db for {User}: {Result}", userId, opened);
            await library.Writer.StartAsync(CancellationToken.None);
            await library.Get<CoverMirror>().StartAsync(CancellationToken.None);

            // Before the Classify lane looks at whether it may send anything.
            await library.Get<ServerApiKeys>().LoadAsync();
            await library.Get<AiSettings>().LoadAsync();
            // A rebuilt index.db gets its chapters back from catalog.db, without the AI reading a book again.
            await library.Get<ChapterProjection>().RestoreAsync();

            await library.Drives.ReloadAsync();
            // Scans every library folder as it starts, which picks up whatever changed while the library was closed.
            await library.Indexing.StartAsync(CancellationToken.None);
            await library.Get<LibraryVectors>().StartAsync(CancellationToken.None);
            return library;
        }
        catch
        {
            await services.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A closed account's library: closed if it's open, even in use, and its folder and backups deleted. Its account
    /// is gone by now, so nothing opens it again.
    /// </summary>
    public async Task DeleteAsync(string userId)
    {
        UserIds.Require(userId);
        while (_slots.TryGetValue(userId, out var slot))
        {
            Task? closing;
            lock (slot) closing = slot.Closed;
            if (closing is not null)
            {
                await closing;
                continue;
            }
            try
            {
                await slot.Opening.Value;
            }
            catch
            {
                // It never opened, so there is nothing to close; the failed slot is let go on its own.
                _slots.TryRemove(new KeyValuePair<string, Slot>(userId, slot));
                continue;
            }
            if (TryBeginClose(slot, _ => true, out var done)) await CloseAsync(userId, slot, done);
        }
#pragma warning disable RS0030 // The server's own copies of a closed account's library; books are never local files here.
        foreach (var folder in new[] { paths.UserRoot(userId), paths.UserBackups(userId) })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
#pragma warning restore RS0030
    }

    /// <summary>The books fetched from drives to be read: none outlives the library being open.</summary>
    static void DeleteFetched(AppPaths paths)
    {
#pragma warning disable RS0030 // Bibliotaph's own copies, never a user's file.
        try
        {
            if (Directory.Exists(paths.Extract)) Directory.Delete(paths.Extract, recursive: true);
        }
        catch (IOException)
        {
            // A file still open; the next open or close tries again.
        }
#pragma warning restore RS0030
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (userId, slot) in _slots)
            if (TryBeginClose(slot, _ => true, out var done)) await CloseAsync(userId, slot, done);
    }

    sealed class Slot(Lazy<Task<UserLibrary>> opening)
    {
        public Lazy<Task<UserLibrary>> Opening { get; } = opening;
        public int Leases;
        public DateTimeOffset LastUsed = DateTimeOffset.MinValue;

        /// <summary>Set once closing starts; completes when the files are free.</summary>
        public Task? Closed;
    }
}

/// <summary>Closes idle libraries once a minute.</summary>
public sealed class IdleLibraries(UserLibraries libraries, Microsoft.Extensions.Options.IOptions<ServerOptions> options, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await libraries.CloseIdleAsync(options.Value.LibraryIdle);
    }
}

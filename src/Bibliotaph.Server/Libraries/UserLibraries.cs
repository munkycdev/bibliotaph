using System.Collections.Concurrent;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// One user's library: the same catalog.db, index.db and single index writer the Windows app runs, in that user's
/// folder. Only <see cref="UserLibraries"/> opens and closes it; endpoints borrow it through a <see cref="LibraryLease"/>.
/// </summary>
public sealed class UserLibrary
{
    internal UserLibrary(string userId, AppPaths paths, CatalogDatabase catalog, IndexDatabase index, IndexWriter writer)
    {
        UserId = userId;
        Paths = paths;
        Catalog = catalog;
        Index = index;
        Writer = writer;
        Contexts = new ContextFactory(catalog);
    }

    public string UserId { get; }
    public AppPaths Paths { get; }
    public CatalogDatabase Catalog { get; }
    public IndexDatabase Index { get; }
    public IndexWriter Writer { get; }

    /// <summary>For the Catalog stores, which take a context factory as the Windows app's DI gives them.</summary>
    public IDbContextFactory<CatalogDbContext> Contexts { get; }

    sealed class ContextFactory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
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
public sealed class UserLibraries(ServerPaths paths, IDatabaseRestore restore, TimeProvider clock, ILoggerFactory logs) : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, Slot> _slots = new();
    readonly ILogger _log = logs.CreateLogger<UserLibraries>();

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
            if (!TryBeginClose(slot, s => s.Leases == 0 && now - s.LastUsed >= idle, out var done)) continue;
            await CloseAsync(userId, slot, done);
            closed++;
        }
        return closed;
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
            await library.Writer.StopAsync(CancellationToken.None);
            library.Writer.Dispose();
            // Pooled connections keep the files open; a library opened again later must not share them.
            library.Index.ClearPools();
            library.Catalog.ClearPool();
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
        await restore.RestoreAsync($"users/{userId}/catalog.db", appPaths.CatalogDatabase, CancellationToken.None);
        await restore.RestoreAsync($"users/{userId}/index.db", appPaths.IndexDatabase, CancellationToken.None);

        var catalog = new CatalogDatabase(appPaths.CatalogDatabase, paths.UserBackups(userId), clock, logs.CreateLogger<CatalogDatabase>());
        var migrated = await catalog.MigrateAsync();
        if (migrated.AppliedMigrations.Count > 0)
            _log.LogInformation("Applied {Count} catalog migration(s) for {User}", migrated.AppliedMigrations.Count, userId);

        var index = IndexDatabase.ForFile(appPaths.IndexDatabase, logs.CreateLogger<IndexDatabase>());
        var opened = await index.InitializeAsync();
        if (opened != IndexOpenResult.Opened) _log.LogInformation("index.db for {User}: {Result}", userId, opened);

        var writer = new IndexWriter(index, logs.CreateLogger<IndexWriter>());
        await writer.StartAsync(CancellationToken.None);

        var library = new UserLibrary(userId, appPaths, catalog, index, writer);
        await new VocabularyStore(library.Contexts, clock).SeedAsync();
        return library;
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

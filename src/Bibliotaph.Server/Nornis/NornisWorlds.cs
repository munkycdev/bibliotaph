using System.Collections.Concurrent;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;
using Microsoft.EntityFrameworkCore;
using World = Bibliotaph.Catalog.Entities.NornisWorld;
using WorldEvent = Bibliotaph.Api.Contracts.NornisWorldEvent;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// Nornis's world events turned into collections. Each batch is written to the user's inbox in accounts.db before
/// Nornis is answered, so it's never lost while the library is closed; the inbox is applied at once when the library is
/// open, and otherwise as it opens. Applying is idempotent: an event seen before is never written again, and an event
/// older than the last one applied to its world is dropped, since it says something the world has moved past.
/// </summary>
public sealed class NornisWorlds
{
    /// <summary>How long an applied event is kept, so Nornis sending it again changes nothing.</summary>
    public static readonly TimeSpan KeptApplied = TimeSpan.FromDays(30);

    readonly AccountsDatabase _accounts;
    readonly UserLibraries _libraries;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly ConcurrentDictionary<string, SemaphoreSlim> _applying = new();

    public NornisWorlds(AccountsDatabase accounts, UserLibraries libraries, TimeProvider clock, ILogger<NornisWorlds> log)
    {
        _accounts = accounts;
        _libraries = libraries;
        _clock = clock;
        _log = log;
        libraries.WhenOpened(ApplyAsync);
    }

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Writes a batch to the user's inbox, leaving out events it holds already, then applies it if the library is open.</summary>
    public async Task ReceiveAsync(string userId, IReadOnlyList<WorldEvent> events, CancellationToken ct)
    {
        await using (var db = _accounts.CreateContext())
        {
            var ids = events.Select(e => Id(e.EventId)).Distinct().ToList();
            var known = (await db.NornisWorldEvents.Where(e => e.UserId == userId && ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            var now = Now;
            foreach (var e in events.DistinctBy(e => e.EventId).Where(e => !known.Contains(Id(e.EventId))))
                db.NornisWorldEvents.Add(new NornisWorldEvent
                {
                    UserId = userId,
                    EventId = Id(e.EventId),
                    Kind = e.Kind,
                    WorldId = Id(e.WorldId),
                    WorldName = e.WorldName.Trim(),
                    OccurredUtc = e.OccurredAt.UtcDateTime,
                    ReceivedUtc = now,
                });
            await db.SaveChangesAsync(ct);
        }

        await using var lease = await _libraries.LeaseIfOpenAsync(userId);
        if (lease is null) return;
        try
        {
            await ApplyAsync(lease.Library, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Written already, so it's applied when the library next opens; Nornis needn't send it again.
            _log.LogError(ex, "Applying Nornis's world events for {User} failed", userId);
        }
    }

    /// <summary>Applies the user's waiting events to their library, oldest first, and forgets events applied long ago.</summary>
    public async Task ApplyAsync(UserLibrary library, CancellationToken ct = default)
    {
        var gate = _applying.GetOrAdd(library.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = _accounts.CreateContext();
            var pending = (await db.NornisWorldEvents.Where(e => e.UserId == library.UserId && e.AppliedUtc == null).ToListAsync(ct))
                .OrderBy(e => e.OccurredUtc).ThenBy(e => e.ReceivedUtc).ToList();
            foreach (var e in pending)
            {
                await ApplyAsync(library, e, ct);
                // Marked one at a time: applying an event twice changes nothing, so a failure part way is safe to retry.
                e.AppliedUtc = Now;
                await db.SaveChangesAsync(ct);
            }
            var forgotten = Now - KeptApplied;
            await db.NornisWorldEvents.Where(e => e.UserId == library.UserId && e.AppliedUtc != null && e.AppliedUtc < forgotten).ExecuteDeleteAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    static async Task ApplyAsync(UserLibrary library, NornisWorldEvent e, CancellationToken ct)
    {
        await using var catalog = await library.Contexts.CreateDbContextAsync(ct);
        var world = await catalog.NornisWorlds.FindAsync([e.WorldId], ct);
        if (world is not null && e.OccurredUtc < world.LastOccurredUtc) return;
        // A rescan of the world's folder comes with Nornis as storage; until then it says nothing about collections.
        if (e.Kind == WorldEvent.FilesChanged) return;

        var collections = library.Get<CollectionsService>();
        var isNew = world is null;
        world ??= catalog.NornisWorlds.Add(new World { WorldId = e.WorldId, Name = e.WorldName }).Entity;
        var collection = world.CollectionId is { } id ? (await collections.ListAsync(ct)).FirstOrDefault(c => c.Id == id) : null;
        // The GM renamed the collection since Nornis last named it, so it keeps their name from now on.
        if (collection is not null && world.NameManaged && collection.Name != world.Name) world.NameManaged = false;

        switch (e.Kind)
        {
            case WorldEvent.Deleted:
                world.GoneUtc = e.OccurredUtc;
                break;
            case WorldEvent.Created:
            case WorldEvent.Renamed when isNew:
                world.GoneUtc = null;
                if (collection is null)
                {
                    var root = await NornisConnect.EnsureCollectionAsync(library, ct);
                    world.CollectionId = (await collections.CreateAsync(e.WorldName, root, null, ct)).Id;
                    world.NameManaged = true;
                }
                else if (world.NameManaged && collection.Name != e.WorldName)
                    await collections.RenameAsync(collection.Id, e.WorldName, collection.Description, ct);
                break;
            case WorldEvent.Renamed:
                if (collection is not null && world.NameManaged && collection.Name != e.WorldName)
                    await collections.RenameAsync(collection.Id, e.WorldName, collection.Description, ct);
                break;
        }
        world.Name = e.WorldName;
        world.LastOccurredUtc = e.OccurredUtc;
        await catalog.SaveChangesAsync(ct);
    }

    /// <summary>Each Nornis world's collection, and whether the world is gone, for the collections' badges.</summary>
    public static async Task<IReadOnlyDictionary<long, bool>> CollectionsAsync(UserLibrary library, CancellationToken ct)
    {
        await using var catalog = await library.Contexts.CreateDbContextAsync(ct);
        return await catalog.NornisWorlds.Where(w => w.CollectionId != null).ToDictionaryAsync(w => w.CollectionId!.Value, w => w.GoneUtc != null, ct);
    }

    static string Id(Guid id) => id.ToString("D");
}

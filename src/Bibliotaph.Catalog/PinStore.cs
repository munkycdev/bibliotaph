using System.Linq.Expressions;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>What a pin points at: a book, Favorites, a collection, a binder or a saved search (Pinned Views plan, choice 2).</summary>
public readonly record struct PinTarget(PinKind Kind, long? Id)
{
    public static PinTarget Favorites => new(PinKind.Favorites, null);

    public static PinTarget Entry(EntryId entryId) => new(PinKind.Entry, entryId.Value);

    public static PinTarget Collection(long collectionId) => new(PinKind.Collection, collectionId);

    public static PinTarget Binder(long packId) => new(PinKind.Binder, packId);

    public static PinTarget SavedSearch(long viewId) => new(PinKind.SavedSearch, viewId);
}

/// <summary>A pin as the sidebar lists it, in order.</summary>
public sealed record PinInfo(long Id, PinTarget Target, int Position);

/// <summary>
/// The sidebar's Pinned group (Pinned Views plan, choices 3 to 5): one ordered list of pins of every kind. New pins go
/// to the bottom. The stores that delete a pin's target say where its pin stood, so Undo puts it back there.
/// </summary>
public sealed class PinStore(IDbContextFactory<CatalogDbContext> contexts)
{
    public async Task<IReadOnlyList<PinInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pins = await db.Pins.AsNoTracking().OrderBy(p => p.Position).ThenBy(p => p.Id).ToListAsync(ct);
        return [.. pins.Select(p => new PinInfo(p.Id, TargetOf(p), p.Position))];
    }

    /// <summary>Pins at the bottom of the list. Returns false if it was pinned already.</summary>
    public async Task<bool> PinAsync(PinTarget target, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Pins.AnyAsync(Matches(target), ct)) return false;
        await AddAsync(db, target, null, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Unpins. Returns where the pin stood, or null if it wasn't pinned.</summary>
    public async Task<int?> UnpinAsync(PinTarget target, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var position = await PositionAsync(db, target, ct);
        if (position is not null) await db.Pins.Where(Matches(target)).ExecuteDeleteAsync(ct);
        return position;
    }

    /// <summary>Where the target's pin stands, or null if it isn't pinned.</summary>
    internal static Task<int?> PositionAsync(CatalogDbContext db, PinTarget target, CancellationToken ct) =>
        db.Pins.Where(Matches(target)).Select(p => (int?)p.Position).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Adds a pin for the caller to save: at <paramref name="position"/>, moving those at or after it down one, or at
    /// the bottom when null.
    /// </summary>
    internal static async Task AddAsync(CatalogDbContext db, PinTarget target, int? position, CancellationToken ct)
    {
        if (position is { } at) await db.Pins.Where(p => p.Position >= at).ExecuteUpdateAsync(s => s.SetProperty(p => p.Position, p => p.Position + 1), ct);
        else position = (await db.Pins.MaxAsync(p => (int?)p.Position, ct) ?? -1) + 1;
        db.Pins.Add(new Pin
        {
            Kind = target.Kind,
            Position = position.Value,
            EntryId = target.Kind == PinKind.Entry ? target.Id : null,
            CollectionId = target.Kind == PinKind.Collection ? target.Id : null,
            PackId = target.Kind == PinKind.Binder ? target.Id : null,
            SmartViewId = target.Kind == PinKind.SavedSearch ? target.Id : null,
        });
    }

    internal static Expression<Func<Pin, bool>> Matches(PinTarget target)
    {
        var id = target.Id;
        return target.Kind switch
        {
            PinKind.Entry => p => p.EntryId == id,
            PinKind.Collection => p => p.CollectionId == id,
            PinKind.Binder => p => p.PackId == id,
            PinKind.SavedSearch => p => p.SmartViewId == id,
            _ => p => p.Kind == PinKind.Favorites,
        };
    }

    static PinTarget TargetOf(Pin pin) => new(pin.Kind, pin.Kind switch
    {
        PinKind.Entry => pin.EntryId,
        PinKind.Collection => pin.CollectionId,
        PinKind.Binder => pin.PackId,
        PinKind.SavedSearch => pin.SmartViewId,
        _ => null,
    });
}

using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A saved Smart View as the sidebar and Home list it, with its definition as stored.</summary>
public sealed record SmartViewInfo(long Id, string Name, string Definition, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>What deleting a view took away, for Undo: the view and where its pin stood, if it was pinned.</summary>
public sealed record SmartViewDeletion(SmartViewInfo View, int? PinPosition);

/// <summary>
/// Saved Smart Views (slice 3 plan, choice 17), listed by name; the app calls them saved searches. The definition is
/// kept as the caller wrote it; the app reads and writes it. A new one is pinned (Pinned Views plan).
/// </summary>
public sealed class SmartViewStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<SmartViewInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var views = await db.SmartViews.AsNoTracking().ToListAsync(ct);
        return [.. views.Select(Info).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Id)];
    }

    public async Task<SmartViewInfo?> GetAsync(long viewId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SmartViews.AsNoTracking().FirstOrDefaultAsync(v => v.Id == viewId, ct) is { } view ? Info(view) : null;
    }

    public async Task<SmartViewInfo> CreateAsync(string name, string definition, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = Now;
        var view = new SmartView { Name = Name(name), Definition = definition, CreatedUtc = now, UpdatedUtc = now };
        db.SmartViews.Add(view);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await PinStore.AddAsync(db, PinTarget.SavedSearch(view.Id), null, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Info(view);
    }

    /// <summary>Update view: the view now shows what the Library shows. Returns false if it doesn't exist.</summary>
    public async Task<bool> UpdateAsync(long viewId, string definition, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = Now;
        return await db.SmartViews.Where(v => v.Id == viewId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Definition, definition).SetProperty(v => v.UpdatedUtc, now), ct) > 0;
    }

    public async Task<bool> RenameAsync(long viewId, string name, CancellationToken ct = default)
    {
        var clean = Name(name);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SmartViews.Where(v => v.Id == viewId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Name, clean), ct) > 0;
    }

    /// <summary>Deletes a view, and its pin. Returns what Undo needs, or null if it wasn't there.</summary>
    public async Task<SmartViewDeletion?> DeleteAsync(long viewId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SmartViews.FindAsync([viewId], ct) is not { } view) return null;
        var pin = await PinStore.PositionAsync(db, PinTarget.SavedSearch(viewId), ct);
        db.SmartViews.Remove(view);
        await db.SaveChangesAsync(ct);
        return new SmartViewDeletion(Info(view), pin);
    }

    /// <summary>Undo after Delete: the view again, with its id, pinned where it was if it was.</summary>
    public async Task<SmartViewInfo> RestoreAsync(SmartViewDeletion deleted, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (old, pin) = (deleted.View, deleted.PinPosition);
        var view = new SmartView { Id = old.Id, Name = old.Name, Definition = old.Definition, CreatedUtc = old.CreatedUtc, UpdatedUtc = old.UpdatedUtc };
        db.SmartViews.Add(view);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (pin is not null)
        {
            await PinStore.AddAsync(db, PinTarget.SavedSearch(view.Id), pin, ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Info(view);
    }

    static SmartViewInfo Info(SmartView view) => new(view.Id, view.Name, view.Definition, view.CreatedUtc, view.UpdatedUtc);

    static string Name(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException("A saved search needs a name.", nameof(name));
}

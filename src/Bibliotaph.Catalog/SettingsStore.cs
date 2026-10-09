using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

public static class SettingKeys
{
    /// <summary>A <see cref="Core.ThemePreference"/> name.</summary>
    public const string Appearance = "appearance";

    /// <summary>Where the last pop-out reader window was: a <c>WindowPlacement</c> in pixels.</summary>
    public const string ReaderWindow = "reader-window";
}

/// <summary>Key-value settings in catalog.db, so they travel with backups.</summary>
public sealed class SettingsStore(IDbContextFactory<CatalogDbContext> contexts)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Settings.Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([key], ct);
        if (setting is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else setting.Value = value;
        await db.SaveChangesAsync(ct);
    }
}

using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

public static class SettingKeys
{
    /// <summary>A <see cref="Core.ThemePreference"/> name.</summary>
    public const string Appearance = "appearance";

    /// <summary>Where the last pop-out reader window was: a <c>WindowPlacement</c> in pixels.</summary>
    public const string ReaderWindow = "reader-window";

    /// <summary>"True" when every suggestion goes to Needs review, not only conflicts and missing titles.</summary>
    public const string ReviewAll = "review.all";

    /// <summary>The model server's address, as "http://localhost:11434". No AI runs until there is one.</summary>
    public const string AiEndpoint = "ai.endpoint";

    /// <summary>"ollama" or "openai-compatible": what the endpoint turned out to be, recorded with each run.</summary>
    public const string AiProvider = "ai.provider";

    /// <summary>The model the classifier asks, by the name the server lists it under.</summary>
    public const string AiModel = "ai.model";

    /// <summary>"True" while classification runs; "False" keeps the setup but stops sending books.</summary>
    public const string AiEnabled = "ai.enabled";

    /// <summary>"True" when the user agreed to send excerpts to an endpoint that isn't on this computer or network.</summary>
    public const string AiRemoteAllowed = "ai.remote";

    /// <summary>Library folders whose books are never sent to AI, as a JSON array of source root ids.</summary>
    public const string AiSkippedRoots = "ai.skip-roots";

    /// <summary>"True" once the first-run AI step has been answered, either way.</summary>
    public const string AiAsked = "ai.asked";
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

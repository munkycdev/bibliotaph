using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

public static class SettingKeys
{
    /// <summary>A <see cref="Core.ThemePreference"/> name.</summary>
    public const string Appearance = "appearance";

    /// <summary>Where the last pop-out reader window was: a <c>WindowPlacement</c> in pixels.</summary>
    public const string ReaderWindow = "reader-window";

    /// <summary>
    /// The web server's keys for model services, sealed with the server's own keys, which nothing but that server can
    /// open.
    /// </summary>
    public const string ServerAiKeys = "server.ai-keys";

    /// <summary>The indexing lanes paused in Settings › Processing on the web, by name, separated by commas.</summary>
    public const string ServerPausedLanes = "server.paused-lanes";

    /// <summary>
    /// The PDF passwords a reader on the web chose to remember, by content hash, sealed with the server's own keys as
    /// its AI keys are.
    /// </summary>
    public const string ServerPdfPasswords = "server.pdf-passwords";

    /// <summary>Settings left out of backups and the JSON export, as they're only any use where they were made.</summary>
    public static IReadOnlySet<string> Private { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ServerAiKeys, ServerPausedLanes, ServerPdfPasswords };

    /// <summary>"True" when every suggestion goes to Needs review, not only conflicts and missing titles.</summary>
    public const string ReviewAll = "review.all";

    /// <summary>"True" when backups take the search index (index.db) too.</summary>
    public const string BackupIncludeIndex = "backup.include-index";

    /// <summary>"True" when books are looked up online by the ISBNs they print. Off until the user turns it on.</summary>
    public const string LookUpOnline = "lookup.online";

    /// <summary>The web library's address, when it isn't bibliotaph.online: only a developer sets it.</summary>
    public const string WebAddress = "web.address";

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

    /// <summary>"True" when Bibliotaph runs the AI itself instead of asking a model server.</summary>
    public const string AiBuiltIn = "ai.built-in";

    /// <summary>The built-in AI's model, by its id in the list of models offered: "qwen3-8b".</summary>
    public const string AiBuiltInModel = "ai.built-in.model";

    /// <summary>
    /// The model server's embedding model, which Ask this book finds pages by meaning with: "nomic-embed-text". Empty
    /// for none, and Ask finds pages by their words.
    /// </summary>
    public const string AiEmbeddingModel = "ai.embedding.model";

    /// <summary>The built-in AI's embedding model, by its id: "nomic-embed-text-v1.5". Set when it is downloaded.</summary>
    public const string AiBuiltInEmbeddingModel = "ai.built-in.embedding";

    /// <summary>
    /// "False" stops the library's pages being read for meaning in the background, for questions to the whole library;
    /// otherwise, with an embedding model set, they are.
    /// </summary>
    public const string AiLibraryVectors = "ai.embedding.library";

    /// <summary>"True" once the first-run AI step has been answered, either way.</summary>
    public const string AiAsked = "ai.asked";

    /// <summary>
    /// "False" stops the AI reading whole books chapter by chapter once their cataloging is done; otherwise it reads
    /// them, slowly and only while nothing else wants the AI.
    /// </summary>
    public const string AiScanWholeBooks = "ai.scan-whole-books";

    /// <summary>
    /// "False" keeps files that indexing downloaded on this device; otherwise they go back to online-only.
    /// </summary>
    public const string ReturnOnlineOnly = "online-only.return";

    /// <summary>
    /// "False" shows a plain mark instead of a site's own icon on books owned online, and fetches none; otherwise icons
    /// show.
    /// </summary>
    public const string SiteIcons = "library.site-icons";

    /// <summary>Where a book owned online opens: "app" (the online reader, the default) or "browser".</summary>
    public const string OnlineReader = "library.online-reader";

    /// <summary>
    /// "False" shows a service's pages in the online reader as the service draws them; otherwise Reading mode is on.
    /// </summary>
    public const string OnlineReadingMode = "library.online-reading-mode";

    /// <summary>
    /// The folder a store's download goes to from Bibliotaph's browser: a library folder or one inside it; empty or
    /// absent leaves downloads to the browser's own Downloads folder.
    /// </summary>
    public const string DownloadsFolder = "library.downloads-folder";

    /// <summary>"Library" to start on the Library; otherwise the app starts on Home.</summary>
    public const string StartPage = "start-page";

    /// <summary>The Library's order while browsing: a <c>LibrarySort</c> name. Absent for Recently added.</summary>
    public const string LibrarySort = "library.sort";

    /// <summary>The Library's order while searching: a <c>LibrarySort</c> name. Absent for Best match.</summary>
    public const string LibrarySearchSort = "library.search-sort";

    /// <summary>"files" while the Library shows each file in a pack on a card of its own; "packs", or absent, for a card per pack.</summary>
    public const string LibraryPackView = "library.pack-view";

    /// <summary>"1" while the sidebar is folded to its icons; otherwise it shows in full.</summary>
    public const string SidebarCollapsed = "sidebar-collapsed";

    /// <summary>This device's id in its sync file, so its own file is told from another device's of the same name.</summary>
    public const string SyncMachineId = "sync.machine-id";

    /// <summary>Which PC's work was last merged, and when, as JSON, for Settings to show after a restart.</summary>
    public const string SyncLastMerge = "sync.last-merge";
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

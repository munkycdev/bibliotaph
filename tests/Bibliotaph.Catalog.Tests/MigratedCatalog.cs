namespace Bibliotaph.Catalog.Tests;

/// <summary>
/// A catalog.db with every migration applied, made once per test run. Migrating a new file runs all of them, which
/// took about a second a test; a copy of this one migrates again with nothing to do.
/// </summary>
static class MigratedCatalog
{
    static readonly Lazy<Task<string>> Template = new(CreateAsync);

    /// <summary>
    /// Puts a migrated catalog.db at the database's path unless one is there already (a restored backup, say), then
    /// migrates it as the app does at startup.
    /// </summary>
    public static async Task MigrateFromTemplateAsync(this CatalogDatabase database, CancellationToken ct = default)
    {
        if (!File.Exists(database.FilePath)) File.Copy(await Template.Value, database.FilePath);
        await database.MigrateAsync(ct);
    }

    static async Task<string> CreateAsync()
    {
        var folder = Directory.CreateTempSubdirectory("bibliotaph-catalog-template-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(folder, recursive: true);
        var template = new CatalogDatabase(Path.Combine(folder, "catalog.db"), Path.Combine(folder, "backups"));
        await template.MigrateAsync();
        template.ClearPool();
        return template.FilePath;
    }
}

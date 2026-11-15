using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Databases made once for the whole run, copied into place where a test's server would otherwise make them: applying
/// every migration to each new library took about a second of each test. Catalog's tests cover the migrations.
/// </summary>
static class TemplateDatabases
{
    static readonly Lazy<Task<Templates>> Made = new(MakeAsync);

    sealed record Templates(string Catalog, string Index, string Accounts);

    /// <summary>Puts a migrated accounts.db in the data root, as a server that ran before would have left it.</summary>
    public static void PlaceAccounts(string dataRoot)
    {
        var target = new ServerPaths(dataRoot).Accounts;
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Made.Value.GetAwaiter().GetResult().Accounts, target);
    }

    static async Task<Templates> MakeAsync()
    {
        var folder = Directory.CreateTempSubdirectory("bibliotaph-server-templates-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(folder, recursive: true);

        var catalog = new CatalogDatabase(Path.Combine(folder, "catalog.db"), Path.Combine(folder, "backups"));
        await catalog.MigrateAsync();
        catalog.ClearPool();

        var index = IndexDatabase.ForFile(Path.Combine(folder, "index.db"));
        await index.InitializeAsync();
        index.ClearPools();

        var accounts = new AccountsDatabase(Path.Combine(folder, "accounts.db"));
        await accounts.MigrateAsync();
        accounts.ClearPool();
        return new Templates(catalog.FilePath, index.FilePath!, accounts.FilePath);
    }

    /// <summary>
    /// Brings a new library's catalog.db and index.db "back" as copies of the templates, where the server would restore
    /// them from its replica. A library's other files, and any database already on disk, are left alone.
    /// </summary>
    public sealed class Restore : IDatabaseRestore
    {
        public async Task RestoreAsync(string relativePath, string target, CancellationToken ct)
        {
            if (File.Exists(target)) return;
            var templates = await Made.Value.WaitAsync(ct);
            var template = Path.GetFileName(relativePath) switch
            {
                "catalog.db" => templates.Catalog,
                "index.db" => templates.Index,
                _ => null,
            };
            if (template is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(template, target);
        }
    }
}

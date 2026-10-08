namespace Bibliotaph.Core;

/// <summary>
/// Where Bibliotaph keeps its own data: %LOCALAPPDATA%\Bibliotaph, never a synced folder,
/// because a sync client and SQLite writing the same file is a known way to corrupt a database.
/// Computes paths only; creating the folders is the caller's job.
/// </summary>
public sealed record AppPaths(string Root)
{
    public string CatalogDatabase => Path.Combine(Root, "catalog.db");
    public string IndexDatabase => Path.Combine(Root, "index.db");
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");

    public IEnumerable<string> Directories => [Root, Backups, Logs, Cache];

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bibliotaph"));
}

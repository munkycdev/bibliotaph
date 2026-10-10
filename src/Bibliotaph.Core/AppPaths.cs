namespace Bibliotaph.Core;

/// <summary>
/// Where Bibliotaph keeps its own data: %LOCALAPPDATA%\Bibliotaph, never a synced folder,
/// because a sync client and SQLite writing the same file is a known way to corrupt a database.
/// Computes paths only; creating the folders is the caller's job.
/// </summary>
public sealed record AppPaths(string Root)
{
    /// <summary>The data folder's name in %LOCALAPPDATA%.</summary>
    public const string FolderName = "Bibliotaph";

    /// <summary>
    /// The installer's pack id (slice 4l plan, choice 1), which .github/workflows/release.yml passes to vpk. Velopack
    /// installs the app into %LOCALAPPDATA%\&lt;pack id&gt; and deletes that folder on uninstall, so it must never be the
    /// data folder or hold it: uninstalling would delete the library.
    /// </summary>
    public const string PackId = "BibliotaphApp";

    public string CatalogDatabase => Path.Combine(Root, "catalog.db");
    public string IndexDatabase => Path.Combine(Root, "index.db");
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");

    /// <summary>Files from inside ZIPs, extracted while they are processed or viewed (F3). Kept to a size limit.</summary>
    public string Extract => Path.Combine(Cache, "extract");

    /// <summary>The model pilot's book list, proposals, answers and report (slice 2d). Never part of a backup or the repo.</summary>
    public string Pilot => Path.Combine(Root, "pilot");

    public IEnumerable<string> Directories => [Root, Backups, Logs, Cache];

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName));
}

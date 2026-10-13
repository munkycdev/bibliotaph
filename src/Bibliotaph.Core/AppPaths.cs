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
    /// The installer's pack id, which .github/workflows/release.yml passes to vpk. Velopack installs the app into
    /// %LOCALAPPDATA%\&lt;pack id&gt; and deletes that folder on uninstall, so it must never be the data folder or hold
    /// it: uninstalling would delete the library.
    /// </summary>
    public const string PackId = "BibliotaphApp";

    public string CatalogDatabase => Path.Combine(Root, "catalog.db");
    public string IndexDatabase => Path.Combine(Root, "index.db");
    public string Backups => Path.Combine(Root, "backups");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");

    /// <summary>Files from inside ZIPs, extracted while they are processed or viewed. Kept to a size limit.</summary>
    public string Extract => Path.Combine(Cache, "extract");

    /// <summary>
    /// The model pilot's book list, proposals, answers and report. Never part of a backup or the repo.
    /// </summary>
    public string Pilot => Path.Combine(Root, "pilot");

    /// <summary>
    /// The built-in AI's downloaded models: gigabytes each, downloaded again rather than backed up, and kept by Start
    /// over so a fresh library doesn't mean a fresh download.
    /// </summary>
    public string Models => Path.Combine(Root, "models");

    /// <summary>
    /// The built-in AI's engine, downloaded at startup by a copy of Bibliotaph that wasn't installed with one (a
    /// developer's build). An installed copy has it in aiworker\ beside Bibliotaph.exe and never uses this folder.
    /// </summary>
    public string Engine => Path.Combine(Root, "engine");

    /// <summary>
    /// The in-app browser's own profile, where the services' sign-ins are kept between imports, apart from the user's
    /// everyday browser. The browser writes it; Bibliotaph never reads its cookies.
    /// </summary>
    public string Browser => Path.Combine(Root, "browser");

    public IEnumerable<string> Directories => [Root, Backups, Logs, Cache];

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName));
}

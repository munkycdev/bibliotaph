using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Start over, while Bibliotaph is in development (to be removed before 1.0): deletes everything Bibliotaph keeps
/// about the library (catalog.db with the folder list and settings, index.db, covers, backups) and never a source
/// file. Asked for while the app runs and done at the next start, before anything opens the databases, because
/// Windows won't delete an open SQLite file. Logs stay, so a problem right after starting over can be diagnosed.
/// </summary>
public static class DataPurge
{
    const string MarkerName = "start-over.pending";

    static readonly string[] Databases = ["catalog.db", "index.db"];

    // SQLite's companions: write-ahead log, shared memory, rollback journal.
    static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];

    public static void Request(AppPaths paths) =>
        File.WriteAllText(Path.Combine(paths.Root, MarkerName), "Bibliotaph deletes its library data at the next start.");

    /// <summary>
    /// Does a requested purge. Returns false when none was requested. The marker goes last, so a purge that fails
    /// part way (a file still locked) is finished at the start after.
    /// </summary>
    public static bool RunIfRequested(AppPaths paths)
    {
        var marker = Path.Combine(paths.Root, MarkerName);
        if (!File.Exists(marker)) return false;
        // Named files only: the root may be a folder chosen with --data-root, so nothing else in it is touched.
        foreach (var file in Databases.SelectMany(d => Suffixes.Select(s => Path.Combine(paths.Root, d + s))))
            if (File.Exists(file)) File.Delete(file);
        foreach (var folder in new[] { paths.Cache, paths.Backups })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        File.Delete(marker);
        return true;
    }
}

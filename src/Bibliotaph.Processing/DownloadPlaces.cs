namespace Bibliotaph.Processing;

/// <summary>
/// Where a store's download goes: a downloads folder that is a library folder or inside one, so the library picks the
/// file up, under a name that leaves every file already there alone.
/// </summary>
public static class DownloadPlaces
{
    /// <summary>The library folder <paramref name="folder"/> is, or is inside; null when it is in none of them.</summary>
    public static T? RootOf<T>(string folder, IEnumerable<T> roots, Func<T, string> pathOf) where T : class
    {
        var wanted = Normal(folder);
        return roots.Where(r => Within(wanted, Normal(pathOf(r)))).MaxBy(r => Normal(pathOf(r)).Length);
    }

    static bool Within(string folder, string root) =>
        folder.Equals(root, StringComparison.OrdinalIgnoreCase)
        || (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (root.EndsWith(Path.DirectorySeparatorChar) || folder[root.Length] == Path.DirectorySeparatorChar));

    static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// A path in <paramref name="folder"/> for a download the store named <paramref name="suggested"/>: its file name,
    /// with " (2)", " (3)"… before the extension while <paramref name="exists"/> says that name is taken.
    /// </summary>
    public static string FreePath(string folder, string suggested, Func<string, bool> exists)
    {
        var name = string.Concat(Path.GetFileName(suggested.Replace('\\', '/').Split('/')[^1]).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (name.Length == 0 || name.Trim('.').Length == 0) name = "download";
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(folder, name);
        for (var n = 2; exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }
}

namespace Bibliotaph.Core;

/// <summary>
/// One folder in a library folder: the library folder's id and the path of the folder under it ("D&amp;D 5e\Adventures",
/// or "" for the library folder itself). Folders compare as Windows compares paths, ignoring case and which slash
/// separates them, so the books "from this folder" are the files directly in it (scenario review, row 8).
/// </summary>
public readonly record struct FolderKey(long RootId, string Folder)
{
    /// <summary>The folder a file sits in, from the file's path under its library folder.</summary>
    public static FolderKey Of(long rootId, string relativePath)
    {
        var cut = relativePath.LastIndexOfAny(['\\', '/']);
        return new FolderKey(rootId, cut < 0 ? "" : relativePath[..cut]);
    }

    /// <summary>The folder's own name ("Adventures"), or "" for the library folder itself.</summary>
    public string Name => Folder[(Folder.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>Whether a file at <paramref name="relativePath"/> is in this folder or one of its subfolders.</summary>
    public bool Holds(long rootId, string relativePath)
    {
        if (rootId != RootId) return false;
        var folder = Of(rootId, relativePath).Folder;
        if (Folder.Length == 0) return true;
        return Same(folder, Folder) || folder.Length > Folder.Length && (folder[Folder.Length] is '\\' or '/') && Same(folder[..Folder.Length], Folder);
    }

    public bool Equals(FolderKey other) => RootId == other.RootId && Same(Folder, other.Folder);

    public override int GetHashCode() => HashCode.Combine(RootId, StringComparer.OrdinalIgnoreCase.GetHashCode(Folder.Replace('/', '\\')));

    static bool Same(string a, string b) => string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

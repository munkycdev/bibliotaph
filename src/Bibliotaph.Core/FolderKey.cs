using System.Globalization;

namespace Bibliotaph.Core;

/// <summary>
/// One folder in a library folder: the library folder's id and the path of the folder under it ("D&amp;D 5e\Adventures",
/// or "" for the library folder itself). Folders compare as Windows compares paths, ignoring case and which slash
/// separates them, so the books "from this folder" are the files directly in it.
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

    /// <summary>The folder above this one, or the library folder itself; the library folder is its own parent.</summary>
    public FolderKey Parent => Of(RootId, Folder);

    /// <summary>
    /// The folder as a filter menu's value: the library folder's id ("12"), then the path under it after a colon
    /// ("12:D&amp;D 5e\Adventures"). Ids have no colon, so the path may.
    /// </summary>
    public string Text => Folder.Length == 0 ? RootId.ToString(CultureInfo.InvariantCulture) : $"{RootId.ToString(CultureInfo.InvariantCulture)}:{Folder}";

    /// <summary>A folder from <see cref="Text"/>, or null for anything else.</summary>
    public static FolderKey? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var id = colon < 0 ? text : text[..colon];
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var rootId)) return null;
        return new FolderKey(rootId, colon < 0 ? "" : text[(colon + 1)..].Trim('\\', '/'));
    }

    /// <summary>The folder a filter chose: a library folder, and a folder under it if one was picked.</summary>
    public static FolderKey? Chosen(long? rootId, string? folder) => rootId is { } id ? new FolderKey(id, folder?.Trim('\\', '/') ?? "") : null;

    /// <summary>
    /// How a filter menu names the folder: the library folder's name for itself, and a folder under it by its path
    /// ("D&amp;D 5e\Adventures"), after the library folder's name when there are several to tell apart.
    /// </summary>
    public string Label(string rootName, bool withRoot)
    {
        if (Folder.Length == 0) return rootName;
        var path = Folder.Replace('/', '\\');
        return withRoot ? $"{rootName}\\{path}" : path;
    }

    /// <summary>
    /// Every folder under a library folder that holds one of <paramref name="files"/> (paths under their library
    /// folders), with the folders above it, each once, library folder by library folder and in tree order.
    /// </summary>
    public static IReadOnlyList<FolderKey> Holding(IEnumerable<(long RootId, string RelativePath)> files)
    {
        var folders = new HashSet<FolderKey>();
        foreach (var (rootId, path) in files)
            for (var folder = Of(rootId, path); folder.Folder.Length > 0 && folders.Add(folder); folder = folder.Parent) { }
        return [.. folders.Order(TreeOrder.Instance)];
    }

    /// <summary>Library folder, then name by name, so a folder's subfolders come right after it.</summary>
    sealed class TreeOrder : IComparer<FolderKey>
    {
        public static readonly TreeOrder Instance = new();

        public int Compare(FolderKey x, FolderKey y)
        {
            if (x.RootId != y.RootId) return x.RootId.CompareTo(y.RootId);
            var (a, b) = (x.Folder.Split('\\', '/'), y.Folder.Split('\\', '/'));
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var order = string.Compare(a[i], b[i], StringComparison.CurrentCultureIgnoreCase);
                if (order != 0) return order;
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    public bool Equals(FolderKey other) => RootId == other.RootId && Same(Folder, other.Folder);

    public override int GetHashCode() => HashCode.Combine(RootId, StringComparer.OrdinalIgnoreCase.GetHashCode(Folder.Replace('/', '\\')));

    static bool Same(string a, string b) => string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

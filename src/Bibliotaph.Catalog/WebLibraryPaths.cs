using System.Globalization;

namespace Bibliotaph.Catalog;

/// <summary>
/// A connected PC's paths for the web library's folders: <c>/bibliotaph.online/&lt;folder id&gt;</c>, with a file's path
/// under it. No Windows path looks like one (a Windows path has a drive letter or starts with two slashes), and
/// <see cref="SourceRootStore"/> keeps it as it is. The files are read through the web library, never from a disk.
/// </summary>
public static class WebLibraryPaths
{
    public const string Host = "bibliotaph.online";

    public const string Prefix = "/" + Host + "/";

    /// <summary>The library folder on a PC for the web library's folder with this id.</summary>
    public static string Of(long folderId) => Prefix + folderId.ToString(CultureInfo.InvariantCulture);

    public static bool IsWebPath(string path) => TryParse(path, out _, out _);

    /// <summary>
    /// The web library's folder a path is in, and the path under it with forward slashes (empty for the folder itself).
    /// A file's full path joins with a backslash on Windows, so either is a separator.
    /// </summary>
    public static bool TryParse(string path, out long folderId, out string relativePath)
    {
        folderId = 0;
        relativePath = "";
        var normal = path.Replace('\\', '/');
        if (!normal.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = normal[Prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var id = slash < 0 ? rest : rest[..slash];
        if (id.Length == 0 || !id.All(char.IsAsciiDigit) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out folderId)) return false;
        relativePath = slash < 0 ? "" : rest[(slash + 1)..].Trim('/');
        return true;
    }
}

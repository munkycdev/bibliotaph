namespace Bibliotaph.Storage.OneDrive;

/// <summary>
/// A folder or file on a OneDrive, as a library path: <c>/onedrive/&lt;drive id&gt;/&lt;path in the drive&gt;</c>. Library
/// folders are kept as paths, as the Windows app keeps its own, so scanning, moves and relative paths work unchanged;
/// the drive's ID stands where a Windows path has its drive letter.
/// </summary>
public static class DrivePath
{
    public const string Prefix = "/onedrive/";

    public static string Of(string driveId, string pathInDrive)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveId);
        if (driveId.Contains('/', StringComparison.Ordinal)) throw new ArgumentException("A drive ID has no slash.", nameof(driveId));
        var inner = pathInDrive.Trim('/');
        return inner.Length == 0 ? Prefix + driveId : $"{Prefix}{driveId}/{inner}";
    }

    /// <summary>Splits a library path into its drive and the path inside it (empty for the drive's top).</summary>
    public static bool TryParse(string path, out string driveId, out string pathInDrive)
    {
        driveId = pathInDrive = "";
        // Path.Combine joins with a backslash on Windows; OneDrive allows none in a name, so it can only be a separator.
        path = path.Replace('\\', '/');
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        driveId = slash < 0 ? rest : rest[..slash];
        pathInDrive = slash < 0 ? "" : rest[(slash + 1)..].Trim('/');
        return driveId.Length > 0;
    }

    public static bool IsDrivePath(string path) => TryParse(path, out _, out _);
}

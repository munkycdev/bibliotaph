namespace Bibliotaph.Storage;

/// <summary>Where a library path points in connected storage: the provider, the account there, and the path inside it.</summary>
public readonly record struct StorageLocation(string Provider, string Account, string Path);

/// <summary>
/// A folder or file in connected storage, as a library path: <c>/&lt;provider&gt;/&lt;account&gt;/&lt;path inside it&gt;</c>, such
/// as <c>/onedrive/&lt;drive id&gt;/RPG</c>. Library folders are kept as paths, as the Windows app keeps its own, so scanning,
/// moves and relative paths work unchanged; the provider and account stand where a Windows path has its drive letter.
/// </summary>
public static class StoragePath
{
    /// <summary>The providers a path can name. Only these are read as storage, so a server's own folder never is.</summary>
    public static readonly IReadOnlySet<string> Providers = new HashSet<string>(StringComparer.Ordinal) { "onedrive", "dropbox", "googledrive", "webdav" };

    public static string Of(string provider, string account, string pathInStorage)
    {
        if (!Providers.Contains(provider)) throw new ArgumentException($"{provider} isn't a storage provider.", nameof(provider));
        ArgumentException.ThrowIfNullOrEmpty(account);
        if (account.Contains('/', StringComparison.Ordinal)) throw new ArgumentException("An account ID has no slash.", nameof(account));
        var inner = pathInStorage.Trim('/');
        return inner.Length == 0 ? $"/{provider}/{account}" : $"/{provider}/{account}/{inner}";
    }

    public static string Of(StorageLocation location) => Of(location.Provider, location.Account, location.Path);

    /// <summary>Splits a library path into its provider, account and the path inside it (empty for the account's top).</summary>
    public static bool TryParse(string path, out StorageLocation location)
    {
        location = default;
        // Path.Combine joins with a backslash on Windows, and the Windows app shares these paths through library sync, so a
        // backslash is always a separator. A provider whose names may hold one maps it to another character.
        path = path.Replace('\\', '/');
        if (path.Length < 2 || path[0] != '/') return false;
        var parts = path[1..].Split('/', 3);
        if (parts.Length < 2 || !Providers.Contains(parts[0]) || parts[1].Length == 0) return false;
        location = new StorageLocation(parts[0], parts[1], parts.Length == 3 ? parts[2].Trim('/') : "");
        return true;
    }

    public static bool IsStoragePath(string path) => TryParse(path, out _);
}

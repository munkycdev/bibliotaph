using System.Net;

namespace Bibliotaph.Storage;

/// <summary>A file or folder in connected storage. <see cref="Size"/> means nothing for a folder: some providers give its total, some 0.</summary>
public sealed record StorageEntry(string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>
/// The provider answered with an error Bibliotaph can't work around, such as a folder that's gone or access withdrawn.
/// An IOException, so indexing treats a book it couldn't download like a file it couldn't open.
/// </summary>
public sealed class StorageException(HttpStatusCode status, string message) : IOException(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// One connected storage account, as a library reads it: folders by path, slash-separated and empty for the top. Each
/// provider's client sits behind this, so listing, indexing and the reader never ask which provider a book is on.
/// </summary>
public interface IStorageDrive
{
    /// <summary>The provider's name, for messages: "OneDrive".</summary>
    string ProviderName { get; }

    /// <summary>A folder's contents, every page of them, or null when the folder doesn't exist (or is a file).</summary>
    Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default);

    /// <summary>A file's bytes, streamed. Throws <see cref="StorageException"/> with NotFound when it isn't there.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// A short-lived link a browser can fetch the file's byte ranges from without a token, or null when the provider
    /// has none (the reader then reads through the server) or there's nothing at that path.
    /// </summary>
    Task<string?> ReadLinkAsync(string path, CancellationToken ct = default);
}

/// <summary>The storage one library can reach: what its owner has connected.</summary>
public interface IStorageDrives
{
    /// <returns>The account's drive, or null when it isn't connected (any more).</returns>
    IStorageDrive? Find(string provider, string account);
}

public static class StorageDrives
{
    /// <summary>The drive a library path is on and the path inside it, or null when it isn't on connected storage.</summary>
    public static (IStorageDrive Drive, StorageLocation Location)? Locate(this IStorageDrives drives, string path) =>
        StoragePath.TryParse(path, out var location) && drives.Find(location.Provider, location.Account) is { } drive ? (drive, location) : null;
}

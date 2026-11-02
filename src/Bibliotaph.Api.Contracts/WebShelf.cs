namespace Bibliotaph.Api.Contracts;

/// <summary>
/// The web library's files, for a connected PC to show beside its own: each library folder in the user's storage with
/// the files in it that the web library has read far enough to know by their content. <see cref="Version"/> changes
/// whenever anything in it does, so a PC asking again with the version it has gets nothing new.
/// </summary>
public sealed record WebShelf(string Version, IReadOnlyList<WebShelfFolder> Folders);

/// <summary>
/// A library folder in the user's storage: its ID in the web library, its name, where it is ("OneDrive · Dana"), and its
/// path in the storage as <c>/provider/account/path</c>, by which a PC that connects the same storage itself knows it
/// reads the same folder.
/// </summary>
public sealed record WebShelfFolder(long Id, string Name, string Location, IReadOnlyList<WebShelfFile> Files, string? StoragePath = null);

/// <summary>A file in a library folder, its path under the folder with forward slashes, and its content's SHA-256.</summary>
public sealed record WebShelfFile(string Path, string ContentHash, long SizeBytes, DateTime ModifiedUtc);

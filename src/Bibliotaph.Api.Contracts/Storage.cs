namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A storage account the user connected, such as their OneDrive. <see cref="Access"/> is what it was granted, in
/// plain words ("read your files"); <see cref="Scopes"/> is the same as the provider names it.
/// </summary>
public sealed record StorageAccount(string Id, string Provider, string Name, bool NeedsReconnect, DateTimeOffset Added,
    IReadOnlyList<string>? Access = null, string? Scopes = null);

/// <summary>
/// A kind of storage this server can connect: "OneDrive", whose consent screen is "Microsoft"'s. One that
/// <see cref="SignsInWithPassword"/> (WebDAV) is connected with an address, user name and password typed in Settings
/// rather than on its owner's sign-in page.
/// </summary>
public sealed record StorageProviderInfo(string Id, string Name, string Owner, bool SignsInWithPassword = false);

/// <summary>A WebDAV folder to connect: its address, and the user name and password that open it.</summary>
public sealed record WebDavConnect(string Url, string User, string Password);

/// <summary>A folder in connected storage, for picking library folders. <see cref="Path"/> is inside the storage.</summary>
public sealed record StorageFolder(string Name, string Path, bool InLibrary);

/// <summary>One folder's subfolders, and how many files sit directly in it.</summary>
public sealed record StorageFolderList(string StorageId, string Path, IReadOnlyList<StorageFolder> Folders, int Files);

/// <summary>A library folder, as the folders list describes it.</summary>
public sealed record LibraryFolder(long Id, string Name, string Location, string Detail, bool Offline);

public sealed record AddLibraryFolder(string StorageId, string Path);

/// <summary>
/// How far the library is from open. After an update or a restart the server brings each library's databases back
/// from storage before it can answer anything, which can take minutes, so the page asks this, which never waits.
/// </summary>
/// <param name="Ready">Open, so every other call answers without waiting on it.</param>
/// <param name="Step">"restoring" while the databases come back, "starting" once they are back, "failed" when the last
/// try didn't work and another is coming; null once it's ready.</param>
/// <param name="Seconds">How long this step's opening has taken so far.</param>
public sealed record LibraryOpening(bool Ready, string? Step = null, int Seconds = 0);

/// <summary>
/// What the library is doing, for the sidebar: the Windows app's status line, whether it's still busy, and what
/// Needs review holds, its metadata suggestions and its files needing attention, which the sidebar counts together.
/// </summary>
public sealed record LibraryStatus(string Text, bool Busy, int Books, int Folders, int Suggestions = 0, int Attention = 0, bool Ask = false)
{
    public int Review => Suggestions + Attention;
}

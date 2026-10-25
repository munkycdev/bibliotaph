namespace Bibliotaph.Api.Contracts;

/// <summary>A storage account the user connected, such as their OneDrive.</summary>
public sealed record StorageAccount(string Id, string Provider, string Name, bool NeedsReconnect, DateTimeOffset Added);

/// <summary>A folder in connected storage, for picking library folders. <see cref="Path"/> is inside the storage.</summary>
public sealed record StorageFolder(string Name, string Path, bool InLibrary);

/// <summary>One folder's subfolders, and how many files sit directly in it.</summary>
public sealed record StorageFolderList(string StorageId, string Path, IReadOnlyList<StorageFolder> Folders, int Files);

/// <summary>A library folder, described as the Windows app's Library folders list describes it.</summary>
public sealed record LibraryFolder(long Id, string Name, string Location, string Detail, bool Offline);

public sealed record AddLibraryFolder(string StorageId, string Path);

/// <summary>
/// What the library is doing, for the sidebar: the Windows app's status line, whether it's still busy, and what
/// Needs review holds, its metadata suggestions and its files needing attention, which the sidebar counts together.
/// </summary>
public sealed record LibraryStatus(string Text, bool Busy, int Books, int Folders, int Suggestions = 0, int Attention = 0)
{
    public int Review => Suggestions + Attention;
}

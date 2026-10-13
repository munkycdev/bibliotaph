using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>Why a book's file can't be opened right now.</summary>
public enum AwayReason
{
    /// <summary>Its library folder can't be reached: its drive is unplugged, or its network share is away.</summary>
    Offline,

    /// <summary>Its file isn't where it was, in a folder that can be reached, and hasn't turned up anywhere else.</summary>
    Missing,

    /// <summary>New content was found where its file was and is still being read: a new version, not a file that has gone.</summary>
    BeingRead,
}

/// <summary>
/// Why a book can't be opened, for the reader's "plug in the drive" prompt and a binder item that stays dimmed: the
/// library folder its file was in, the last place it was, and which folders to look at again before trying once more.
/// Worked out from the catalog alone, so nothing is read from the disk.
/// </summary>
public sealed record AwayFile(AwayReason Reason, string? Folder = null, string? LastPath = null, IReadOnlyList<long>? OfflineRoots = null)
{
    /// <summary>
    /// From where a document's files are or were (<see cref="LibraryStore.GetLocationsAsync"/>): offline when any of
    /// them is in a folder that can't be reached, since it comes back with its folder; otherwise missing.
    /// </summary>
    public static AwayFile From(IReadOnlyList<DocumentLocation> locations, bool beingRead = false)
    {
        if (beingRead) return new AwayFile(AwayReason.BeingRead);
        var offline = locations.Where(l => l.RootAvailability == SourceRootAvailability.Offline).ToList();
        if (offline.Count > 0)
            return new AwayFile(AwayReason.Offline, offline[0].RootPath, offline[0].ExplorerPath, [.. offline.Select(l => l.RootId).Distinct()]);
        var last = locations.Count > 0 ? locations[0] : null;
        return new AwayFile(AwayReason.Missing, last?.RootPath, last?.ExplorerPath);
    }

    /// <summary>"E:" when the library folder is on a drive letter, so the prompt can name the drive; null for a network share.</summary>
    public string? Drive => Folder is { Length: >= 2 } path && path[1] == ':' && char.IsAsciiLetter(path[0]) ? path[..2].ToUpperInvariant() : null;

    /// <summary>The folder the file was last in, or the ZIP's when it was inside one.</summary>
    public string? LastFolder => LastPath is null ? null : Path.GetDirectoryName(LastPath);

    /// <summary>
    /// The library folders to look at again before trying once more: an offline book's own, which may be back; for a
    /// missing file every folder (null), since it may have moved into another; none for a new version being read.
    /// </summary>
    public IReadOnlyCollection<long>? FoldersToLook => Reason switch
    {
        AwayReason.Offline => OfflineRoots ?? [],
        AwayReason.Missing => null,
        _ => [],
    };

    /// <summary>A missing file's last folder can be shown in File Explorer.</summary>
    public bool CanShowFolder => Reason == AwayReason.Missing && LastPath is not null;

    /// <summary>The prompt's heading.</summary>
    public string Title => Reason switch
    {
        AwayReason.Offline when Drive is not null => "Plug in the drive to read this book.",
        AwayReason.Offline => "This book's folder can't be reached right now.",
        AwayReason.Missing => "This book's file isn't where it was.",
        _ => "A new version of this book is still being read.",
    };

    /// <summary>What happened and what to do, naming the folder.</summary>
    public string Message => Reason switch
    {
        AwayReason.Offline when Drive is { } drive => $"It's in {Folder}, on drive {drive}, which can't be reached right now. Plug the drive in, then choose Try again.",
        AwayReason.Offline => $"It's in {Folder ?? "a library folder"}, which can't be reached right now. Once it's back, choose Try again.",
        AwayReason.Missing when LastFolder is { } folder => $"It was last in {folder}. Put it back, or into any library folder, then choose Find it to look again.",
        AwayReason.Missing => "It's gone from your library folders. Put it back in one, then choose Find it to look again.",
        _ => "It opens once it has been read, in a moment. Choose Try again then.",
    };

    /// <summary>The prompt's button: Find it looks in every folder for a missing file; otherwise Try again.</summary>
    public string Action => Reason == AwayReason.Missing ? "Find it" : "Try again";

    /// <summary>One line, for a binder item that can't open.</summary>
    public string Line => Reason switch
    {
        AwayReason.Offline when Drive is { } drive => $"Its folder, {Folder}, is offline. Plug in drive {drive} and it opens again.",
        AwayReason.Offline => $"Its folder, {Folder ?? "a library folder"}, is offline. It opens again once the folder is back.",
        AwayReason.Missing when LastFolder is { } folder => $"Its file is gone from your library folders. It was last in {folder}.",
        AwayReason.Missing => "Its file is gone from your library folders.",
        _ => "New version: still being read.",
    };
}

using Bibliotaph.Catalog;

namespace Bibliotaph.Processing;

/// <summary>
/// Every file a listing of a library folder found, and the folders under it that could not be read, relative to it;
/// the folder itself is <c>.</c>, which makes the whole folder read as unreachable.
/// </summary>
public sealed record SourceListing(IReadOnlyList<ScannedFile> Files, IReadOnlyList<string> Inaccessible);

/// <summary>
/// Lists a library folder without opening any file: names, sizes, times and whether each is online-only, so cloud
/// files are not downloaded. Where the folder lives, a Windows folder or a cloud drive, is the lister's business;
/// which files Bibliotaph reads is <see cref="SourceScanner"/>'s.
/// </summary>
public interface ISourceLister
{
    /// <summary>
    /// Every file under <paramref name="root"/>, of any format, leaving out folders that are themselves one of
    /// <paramref name="otherRoots"/>, so overlapping roots never list a file twice.
    /// </summary>
    /// <returns>The listing, or null when the root itself can't be reached (offline drive, unplugged disk).</returns>
    Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct);
}

/// <summary>
/// The file system's listing. Symbolic links and junctions are not followed (no cycles), and attributes alone tell a
/// cloud placeholder whose content is not on disk.
/// </summary>
public sealed class FileSystemSourceLister : ISourceLister
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS and FILE_ATTRIBUTE_RECALL_ON_OPEN: a cloud placeholder whose content is not on disk.
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct) =>
        Task.Run(() => List(root, otherRoots, ct), ct);

    static SourceListing? List(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct)
    {
        var rootInfo = new DirectoryInfo(root);
        try
        {
            if (!rootInfo.Exists) return null;
            _ = rootInfo.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var nested = otherRoots
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Where(r => r.Length > rootInfo.FullName.Length && SourceScanner.IsUnder(r, rootInfo.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = new List<ScannedFile>();
        var inaccessible = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inaccessible.Add(Path.GetRelativePath(rootInfo.FullName, directory.FullName));
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    // A cloud folder is a reparse point too, so test for a real link rather than the attribute.
                    if (sub.LinkTarget is not null || nested.Contains(Path.TrimEndingDirectorySeparator(sub.FullName))) continue;
                    pending.Push(sub);
                    continue;
                }
                if (entry is not FileInfo file || entry.LinkTarget is not null) continue;
                var isOnlineOnly = (file.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;
                files.Add(new ScannedFile(Path.GetRelativePath(rootInfo.FullName, file.FullName), file.Length, file.LastWriteTimeUtc, isOnlineOnly));
            }
        }
        return new SourceListing(files, inaccessible);
    }
}

using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>What a folder holds, for the Add folder preview and the processing summary.</summary>
public sealed record ScanSummary
{
    /// <summary>Indexable files by format (pdf, jpg, png).</summary>
    public required IReadOnlyDictionary<string, int> ByFormat { get; init; }
    public int Unsupported { get; init; }

    /// <summary>ZIPs, whose PDFs and images are read from inside them (F3). What they hold isn't known until then.</summary>
    public int Archives { get; init; }

    public int OnlineOnly { get; init; }

    /// <summary>Bytes that will download as online-only files are indexed.</summary>
    public long OnlineOnlyBytes { get; init; }

    /// <summary>Folders that could not be read, relative to the root.</summary>
    public IReadOnlyList<string> Inaccessible { get; init; } = [];

    public int Indexable => ByFormat.Values.Sum();
}

public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, ScanSummary Summary);

/// <summary>
/// Lists a source folder without opening any file. Reads names, sizes, times and attributes only, so cloud
/// placeholders are not downloaded. Symbolic links and junctions are not followed (no cycles), and folders that
/// are themselves library roots are left to their own scan, so overlapping roots never list a file twice.
/// </summary>
public static class SourceScanner
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS and FILE_ATTRIBUTE_RECALL_ON_OPEN: a cloud placeholder whose content is not on disk.
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <returns>The listing, or null when the root itself can't be reached (offline drive, unplugged disk).</returns>
    public static ScanResult? Scan(string root, IEnumerable<string> otherRoots, CancellationToken ct = default)
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
            .Where(r => r.Length > rootInfo.FullName.Length && IsUnder(r, rootInfo.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = new List<ScannedFile>();
        var byFormat = new Dictionary<string, int>();
        var inaccessible = new List<string>();
        int unsupported = 0, archives = 0, onlineOnly = 0;
        long onlineOnlyBytes = 0;

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

                var format = SourceFormats.FromFileName(file.Name);
                var isArchive = SourceFormats.IsArchive(file.Name);
                if (format is null && !isArchive)
                {
                    unsupported++;
                    continue;
                }
                var isOnlineOnly = (file.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;
                files.Add(new ScannedFile(Path.GetRelativePath(rootInfo.FullName, file.FullName), file.Length, file.LastWriteTimeUtc, isOnlineOnly));
                if (format is null) archives++;
                else byFormat[format] = byFormat.GetValueOrDefault(format) + 1;
                if (isOnlineOnly)
                {
                    onlineOnly++;
                    onlineOnlyBytes += file.Length;
                }
            }
        }

        return new ScanResult(files, new ScanSummary
        {
            ByFormat = byFormat,
            Unsupported = unsupported,
            Archives = archives,
            OnlineOnly = onlineOnly,
            OnlineOnlyBytes = onlineOnlyBytes,
            Inaccessible = inaccessible,
        });
    }

    static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
}

using Bibliotaph.Catalog;
using Bibliotaph.Processing;

namespace Bibliotaph.Storage.OneDrive;

/// <summary>The OneDrives one library can reach, by drive ID: those its owner has connected.</summary>
public interface IDriveClients
{
    /// <returns>The drive's client, or null when it isn't connected (any more).</returns>
    OneDriveClient? Find(string driveId);
}

/// <summary>
/// Lists a library folder on a OneDrive by walking it, one Graph call a folder. Nothing is downloaded: names, sizes and
/// times come from the listing. Every file is read the same way, so none counts as online-only.
/// </summary>
public sealed class OneDriveSourceLister(IDriveClients drives) : ISourceLister
{
    public async Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct)
    {
        if (!DrivePath.TryParse(root, out var driveId, out var top) || drives.Find(driveId) is not { } client) return null;

        // Library folders inside this one on the same drive are listed as their own.
        var nested = otherRoots
            .Select(r => DrivePath.TryParse(r, out var d, out var p) && d == driveId ? p : null)
            .Where(p => p is not null && p.Length > top.Length && IsUnder(p, top))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<DriveEntry>? first;
        try
        {
            first = await client.ChildrenAsync(driveId, top, ct);
        }
        catch (Exception ex) when (ex is OneDriveException or HttpRequestException)
        {
            return null;
        }
        if (first is null) return null;

        var files = new List<ScannedFile>();
        var inaccessible = new List<string>();
        var pending = new Queue<(string Relative, IReadOnlyList<DriveEntry>? Entries)>();
        pending.Enqueue(("", first));
        while (pending.TryDequeue(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            var entries = folder.Entries;
            if (entries is null)
            {
                try
                {
                    entries = await client.ChildrenAsync(driveId, Join(top, folder.Relative), ct);
                }
                catch (Exception ex) when (ex is OneDriveException or HttpRequestException)
                {
                    // Half a listing would read as files gone missing: the whole folder is unreachable this time.
                    return null;
                }
                if (entries is null)
                {
                    inaccessible.Add(folder.Relative);
                    continue;
                }
            }

            foreach (var entry in entries)
            {
                var relative = folder.Relative.Length == 0 ? entry.Name : $"{folder.Relative}/{entry.Name}";
                if (entry.IsFolder)
                {
                    if (!nested.Contains(Join(top, relative))) pending.Enqueue((relative, null));
                    continue;
                }
                files.Add(new ScannedFile(relative, entry.Size, entry.ModifiedUtc, OnlineOnly: false));
            }
        }
        return new SourceListing(files, inaccessible);
    }

    static string Join(string top, string relative) => top.Length == 0 ? relative : relative.Length == 0 ? top : $"{top}/{relative}";

    static bool IsUnder(string path, string folder) =>
        folder.Length == 0 || (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path.Length > folder.Length && path[folder.Length] == '/');
}

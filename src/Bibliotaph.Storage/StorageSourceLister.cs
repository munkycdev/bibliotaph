using Bibliotaph.Catalog;
using Bibliotaph.Processing;

namespace Bibliotaph.Storage;

/// <summary>
/// Lists a library folder in connected storage by walking it, one call a folder. Nothing is downloaded: names, sizes
/// and times come from the listing. Every file is read the same way, so none counts as online-only.
/// </summary>
public sealed class StorageSourceLister(IStorageDrives drives) : ISourceLister
{
    public async Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct)
    {
        if (drives.Locate(root) is not { } found) return null;
        var (drive, location) = found;
        var top = location.Path;

        // Library folders inside this one in the same account are listed as their own.
        var nested = otherRoots
            .Select(r => StoragePath.TryParse(r, out var other) && (other.Provider, other.Account) == (location.Provider, location.Account) ? other.Path : null)
            .Where(p => p is not null && p.Length > top.Length && IsUnder(p, top))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<StorageEntry>? first;
        try
        {
            first = await drive.ChildrenAsync(top, ct);
        }
        catch (Exception ex) when (ex is StorageException or HttpRequestException)
        {
            return null;
        }
        if (first is null) return null;

        var files = new List<ScannedFile>();
        var inaccessible = new List<string>();
        var pending = new Queue<(string Relative, IReadOnlyList<StorageEntry>? Entries)>();
        pending.Enqueue(("", first));
        while (pending.TryDequeue(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            var entries = folder.Entries;
            if (entries is null)
            {
                try
                {
                    entries = await drive.ChildrenAsync(Join(top, folder.Relative), ct);
                }
                catch (Exception ex) when (ex is StorageException or HttpRequestException)
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

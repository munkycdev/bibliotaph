using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The web library as a connected PC reads its books: the list of its files, what the server read from each, its
/// covers, and a file itself to open. <see cref="WebLibrary"/> asks the server; the tests have their own.
/// </summary>
public interface IWebBooks
{
    /// <summary>Whether this PC is connected to a web library.</summary>
    bool IsOn { get; }

    Task<WebShelfRead> ReadShelfAsync(string? version, CancellationToken ct);

    Task<WebReading> ReadReadingAsync(string contentHash, CancellationToken ct);

    Task<byte[]?> ReadCoverAsync(string name, CancellationToken ct);

    /// <summary>
    /// The file with this content, to read once from the start. Throws <see cref="FileNotFoundException"/> when the web
    /// library no longer has it, and <see cref="IOException"/> when it can't be had now.
    /// </summary>
    Task<Stream> OpenFileAsync(string contentHash, CancellationToken ct);
}

/// <summary>
/// The web library's files as this PC last listed them, kept on disk (<see cref="AppPaths.WebShelf"/>) so its folders
/// list, and its files' hashes are known, while the server can't be reached. A file's hash comes from the list, so a
/// file is never downloaded to be hashed (<see cref="IKnownHashes"/>).
/// </summary>
public sealed class WebShelfCache(AppPaths paths, ILogger<WebShelfCache>? log = null) : IKnownHashes
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly ILogger _log = log ?? NullLogger<WebShelfCache>.Instance;
    readonly Lock _lock = new();
    Snapshot? _snapshot;
    bool _loaded;

    /// <summary>A shelf with its folders and files found by id and path.</summary>
    sealed record Snapshot(WebShelf Shelf, IReadOnlyDictionary<long, (WebShelfFolder Folder, IReadOnlyDictionary<string, WebShelfFile> Files)> Folders)
    {
        public static Snapshot Of(WebShelf shelf)
        {
            var folders = new Dictionary<long, (WebShelfFolder, IReadOnlyDictionary<string, WebShelfFile>)>();
            foreach (var folder in shelf.Folders)
            {
                // Paths compare as Windows compares them; storage that tells two names apart by case alone keeps the first.
                var files = new Dictionary<string, WebShelfFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in folder.Files) files.TryAdd(file.Path, file);
                folders.TryAdd(folder.Id, (folder, files));
            }
            return new Snapshot(shelf, folders);
        }
    }

    /// <summary>The shelf this PC has, or null when it has none: never connected, or disconnected since.</summary>
    public WebShelf? Current => Load()?.Shelf;

    /// <summary>A folder of the web library by its id, or null when the shelf doesn't have it.</summary>
    public WebShelfFolder? Folder(long id) => Load() is { } snapshot && snapshot.Folders.TryGetValue(id, out var folder) ? folder.Folder : null;

    /// <summary>The web library's file at a PC path (<see cref="WebLibraryPaths"/>), or null when it isn't one or isn't listed.</summary>
    public WebShelfFile? File(string path) =>
        WebLibraryPaths.TryParse(path, out var id, out var relative) && Load() is { } snapshot && snapshot.Folders.TryGetValue(id, out var folder)
            && folder.Files.TryGetValue(relative, out var file) ? file : null;

    public ContentHash? Find(string path) => File(path) is { } file && ContentHash.TryParse(file.ContentHash, out var hash) ? hash : null;

    /// <summary>
    /// A web library path as the user knows it, "bibliotaph.online › OneDrive · Dana › RPG › Maps › harbor.png"; any
    /// other path as it is.
    /// </summary>
    public string Show(string path)
    {
        if (!WebLibraryPaths.TryParse(path, out var id, out var relative)) return path;
        var folder = Folder(id)?.Location ?? "a folder no longer in the web library";
        var under = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" › ", [WebLibraryPaths.Host, folder, .. under]);
    }

    /// <summary>A library folder's name: a web library folder's own, or the last part of a folder's path.</summary>
    public string NameOf(string rootPath) =>
        WebLibraryPaths.TryParse(rootPath, out var id, out _) ? Folder(id)?.Name ?? WebLibraryPaths.Host
        : Path.GetFileName(rootPath) is { Length: > 0 } name ? name : rootPath;

    /// <summary>Keeps a new shelf, here and on disk.</summary>
    public void Replace(WebShelf shelf)
    {
        var snapshot = Snapshot.Of(shelf);
        lock (_lock)
        {
            _snapshot = snapshot;
            _loaded = true;
        }
        try
        {
            var temp = paths.WebShelf + ".tmp";
            System.IO.File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(shelf, Json));
            System.IO.File.Move(temp, paths.WebShelf, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It's asked for again at the next start; until then only an offline start goes without it.
            _log.LogWarning(ex, "Keeping the web library's list of files failed");
        }
    }

    /// <summary>Forgets the shelf, as when this PC is disconnected.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _snapshot = null;
            _loaded = true;
        }
        try
        {
            System.IO.File.Delete(paths.WebShelf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Deleting the web library's list of files failed");
        }
    }

    Snapshot? Load()
    {
        lock (_lock)
        {
            if (_loaded) return _snapshot;
            _loaded = true;
            try
            {
                if (System.IO.File.Exists(paths.WebShelf)
                    && JsonSerializer.Deserialize<WebShelf>(System.IO.File.ReadAllBytes(paths.WebShelf), Json) is { Folders: not null } shelf)
                    _snapshot = Snapshot.Of(shelf);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Asked for again from the server.
                _log.LogWarning(ex, "Reading the web library's list of files failed");
            }
            return _snapshot;
        }
    }
}

/// <summary>
/// Lists library folders: a web library folder from the shelf this PC has, without asking the server, and any other
/// from <paramref name="local"/>. A web library folder the shelf doesn't have reads as unreachable, so its files are
/// kept until the shelf has it again or it's removed.
/// </summary>
public sealed class WebAwareSourceLister(ISourceLister local, WebShelfCache shelf) : ISourceLister
{
    public Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct)
    {
        if (!WebLibraryPaths.TryParse(root, out var id, out _))
            return local.ListAsync(root, [.. otherRoots.Where(r => !WebLibraryPaths.IsWebPath(r))], ct);
        if (shelf.Folder(id) is not { } folder) return Task.FromResult<SourceListing?>(null);
        // Paths as the file system gives them here, so a folder hint reads the same from either.
        return Task.FromResult<SourceListing?>(new SourceListing(
            [.. folder.Files.Select(f => new ScannedFile(f.Path.Replace('/', Path.DirectorySeparatorChar), f.SizeBytes, f.ModifiedUtc, OnlineOnly: false))], []));
    }
}

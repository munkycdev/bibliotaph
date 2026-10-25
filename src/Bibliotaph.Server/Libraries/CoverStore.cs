using System.Collections.Concurrent;
using System.Threading.Channels;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Bibliotaph.Processing;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// Where a user's covers outlive the server's disk, which a restart empties: the databases come back through
/// Litestream, and the covers they name come back from here.
/// </summary>
public interface ICoverStore
{
    Task<IReadOnlySet<string>> ListAsync(string userId, CancellationToken ct);

    Task UploadAsync(string userId, string name, Stream jpeg, CancellationToken ct);

    /// <summary>The cover's bytes, or null when it isn't kept.</summary>
    Task<byte[]?> DownloadAsync(string userId, string name, CancellationToken ct);
}

/// <summary>On a developer's machine and in tests, the disk is kept, so there is nothing to keep elsewhere.</summary>
public sealed class NoCoverStore : ICoverStore
{
    public Task<IReadOnlySet<string>> ListAsync(string userId, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task UploadAsync(string userId, string name, Stream jpeg, CancellationToken ct) => Task.CompletedTask;

    public Task<byte[]?> DownloadAsync(string userId, string name, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

/// <summary>Covers in a Blob container, under users/{user ID}/, beside the database replicas' account.</summary>
public sealed class BlobCoverStore(BlobContainerClient container) : ICoverStore
{
    public async Task<IReadOnlySet<string>> ListAsync(string userId, CancellationToken ct)
    {
        var prefix = Prefix(userId);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct)) names.Add(blob.Name[prefix.Length..]);
        return names;
    }

    public async Task UploadAsync(string userId, string name, Stream jpeg, CancellationToken ct) =>
        await container.GetBlobClient(Prefix(userId) + name).UploadAsync(jpeg,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/jpeg" } }, ct);

    public async Task<byte[]?> DownloadAsync(string userId, string name, CancellationToken ct)
    {
        try
        {
            var result = await container.GetBlobClient(Prefix(userId) + name).DownloadContentAsync(ct);
            return result.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    static string Prefix(string userId) => $"users/{UserIds.Require(userId)}/";
}

/// <summary>
/// Keeps one library's covers in the <see cref="ICoverStore"/> while the library is open: those already on disk when
/// it opens and not yet kept, then each new one as indexing draws it, and any missed once more as it closes. A cover
/// the disk has lost is fetched back when it is next asked for.
/// </summary>
public sealed class CoverMirror(string userId, CoverCache covers, ICoverStore store, ILogger<CoverMirror> log) : IAsyncDisposable
{
    readonly Channel<string> _new = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly List<FileSystemWatcher> _watchers = [];
    ConcurrentDictionary<string, bool>? _kept;
    Task? _loop;

    public void Start()
    {
        foreach (var folder in new[] { covers.Folder, covers.ChosenFolder })
        {
            Directory.CreateDirectory(folder);
            // CoverCache writes a .tmp file and renames it, so a finished cover arrives as a rename.
            var watcher = new FileSystemWatcher(folder, "*.jpg") { NotifyFilter = NotifyFilters.FileName };
            watcher.Renamed += (_, e) => _new.Writer.TryWrite(e.Name!);
            watcher.Created += (_, e) => _new.Writer.TryWrite(e.Name!);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        _loop = RunAsync();
    }

    /// <summary>Puts back a cover the disk has lost, if it is kept. True when the cover is on disk afterwards.</summary>
    public async Task<bool> RestoreAsync(string name, CancellationToken ct)
    {
        if (File.Exists(covers.PathFor(name))) return true;
        if (await store.DownloadAsync(userId, name, ct) is not { } jpeg) return false;
        covers.Restore(name, jpeg);
        _kept?.TryAdd(name, true);
        return true;
    }

    async Task RunAsync()
    {
        try
        {
            _kept = new ConcurrentDictionary<string, bool>((await store.ListAsync(userId, CancellationToken.None)).Select(n => KeyValuePair.Create(n, true)),
                StringComparer.Ordinal);
            foreach (var name in OnDisk()) _new.Writer.TryWrite(name);
            await foreach (var name in _new.Reader.ReadAllAsync()) await KeepAsync(name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Keeping the covers of {User} stopped", userId);
        }
    }

    IEnumerable<string> OnDisk() =>
        new[] { covers.Folder, covers.ChosenFolder }.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.jpg")).Select(Path.GetFileName).OfType<string>();

    async Task KeepAsync(string name, CancellationToken ct)
    {
        if (_kept is null || _kept.ContainsKey(name)) return;
        var path = covers.PathFor(name);
        if (!File.Exists(path)) return;
        try
        {
            await using (var jpeg = File.OpenRead(path)) await store.UploadAsync(userId, name, jpeg, ct);
            _kept.TryAdd(name, true);
        }
        catch (Exception ex) when (ex is IOException or RequestFailedException)
        {
            // The cover stays on disk; the next open or close tries again.
            log.LogInformation("Couldn't keep cover {Cover} of {User}: {Reason}", name, userId, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _new.Writer.TryComplete();
        if (_loop is not null)
        {
            // The covers already noticed go first; then any a watcher missed.
            await _loop;
            if (_kept is not null)
                foreach (var name in OnDisk()) await KeepAsync(name, CancellationToken.None);
        }
    }
}

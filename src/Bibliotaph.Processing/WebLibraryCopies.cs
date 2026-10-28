using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A web library book being downloaded: how much of it has come, of how much.</summary>
public sealed record WebDownload(string ContentHash, long Received, long Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Clamp(Received * 100 / Total, 0, 100);
}

/// <summary>How much of the web library is on this PC: the books downloaded, their size, and how many are kept offline.</summary>
public sealed record WebCopiesSummary(int Books, long Bytes, int KeptOffline);

/// <summary>
/// A connected PC's copies of the web library's books, downloaded through bibliotaph.online when one is opened or its
/// scanned pages are read, and kept in <see cref="AppPaths.WebCopies"/> under their content hash, checked against it.
/// Those the user keeps offline stay; the rest go, least recently read first, once they pass the room set for them
/// (<see cref="DefaultRoomGb"/> to start), and all of them when this PC is disconnected. <see cref="Progress"/> says how
/// a download is going, for the reader to show. <paramref name="gigabyte"/> is the size the room is counted in: a GB,
/// or a few bytes in the tests.
/// </summary>
public sealed class WebLibraryCopies(AppPaths paths, IWebBooks web, WebShelfCache shelf, SettingsStore settings, IDiskSpace disk,
    IndexingOptions? options = null, ILogger<WebLibraryCopies>? log = null, long gigabyte = 1L << 30) : IKeptCopies
{
    public const string KeptKey = "web.kept-offline";
    public const string RoomKey = "web.copies-room-gb";
    public const int DefaultRoomGb = 5;

    /// <summary>The rooms offered in Settings > Devices, in GB.</summary>
    public static readonly IReadOnlyList<int> Rooms = [1, 2, 5, 10, 20, 50];

    readonly long _reserveBytes = (options ?? new IndexingOptions()).OnlineOnlyReserveBytes;
    readonly ILogger _log = log ?? NullLogger<WebLibraryCopies>.Instance;
    readonly Lock _lock = new();
    readonly Dictionary<string, int> _inUse = [with(StringComparer.OrdinalIgnoreCase)];
    readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The books kept offline, by content hash; replaced, never changed, so it's read without a lock.</summary>
    IReadOnlySet<string>? _kept;

    public string Folder => paths.WebCopies;

    /// <summary>A download moved on. Raised on a background thread, at most once a percent.</summary>
    public event EventHandler<WebDownload>? Progress;

    public bool Holds(DocumentSource source) => !source.InArchive && WebLibraryPaths.IsWebPath(source.FullPath);

    /// <summary>
    /// The book's copy here, downloaded first when there isn't one. Throws <see cref="IOException"/> when it can't be had
    /// now, as when bibliotaph.online can't be reached, and <see cref="FileNotFoundException"/> when the web library no
    /// longer has it.
    /// </summary>
    public async Task<LocalFile> OpenAsync(DocumentSource source, CancellationToken ct)
    {
        var name = NameOf(source);
        Hold(name);
        try
        {
            await FetchAsync(source, name, ct);
            await EvictAsync(ct);
            return new LocalFile(Path.Combine(Folder, name), () => Release(name));
        }
        catch
        {
            Release(name);
            throw;
        }
    }

    /// <summary>Whether the book with this content has a copy here.</summary>
    public bool Has(string contentHash) =>
        Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, contentHash.ToLowerInvariant() + ".*").Any(f => !f.EndsWith(".tmp", StringComparison.Ordinal));

    public async Task<bool> IsKeptAsync(string contentHash, CancellationToken ct = default) => (await KeptAsync(ct)).Contains(contentHash);

    /// <summary>
    /// Keeps a book's copy for good, downloading it now if it isn't here, or lets it go with the others. A book that
    /// can't be downloaded isn't kept.
    /// </summary>
    public async Task KeepAsync(DocumentSource source, bool keep, CancellationToken ct = default)
    {
        var name = NameOf(source);
        Hold(name);
        try
        {
            if (keep) await FetchAsync(source, name, ct);
            var kept = new HashSet<string>(await KeptAsync(ct), StringComparer.OrdinalIgnoreCase);
            if (keep) kept.Add(source.ContentHash);
            else kept.Remove(source.ContentHash);
            await SaveKeptAsync(kept, ct);
        }
        finally
        {
            Release(name);
        }
        await EvictAsync(ct);
    }

    /// <summary>The room for copies not kept offline, in GB.</summary>
    public async Task<int> GetRoomGbAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(RoomKey, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : DefaultRoomGb;

    public async Task SetRoomGbAsync(int gb, CancellationToken ct = default)
    {
        await settings.SetAsync(RoomKey, gb.ToString(CultureInfo.InvariantCulture), ct);
        await EvictAsync(ct);
    }

    public async Task<WebCopiesSummary> SummarizeAsync(CancellationToken ct = default)
    {
        var kept = await KeptAsync(ct);
        var files = Copies();
        return new WebCopiesSummary(files.Count, files.Sum(f => f.Length), files.Count(f => kept.Contains(HashOf(f.Name))));
    }

    /// <summary>
    /// Deletes the copies not in use: all of them, or those not kept offline. Returns how many went. Those kept offline
    /// stay when this PC is disconnected, for when it connects again.
    /// </summary>
    public async Task<int> ClearAsync(bool keepKept, CancellationToken ct = default)
    {
        var kept = await KeptAsync(ct);
        var cleared = 0;
        foreach (var file in Copies())
        {
            lock (_lock)
                if (_inUse.ContainsKey(file.Name) || (keepKept && kept.Contains(HashOf(file.Name)))) continue;
            if (TryDelete(file.FullName)) cleared++;
        }
        if (!keepKept) await SaveKeptAsync([], ct);
        return cleared;
    }

    async Task FetchAsync(DocumentSource source, string name, CancellationToken ct)
    {
        var target = Path.Combine(Folder, name);
        var gate = _gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The modified time says when it was last read, for eviction.
            if (File.Exists(target))
            {
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                return;
            }
            await DownloadAsync(source.ContentHash, target, shelf.File(source.FullPath)?.SizeBytes ?? 0, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task DownloadAsync(string hash, string target, long total, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        if (disk.FreeBytes(Folder) is { } free && free - total < _reserveBytes)
            throw new IOException("There isn't enough free disk space to download this book.");
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await web.OpenFileAsync(hash, ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                var shown = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    var progress = new WebDownload(hash, received, total);
                    if (progress.Percent == shown) continue;
                    shown = progress.Percent;
                    Progress?.Invoke(this, progress);
                }
            }
            if (!ContentHash.FromBytes(sha.GetHashAndReset()).Hex.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new SourceChangedException("The file bibliotaph.online gave isn't the book it lists; it may have changed in your storage.");
            File.Move(temp, target, overwrite: true);
            _log.LogInformation("Downloaded a web library book, {Bytes} bytes", new FileInfo(target).Length);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Lets the least recently read copies go, past the room for them; those kept offline and in use stay.</summary>
    async Task EvictAsync(CancellationToken ct)
    {
        var room = await GetRoomGbAsync(ct) * gigabyte;
        var kept = await KeptAsync(ct);
        var loose = Copies().Where(f => !kept.Contains(HashOf(f.Name))).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = loose.Sum(f => f.Length);
        foreach (var file in loose)
        {
            if (total <= room) return;
            lock (_lock)
                if (_inUse.ContainsKey(file.Name)) continue;
            if (TryDelete(file.FullName)) total -= file.Length;
        }
    }

    List<FileInfo> Copies() =>
        Directory.Exists(Folder) ? [.. new DirectoryInfo(Folder).EnumerateFiles().Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))] : [];

    static string NameOf(DocumentSource source) => source.ContentHash.ToLowerInvariant() + Path.GetExtension(source.FullPath).ToLowerInvariant();

    static string HashOf(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    async Task<IReadOnlySet<string>> KeptAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _kept) is { } known) return known;
        HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await settings.GetAsync(KeptKey, ct) is { Length: > 0 } json && JsonSerializer.Deserialize<string[]>(json) is { } hashes) kept.UnionWith(hashes);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The list of web library books kept offline couldn't be read");
        }
        return Interlocked.CompareExchange(ref _kept, kept, null) ?? kept;
    }

    async Task SaveKeptAsync(HashSet<string> kept, CancellationToken ct)
    {
        Volatile.Write(ref _kept, kept);
        await settings.SetAsync(KeptKey, JsonSerializer.Serialize(kept.Order(StringComparer.Ordinal).ToArray()), ct);
    }

    void Hold(string name)
    {
        lock (_lock) _inUse[name] = _inUse.GetValueOrDefault(name) + 1;
    }

    void Release(string name)
    {
        lock (_lock)
        {
            if (_inUse.GetValueOrDefault(name) <= 1) _inUse.Remove(name);
            else _inUse[name]--;
        }
    }

    bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "A web library copy couldn't be deleted yet");
            return false;
        }
    }
}

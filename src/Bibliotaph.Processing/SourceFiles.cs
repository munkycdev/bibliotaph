using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>A document's file on disk, for the PDF engine or an image decoder, held while it is in use.</summary>
public sealed class LocalFile(string path, Action? release) : IAsyncDisposable, IDisposable
{
    Action? _release = release;

    public string Path { get; } = path;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Library files on no disk this process can open, such as a OneDrive the web server reads through its API. The PDF
/// engine and image decoders need a file, so <see cref="SourceFiles"/> fetches a copy into its cache to read.
/// </summary>
public interface IRemoteFiles
{
    bool IsRemote(string path);

    /// <summary>The file's content, read once from the start. Throws <see cref="FileNotFoundException"/> when it's gone.</summary>
    Task<Stream> OpenAsync(string path, CancellationToken ct);
}

/// <summary>
/// Library files kept in a cache of their own, with its own rules, rather than the extract cache: a connected PC's copies
/// of the web library's books. <see cref="SourceFiles"/> hands it the files it <see cref="Holds"/>.
/// </summary>
public interface IKeptCopies
{
    bool Holds(DocumentSource source);

    /// <summary>The file's copy, fetched first when there isn't one, held until disposed.</summary>
    Task<LocalFile> OpenAsync(DocumentSource source, CancellationToken ct);
}

/// <summary>A file inside a ZIP no longer has the content it was hashed with: the ZIP changed, and the next scan sorts it out.</summary>
public sealed class SourceChangedException(string message) : IOException(message);

/// <summary>
/// Gives stages and the viewer a file they can open by path. A loose file is itself; a file inside a ZIP is extracted
/// to the extract cache, named by its content hash, checked against that hash, and reused by every reader after. Files
/// in use are kept; once the cache passes its limit, the least recently used go. It also knows which library files are
/// open through it, a book in the reader or a ZIP being extracted, so none goes back to online-only while it is in use
/// (<see cref="OnlineOnlyReturns"/>). A <paramref name="remote"/> file is fetched into the cache the same way, and its
/// copy deleted as soon as its documents' stages are done with it (<see cref="DropRemoteCopiesAsync"/>). A file
/// <paramref name="kept"/> holds is that cache's to give.
/// </summary>
public sealed class SourceFiles(AppPaths paths, ArchiveReader archives, IDiskSpace disk, IndexingOptions? options = null,
    long limitBytes = SourceFiles.DefaultLimitBytes, IRemoteFiles? remote = null, IKeptCopies? kept = null)
{
    public const long DefaultLimitBytes = 5L * 1024 * 1024 * 1024;

    readonly long _reserveBytes = (options ?? new IndexingOptions()).OnlineOnlyReserveBytes;
    readonly Lock _lock = new();
    readonly Dictionary<string, int> _inUse = [with(StringComparer.OrdinalIgnoreCase)];
    /// <summary>Library files open through here, by full path, with how many hold each.</summary>
    readonly Dictionary<string, int> _open = [with(StringComparer.OrdinalIgnoreCase)];
    readonly ConcurrentDictionary<string, SemaphoreSlim> _extracting = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Copies of remote files in the cache, by name, with the documents they were fetched for.</summary>
    readonly Dictionary<string, HashSet<long>> _remoteCopies = [with(StringComparer.OrdinalIgnoreCase)];
    bool _cleaned;

    public string Folder => paths.Extract;

    /// <summary>
    /// The file to read for <paramref name="source"/>, extracting it from its ZIP when it is in one. Dispose it when
    /// done. Throws <see cref="SourceChangedException"/> when the ZIP no longer holds that content, and
    /// <see cref="IOException"/> or <see cref="InvalidDataException"/> when it can't be read.
    /// </summary>
    public async Task<LocalFile> OpenAsync(DocumentSource source, CancellationToken ct = default)
    {
        if (kept?.Holds(source) == true)
        {
            Track(source.FullPath, +1);
            try
            {
                var copy = await kept.OpenAsync(source, ct);
                return new LocalFile(copy.Path, () =>
                {
                    copy.Dispose();
                    Track(source.FullPath, -1);
                });
            }
            catch
            {
                Track(source.FullPath, -1);
                throw;
            }
        }
        var fetch = !source.InArchive && remote?.IsRemote(source.FullPath) == true;
        if (!source.InArchive && !fetch)
        {
            var path = source.FullPath;
            Track(path, +1);
            return new LocalFile(path, () => Track(path, -1));
        }
        var name = source.ContentHash.ToLowerInvariant() + Path.GetExtension(source.EntryPath ?? source.FullPath).ToLowerInvariant();
        var target = Path.Combine(Folder, name);
        Hold(name);
        try
        {
            var gate = _extracting.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // The modified time says when it was last used, for eviction; it's Bibliotaph's own copy.
                if (File.Exists(target)) File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                else if (fetch) await FetchAsync(source, target, ct);
                else await ExtractAsync(source, target, ct);
                if (fetch)
                    lock (_lock)
                    {
                        if (!_remoteCopies.TryGetValue(name, out var documents)) _remoteCopies[name] = documents = [];
                        documents.Add(source.DocumentId);
                    }
            }
            finally
            {
                gate.Release();
            }
            Evict();
            return new LocalFile(target, () => Release(name));
        }
        catch
        {
            Release(name);
            throw;
        }
    }

    async Task ExtractAsync(DocumentSource source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var archive = source.ArchivePath!;
        Track(archive, +1);
        try
        {
            await ExtractFromAsync(archive, source, target, ct);
        }
        finally
        {
            Track(archive, -1);
        }
    }

    async Task ExtractFromAsync(string archive, DocumentSource source, string target, CancellationToken ct)
    {
        using var zip = archives.Open(archive);
        await using var input = ArchiveReader.OpenMember(zip, source.EntryPath!);
        if (disk.FreeBytes(Folder) is { } free && free - input.Length < _reserveBytes)
            throw new IOException("There isn't enough free disk space to read this file from its ZIP.");
        await CopyCheckedAsync(input, source, target, ct);
    }

    async Task FetchAsync(DocumentSource source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        Track(source.FullPath, +1);
        try
        {
            await using var input = await remote!.OpenAsync(source.FullPath, ct);
            await CopyCheckedAsync(input, source, target, ct);
        }
        finally
        {
            Track(source.FullPath, -1);
        }
    }

    /// <summary>Writes <paramref name="input"/> to <paramref name="target"/>, keeping it only if it has the source's content.</summary>
    static async Task CopyCheckedAsync(Stream input, DocumentSource source, string target, CancellationToken ct)
    {
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (!ContentHash.FromBytes(hash.GetHashAndReset()).Hex.Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new SourceChangedException(source.InArchive ? "The file inside the ZIP has changed since it was read." : "The file has changed since it was read.");
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    void Hold(string name)
    {
        lock (_lock)
        {
            if (!_cleaned)
            {
                // Half-written files left by a run that stopped part way.
                _cleaned = true;
                if (Directory.Exists(Folder))
                    foreach (var temp in Directory.EnumerateFiles(Folder, "*.tmp")) TryDelete(temp);
            }
            _inUse[name] = _inUse.GetValueOrDefault(name) + 1;
        }
    }

    void Release(string name)
    {
        lock (_lock)
        {
            if (_inUse.GetValueOrDefault(name) <= 1) _inUse.Remove(name);
            else _inUse[name]--;
        }
    }

    /// <summary>Whether a library file is open through here: a book being read, or a ZIP a file is being extracted from.</summary>
    public bool IsInUse(string path)
    {
        lock (_lock) return _open.ContainsKey(path);
    }

    void Track(string path, int change)
    {
        lock (_lock)
        {
            var count = _open.GetValueOrDefault(path) + change;
            if (count <= 0) _open.Remove(path);
            else _open[path] = count;
        }
    }

    /// <summary>
    /// Deletes each copy of a remote file not in use whose documents <paramref name="doneReading"/> says are done with
    /// it, so nobody's books stay on the server longer than reading them takes. Returns how many went.
    /// </summary>
    public async Task<int> DropRemoteCopiesAsync(Func<IReadOnlyCollection<long>, Task<IReadOnlySet<long>>> doneReading)
    {
        List<(string Name, long[] Documents)> copies;
        lock (_lock) copies = [.. _remoteCopies.Select(c => (c.Key, c.Value.ToArray()))];
        if (copies.Count == 0) return 0;
        var done = await doneReading([.. copies.SelectMany(c => c.Documents).Distinct()]);
        var dropped = 0;
        foreach (var (name, documents) in copies)
        {
            if (!documents.All(done.Contains)) continue;
            lock (_lock)
            {
                if (_inUse.ContainsKey(name)) continue;
                var path = Path.Combine(Folder, name);
                if (!TryDelete(path) && File.Exists(path)) continue;
                _remoteCopies.Remove(name);
                dropped++;
            }
        }
        return dropped;
    }

    /// <summary>Deletes the least recently used files not in use until the cache is within its limit.</summary>
    void Evict()
    {
        var files = new DirectoryInfo(Folder).EnumerateFiles().Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= limitBytes) return;
            lock (_lock)
            {
                if (_inUse.ContainsKey(file.Name)) continue;
                if (!TryDelete(file.FullName)) continue;
                total -= file.Length;
                _remoteCopies.Remove(file.Name);
            }
        }
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

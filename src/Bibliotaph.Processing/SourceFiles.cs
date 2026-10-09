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

/// <summary>A file inside a ZIP no longer has the content it was hashed with: the ZIP changed, and the next scan sorts it out.</summary>
public sealed class SourceChangedException(string message) : IOException(message);

/// <summary>
/// Gives stages and the viewer a file they can open by path (F3 plan, choice 6). A loose file is itself; a file inside a
/// ZIP is extracted to the extract cache, named by its content hash, checked against that hash, and reused by every
/// reader after. Files in use are kept; once the cache passes its limit, the least recently used go.
/// </summary>
public sealed class SourceFiles(AppPaths paths, ArchiveReader archives, IDiskSpace disk, IndexingOptions? options = null, long limitBytes = SourceFiles.DefaultLimitBytes)
{
    public const long DefaultLimitBytes = 5L * 1024 * 1024 * 1024;

    readonly long _reserveBytes = (options ?? new IndexingOptions()).OnlineOnlyReserveBytes;
    readonly Lock _lock = new();
    readonly Dictionary<string, int> _inUse = [with(StringComparer.OrdinalIgnoreCase)];
    readonly ConcurrentDictionary<string, SemaphoreSlim> _extracting = new(StringComparer.OrdinalIgnoreCase);
    bool _cleaned;

    public string Folder => paths.Extract;

    /// <summary>
    /// The file to read for <paramref name="source"/>, extracting it from its ZIP when it is in one. Dispose it when
    /// done. Throws <see cref="SourceChangedException"/> when the ZIP no longer holds that content, and
    /// <see cref="IOException"/> or <see cref="InvalidDataException"/> when it can't be read.
    /// </summary>
    public async Task<LocalFile> OpenAsync(DocumentSource source, CancellationToken ct = default)
    {
        if (!source.InArchive) return new LocalFile(source.FullPath, null);
        var name = source.ContentHash.ToLowerInvariant() + Path.GetExtension(source.EntryPath!).ToLowerInvariant();
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
                else await ExtractAsync(source, target, ct);
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
        using var zip = archives.Open(source.ArchivePath!);
        await using var input = ArchiveReader.OpenMember(zip, source.EntryPath!);
        if (disk.FreeBytes(Folder) is { } free && free - input.Length < _reserveBytes)
            throw new IOException("There isn't enough free disk space to read this file from its ZIP.");

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
                throw new SourceChangedException("The file inside the ZIP has changed since it was read.");
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
                if (TryDelete(file.FullName)) total -= file.Length;
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

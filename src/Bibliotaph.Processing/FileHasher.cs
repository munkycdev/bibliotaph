using System.Security.Cryptography;
using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>Hashes a library file has before it is read: those the web library lists with its files.</summary>
public interface IKnownHashes
{
    ContentHash? Find(string path);

    /// <summary>
    /// The hash of a file listed with this size and modified time, which lets a hash known for the same file at another
    /// path (the web library's copy of a folder this PC reads itself) count only while the file is unchanged.
    /// </summary>
    ContentHash? Find(string path, long sizeBytes, DateTime modifiedUtc) => Find(path);
}

/// <summary>
/// SHA-256 of a source file's bytes: the document's identity. A file whose hash is <paramref name="known"/> isn't read
/// for it.
/// </summary>
public sealed class FileHasher(ISourceFileReader reader, IKnownHashes? known = null)
{
    public async Task<ContentHash> HashAsync(string path, CancellationToken ct = default)
    {
        if (known?.Find(path) is { } hash) return hash;
        await using var stream = reader.OpenRead(path);
        return await HashAsync(stream, ct);
    }

    /// <summary>A listed file's hash, known already or read from the file.</summary>
    public async Task<ContentHash> HashAsync(UnhashedFile file, CancellationToken ct = default)
    {
        if (known?.Find(file.FullPath, file.SizeBytes, file.ModifiedUtc) is { } hash) return hash;
        await using var stream = reader.OpenRead(file.FullPath);
        return await HashAsync(stream, ct);
    }

    /// <summary>The hash of a stream's bytes, such as a file inside a ZIP as it decompresses.</summary>
    public static async Task<ContentHash> HashAsync(Stream stream, CancellationToken ct = default) =>
        ContentHash.FromBytes(await SHA256.HashDataAsync(stream, ct));
}

/// <summary>Free space on the drive that holds a path, so online-only files stop downloading before the disk fills.</summary>
public interface IDiskSpace
{
    /// <returns>Free bytes, or null when the drive can't be asked (offline network share).</returns>
    long? FreeBytes(string path);
}

public sealed class DiskSpace : IDiskSpace
{
    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

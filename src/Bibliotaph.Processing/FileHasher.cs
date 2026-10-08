using System.Security.Cryptography;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>SHA-256 of a source file's bytes: the document's identity (A03).</summary>
public sealed class FileHasher(ISourceFileReader reader)
{
    public async Task<ContentHash> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = reader.OpenRead(path);
        return ContentHash.FromBytes(await SHA256.HashDataAsync(stream, ct));
    }
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

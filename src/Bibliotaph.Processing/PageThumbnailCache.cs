using System.Globalization;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Page thumbnails for the reader's strip in %LOCALAPPDATA%\Bibliotaph\cache\pages: a folder per book, named by its
/// content hash, holding a JPEG per page drawn so far. Derived and disposable like covers: a missing one is drawn again.
/// The cache is held to a size limit by dropping whole books, those whose thumbnails were used longest ago first.
/// </summary>
public sealed class PageThumbnailCache(AppPaths paths, long limitBytes = PageThumbnailCache.DefaultLimitBytes)
{
    /// <summary>Some tens of thousands of pages, at about 8 KB each.</summary>
    public const long DefaultLimitBytes = 256L * 1024 * 1024;

    /// <summary>Thumbnails show about 70 px wide in the strip; twice that stays sharp at 200% scaling.</summary>
    public const int Width = 144;

    /// <summary>A tall, narrow page is held to this height instead.</summary>
    public const int MaxHeight = 200;

    public string Folder { get; } = Path.Combine(paths.Cache, "pages");

    /// <summary>The pixels per PDF point that draw a page of this size at thumbnail size.</summary>
    public static double ScaleFor(double widthPts, double heightPts) =>
        Math.Min(Width / Math.Max(1, widthPts), MaxHeight / Math.Max(1, heightPts));

    string BookFolder(string contentHash) => Path.Combine(Folder, contentHash.ToLowerInvariant());

    string PathFor(string contentHash, int pageIndex) =>
        Path.Combine(BookFolder(contentHash), pageIndex.ToString(CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>A page's thumbnail, or null when it hasn't been drawn (or can't be read, which comes to the same).</summary>
    public byte[]? Read(string contentHash, int pageIndex)
    {
        try
        {
            var path = PathFor(contentHash, pageIndex);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a page's thumbnail, through a temporary file so a reader never sees half of one.</summary>
    public void Write(string contentHash, int pageIndex, byte[] jpeg)
    {
        Directory.CreateDirectory(BookFolder(contentHash));
        var target = PathFor(contentHash, pageIndex);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, jpeg);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Marks a book's thumbnails as just used, as when it opens in the reader, and makes room by dropping the books
    /// used longest ago while the cache is over its limit. The book being opened always stays.
    /// </summary>
    public void Touch(string contentHash)
    {
        var book = BookFolder(contentHash);
        if (Directory.Exists(book)) Directory.SetLastWriteTimeUtc(book, DateTime.UtcNow);
        Evict(keep: book);
    }

    /// <summary>Deletes a book's thumbnails, as "Forget its text" does. A book with none is no error.</summary>
    public void Delete(string contentHash)
    {
        var book = BookFolder(contentHash);
        if (Directory.Exists(book)) Directory.Delete(book, recursive: true);
    }

    void Evict(string keep)
    {
        if (!Directory.Exists(Folder)) return;
        var books = new DirectoryInfo(Folder).EnumerateDirectories()
            .Select(d => (Folder: d, Bytes: d.EnumerateFiles().Sum(f => f.Length)))
            .ToList();
        var total = books.Sum(b => b.Bytes);
        foreach (var (folder, bytes) in books.OrderBy(b => b.Folder.LastWriteTimeUtc))
        {
            if (total <= limitBytes) return;
            if (string.Equals(folder.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                folder.Delete(recursive: true);
                total -= bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use for a moment; the next book opened tries again.
            }
        }
    }
}

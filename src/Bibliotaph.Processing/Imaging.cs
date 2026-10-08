using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Image encoding and decoding for covers. Processing has no UI dependency; the app supplies an implementation
/// built on WPF's codecs, and tests supply a fake.
/// </summary>
public interface IImageCodec
{
    /// <summary>JPEG-encodes a BGRA, top-down bitmap.</summary>
    byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride);

    /// <summary>The pixel size of a JPG or PNG, read from its header, or null when it isn't a readable image.</summary>
    (int Width, int Height)? ReadSize(Stream image);

    /// <summary>A JPEG of the image scaled to at most <paramref name="maxWidth"/> pixels wide, or null when it can't be decoded.</summary>
    byte[]? Thumbnail(Stream image, int maxWidth);
}

/// <summary>
/// Covers in %LOCALAPPDATA%\Bibliotaph\cache\covers, named by content hash. Derived and disposable:
/// deleting the folder only means covers are made again.
/// </summary>
public sealed class CoverCache(AppPaths paths)
{
    /// <summary>Covers are drawn at about 180 px wide in the grid; twice that stays sharp at 200% scaling.</summary>
    public const int Width = 360;

    public string Folder { get; } = Path.Combine(paths.Cache, "covers");

    public static string FileName(string contentHash) => contentHash + ".jpg";

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Writes a cover through a temporary file, so a reader never sees half of one.</summary>
    public string Write(string contentHash, byte[] jpeg)
    {
        Directory.CreateDirectory(Folder);
        var name = FileName(contentHash);
        var target = PathFor(name);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, jpeg);
        File.Move(temp, target, overwrite: true);
        return name;
    }
}

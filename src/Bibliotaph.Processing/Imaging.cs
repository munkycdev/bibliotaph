using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>The pixel size of a JPG, PNG or WebP, read from its header, or null when it isn't a readable image.</summary>
    (int Width, int Height)? ReadSize(Stream image);

    /// <summary>A JPEG of the image scaled to at most <paramref name="maxWidth"/> pixels wide, or null when it can't be decoded.</summary>
    byte[]? Thumbnail(Stream image, int maxWidth);
}

/// <summary>
/// Covers in %LOCALAPPDATA%\Bibliotaph\cache\covers, named by content hash. Derived and disposable:
/// deleting the folder only means covers are made again. The covers the user chose for books owned elsewhere are the
/// exception: they're kept in <see cref="AppPaths.Covers"/>, and their names say so.
/// </summary>
public sealed class CoverCache(AppPaths paths)
{
    /// <summary>Covers are drawn at about 180 px wide in the grid; twice that stays sharp at 200% scaling.</summary>
    public const int Width = 360;

    public string Folder { get; } = Path.Combine(paths.Cache, "covers");

    public static string FileName(string contentHash) => contentHash + ".jpg";

    /// <summary>What a cover drawn from one page of a book is kept under: an adventure's first page.</summary>
    public static string PageKey(string contentHash, int pdfPage) => string.Create(CultureInfo.InvariantCulture, $"{contentHash}-p{pdfPage}");

    /// <summary>Where the covers the user chose are kept.</summary>
    public string ChosenFolder { get; } = paths.Covers;

    const string ChosenPrefix = "chosen-";

    public string PathFor(string fileName) =>
        Path.Combine(fileName.StartsWith(ChosenPrefix, StringComparison.Ordinal) ? ChosenFolder : Folder, fileName);

    /// <summary>
    /// What a book owned elsewhere's cover is kept under: its entry's sync id, which stays the same however its card
    /// changes. The cover comes from its online library, so importing again remakes it.
    /// </summary>
    public static string ElsewhereKey(Guid syncId) => string.Create(CultureInfo.InvariantCulture, $"elsewhere-{syncId:N}");

    /// <summary>
    /// The cover for a book owned elsewhere, as a name for the card, or null when it has none: one the user chose, else
    /// the one from its online library.
    /// </summary>
    public string? FindElsewhere(Guid syncId) =>
        FindChosen(syncId) ?? (FileName(ElsewhereKey(syncId)) is var name && File.Exists(PathFor(name)) ? name : null);

    /// <summary>The cover the user chose for a book owned elsewhere, or null when they haven't.</summary>
    public string? FindChosen(Guid syncId) =>
        Directory.Exists(ChosenFolder) ? Directory.EnumerateFiles(ChosenFolder, ChosenPattern(syncId)).Select(Path.GetFileName).FirstOrDefault() : null;

    /// <summary>
    /// Keeps a cover the user chose, in place of any they chose before. Its name changes with the image, so a card
    /// showing the old one loads the new one.
    /// </summary>
    public string KeepChosen(Guid syncId, byte[] jpeg)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{ChosenPrefix}{syncId:N}-{Convert.ToHexStringLower(SHA256.HashData(jpeg))[..12]}.jpg");
        WriteTo(ChosenFolder, name, jpeg);
        foreach (var old in Directory.EnumerateFiles(ChosenFolder, ChosenPattern(syncId)).Where(f => Path.GetFileName(f) != name).ToList()) File.Delete(old);
        return name;
    }

    /// <summary>Forgets the cover the user chose, so the book shows its online library's again, or none.</summary>
    public void DeleteChosen(Guid syncId)
    {
        if (!Directory.Exists(ChosenFolder)) return;
        foreach (var file in Directory.EnumerateFiles(ChosenFolder, ChosenPattern(syncId)).ToList()) File.Delete(file);
    }

    static string ChosenPattern(Guid syncId) => string.Create(CultureInfo.InvariantCulture, $"{ChosenPrefix}{syncId:N}-*.jpg");

    /// <summary>
    /// Puts back a cover kept elsewhere under its own name, as the web server does with the copies it keeps of covers
    /// its disk loses on a restart. Only a bare file name is taken, so nothing lands outside the cover folders.
    /// </summary>
    public void Restore(string fileName, byte[] jpeg)
    {
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Not a cover's file name.", nameof(fileName));
        WriteTo(fileName.StartsWith(ChosenPrefix, StringComparison.Ordinal) ? ChosenFolder : Folder, fileName, jpeg);
    }

    public string Write(string contentHash, byte[] jpeg)
    {
        var name = FileName(contentHash);
        WriteTo(Folder, name, jpeg);
        return name;
    }

    /// <summary>Writes a cover through a temporary file, so a reader never sees half of one.</summary>
    static void WriteTo(string folder, string name, byte[] jpeg)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, jpeg);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Deletes a book's cover and those drawn from its pages for the adventures in it, as "Forget its text" does. One
    /// that isn't there is no error.
    /// </summary>
    public void Delete(string contentHash)
    {
        var target = PathFor(FileName(contentHash));
        if (File.Exists(target)) File.Delete(target);
        if (!Directory.Exists(Folder)) return;
        foreach (var page in Directory.EnumerateFiles(Folder, contentHash + "-p*.jpg")) File.Delete(page);
    }
}

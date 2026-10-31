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
/// deleting the folder only means covers are made again. The covers of books owned elsewhere are the exception, as
/// nothing could draw them again: those the user chose and those imported from an online library are kept with the
/// user's data in <see cref="AppPaths.Covers"/>, never with a book's files, and their names say which they are.
/// </summary>
public sealed class CoverCache(AppPaths paths)
{
    /// <summary>Covers are drawn at about 180 px wide in the grid; twice that stays sharp at 200% scaling.</summary>
    public const int Width = 360;

    public string Folder { get; } = Path.Combine(paths.Cache, "covers");

    public static string FileName(string contentHash) => contentHash + ".jpg";

    /// <summary>What a cover drawn from one page of a book is kept under: an adventure's first page.</summary>
    public static string PageKey(string contentHash, int pdfPage) => string.Create(CultureInfo.InvariantCulture, $"{contentHash}-p{pdfPage}");

    /// <summary>Where the covers of books owned elsewhere are kept: those the user chose and those from online libraries.</summary>
    public string ChosenFolder { get; } = paths.Covers;

    const string ChosenPrefix = "chosen-";
    const string ImportedPrefix = "online-";
    const string OldImportedPrefix = "elsewhere-";

    /// <summary>Whether a cover's file name is one the user chose.</summary>
    public static bool IsChosen(string fileName) => fileName.StartsWith(ChosenPrefix, StringComparison.Ordinal);

    /// <summary>Whether a cover's file name is one kept with the user's data, which nothing could draw again: chosen or imported.</summary>
    public static bool IsKept(string fileName) => IsChosen(fileName) || fileName.StartsWith(ImportedPrefix, StringComparison.Ordinal);

    public string PathFor(string fileName) => Path.Combine(IsKept(fileName) ? ChosenFolder : Folder, fileName);

    /// <summary>
    /// The cover for a book owned elsewhere, as a name for the card, or null when it has none: one the user chose, else
    /// the one from its online library.
    /// </summary>
    public string? FindElsewhere(Guid syncId) => FindChosen(syncId) ?? FindImported(syncId);

    /// <summary>The cover the user chose for a book owned elsewhere, or null when they haven't.</summary>
    public string? FindChosen(Guid syncId) => Find(ChosenPrefix, syncId);

    /// <summary>The cover a book owned elsewhere was imported with from its online library, or null when it has none.</summary>
    public string? FindImported(Guid syncId) => Find(ImportedPrefix, syncId);

    /// <summary>
    /// Keeps a cover the user chose, in place of any they chose before. Its name changes with the image, so a card
    /// showing the old one loads the new one.
    /// </summary>
    public string KeepChosen(Guid syncId, byte[] jpeg) => Keep(ChosenPrefix, syncId, jpeg);

    /// <summary>Keeps the cover a book owned elsewhere has on its online library, in place of any it had before.</summary>
    public string KeepImported(Guid syncId, byte[] jpeg) => Keep(ImportedPrefix, syncId, jpeg);

    /// <summary>Forgets the cover the user chose, so the book shows its online library's again, or none.</summary>
    public void DeleteChosen(Guid syncId) => Delete(ChosenPrefix, syncId);

    /// <summary>Forgets the cover from a book's online library, as undoing its import does.</summary>
    public void DeleteImported(Guid syncId) => Delete(ImportedPrefix, syncId);

    /// <summary>Every cover the user chose, by the sync id of its book's entry.</summary>
    public IReadOnlyDictionary<Guid, byte[]> ReadAllChosen() => ReadAll(ChosenPrefix);

    /// <summary>Every cover imported from an online library, by the sync id of its book's entry.</summary>
    public IReadOnlyDictionary<Guid, byte[]> ReadAllImported() => ReadAll(ImportedPrefix);

    /// <summary>
    /// The sync id in the name of an imported cover as earlier versions kept it, in the disposable cache, or null for
    /// any other name.
    /// </summary>
    public static Guid? OldImported(string fileName) =>
        fileName.StartsWith(OldImportedPrefix, StringComparison.Ordinal) && fileName.EndsWith(".jpg", StringComparison.Ordinal)
        && Guid.TryParseExact(fileName.AsSpan(OldImportedPrefix.Length, fileName.Length - OldImportedPrefix.Length - 4), "N", out var syncId)
            ? syncId : null;

    /// <summary>
    /// Moves the imported covers that earlier versions kept in the disposable cache to where the user's covers are
    /// kept, so clearing the cache can't lose them again. How many moved.
    /// </summary>
    public int MoveOldImported()
    {
        if (!Directory.Exists(Folder)) return 0;
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(Folder, OldImportedPrefix + "*.jpg").ToList())
        {
            if (OldImported(Path.GetFileName(file)) is not { } syncId) continue;
            try
            {
                if (FindImported(syncId) is null) KeepImported(syncId, File.ReadAllBytes(file));
                File.Delete(file);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left where it was, it moves on the next start.
            }
        }
        return moved;
    }

    string? Find(string prefix, Guid syncId) =>
        Directory.Exists(ChosenFolder) ? Directory.EnumerateFiles(ChosenFolder, Pattern(prefix, syncId)).Select(Path.GetFileName).FirstOrDefault() : null;

    string Keep(string prefix, Guid syncId, byte[] jpeg)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{prefix}{syncId:N}-{Convert.ToHexStringLower(SHA256.HashData(jpeg))[..12]}.jpg");
        WriteTo(ChosenFolder, name, jpeg);
        foreach (var old in Directory.EnumerateFiles(ChosenFolder, Pattern(prefix, syncId)).Where(f => Path.GetFileName(f) != name).ToList()) File.Delete(old);
        return name;
    }

    void Delete(string prefix, Guid syncId)
    {
        if (!Directory.Exists(ChosenFolder)) return;
        foreach (var file in Directory.EnumerateFiles(ChosenFolder, Pattern(prefix, syncId)).ToList()) File.Delete(file);
    }

    Dictionary<Guid, byte[]> ReadAll(string prefix)
    {
        var covers = new Dictionary<Guid, byte[]>();
        if (!Directory.Exists(ChosenFolder)) return covers;
        foreach (var file in Directory.EnumerateFiles(ChosenFolder, prefix + "*.jpg"))
        {
            var name = Path.GetFileName(file);
            if (name.Length < prefix.Length + 32 || !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out var syncId)) continue;
            try
            {
                covers[syncId] = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                // Being replaced: the next pass reads the new one.
            }
        }
        return covers;
    }

    static string Pattern(string prefix, Guid syncId) => string.Create(CultureInfo.InvariantCulture, $"{prefix}{syncId:N}-*.jpg");

    /// <summary>
    /// Puts back a cover kept elsewhere under its own name, as the web server does with the copies it keeps of covers
    /// its disk loses on a restart. Only a bare file name is taken, so nothing lands outside the cover folders.
    /// </summary>
    public void Restore(string fileName, byte[] jpeg)
    {
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName || fileName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Not a cover's file name.", nameof(fileName));
        WriteTo(IsKept(fileName) ? ChosenFolder : Folder, fileName, jpeg);
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

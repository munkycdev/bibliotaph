namespace Bibliotaph.Core;

/// <summary>
/// The file types Bibliotaph indexes, and the archives it reads them from (F3). Everything else in a source folder is
/// counted and left alone.
/// </summary>
public static class SourceFormats
{
    public const string Pdf = "pdf";
    public const string Jpeg = "jpg";
    public const string Png = "png";

    /// <summary>Read through Windows' own decoder, which needs Microsoft's free WebP Image Extensions (F4 plan, choice 11).</summary>
    public const string Webp = "webp";

    /// <summary>Every image format, for "format:image" and the Images kind.</summary>
    public static IReadOnlyList<string> Images { get; } = [Jpeg, Png, Webp];

    /// <summary>The format for a file name, or null when Bibliotaph doesn't index that type.</summary>
    public static string? FromFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".jpg" or ".jpeg" => Jpeg,
        ".png" => Png,
        ".webp" => Webp,
        _ => null,
    };

    public static bool IsImage(string format) => format is Jpeg or Png or Webp;

    /// <summary>Whether a file is a ZIP, whose PDFs and images Bibliotaph reads in place.</summary>
    public static bool IsArchive(string name) => Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Paths of files inside a ZIP. A member's path reads the way File Explorer shows a ZIP's contents
/// (<c>Bundles\Coriolis Bundle.zip\Maps\Harbor.jpg</c>), so titles, folder hints and file-name search treat it like any
/// other file. It is never opened as a path: the ZIP and the member's entry name are.
/// </summary>
public static class ArchivePaths
{
    /// <summary>
    /// A member's path below its ZIP's: the entry name's folders, with "." and ".." dropped, so a name crafted to point
    /// outside the ZIP stays inside it.
    /// </summary>
    public static string MemberPath(string archivePath, string entryName)
    {
        var parts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(p => p is not ("." or ".."));
        return Path.Combine([archivePath, .. parts]);
    }

    /// <summary>A folder hint's name for a folder or a ZIP: "Coriolis Bundle.zip" reads as "Coriolis Bundle".</summary>
    public static string FolderName(string name) => SourceFormats.IsArchive(name) ? Path.GetFileNameWithoutExtension(name) : name;
}

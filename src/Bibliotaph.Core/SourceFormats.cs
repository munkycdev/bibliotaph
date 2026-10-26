namespace Bibliotaph.Core;

/// <summary>
/// The file types Bibliotaph indexes, and the archives it reads them from. Everything else in a source folder is
/// counted and left alone.
/// </summary>
public static class SourceFormats
{
    public const string Pdf = "pdf";

    /// <summary>An e-book whose text flows to fit the window; Bibliotaph cuts it into pages of its own (see the Ebooks project).</summary>
    public const string Epub = "epub";

    /// <summary>A Mobipocket book (.mobi, .prc), which may also hold a KF8 version; read like an EPUB.</summary>
    public const string Mobi = "mobi";

    /// <summary>A Kindle book: Mobipocket inside, or KFX, which Bibliotaph can't read.</summary>
    public const string Azw = "azw";

    /// <summary>A Kindle KF8 book.</summary>
    public const string Azw3 = "azw3";

    /// <summary>A Kindle KFX book, which Bibliotaph lists but can't read.</summary>
    public const string Kfx = "kfx";
    public const string Jpeg = "jpg";
    public const string Png = "png";

    /// <summary>Read through Windows' own decoder, which needs Microsoft's free WebP Image Extensions.</summary>
    public const string Webp = "webp";

    /// <summary>Every image format, for "format:image" and the Images kind.</summary>
    public static IReadOnlyList<string> Images { get; } = [Jpeg, Png, Webp];

    /// <summary>Every Kindle format, for "format:kindle".</summary>
    public static IReadOnlyList<string> Kindle { get; } = [Mobi, Azw, Azw3, Kfx];

    /// <summary>Every e-book format, for "format:ebook".</summary>
    public static IReadOnlyList<string> Ebooks { get; } = [Epub, .. Kindle];

    /// <summary>The formats with pages of text: what the library calls books, and what search, notes and binders point into.</summary>
    public static IReadOnlyList<string> Paged { get; } = [Pdf, .. Ebooks];

    /// <summary>The format for a file name, or null when Bibliotaph doesn't index that type.</summary>
    public static string? FromFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => Pdf,
        ".epub" => Epub,
        ".mobi" or ".prc" => Mobi,
        ".azw" => Azw,
        ".azw3" => Azw3,
        ".kfx" => Kfx,
        ".jpg" or ".jpeg" => Jpeg,
        ".png" => Png,
        ".webp" => Webp,
        _ => null,
    };

    public static bool IsImage(string format) => format is Jpeg or Png or Webp;

    public static bool IsEbook(string format) => format is Epub or Mobi or Azw or Azw3 or Kfx;

    /// <summary>Whether a format has pages of text, as a PDF or an e-book does.</summary>
    public static bool HasPages(string format) => format is Pdf || IsEbook(format);

    /// <summary>
    /// Whether a file is a ZIP, whose PDFs, e-books and images Bibliotaph reads in place. An EPUB is a ZIP too, but is
    /// read as a book, never listed as a ZIP of files.
    /// </summary>
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

/// <summary>
/// Why a file in a ZIP isn't read, the same wherever the ZIP is read: in a library folder, or by Check a download in
/// the Windows app or the browser.
/// </summary>
public static class ArchiveProblems
{
    /// <summary>A member bigger than this isn't read: the extract cache could never hold it, nor a browser's memory.</summary>
    public const long MaxMemberBytes = 5L * 1024 * 1024 * 1024;

    public const string Nested = "A ZIP inside a ZIP isn't read. Unpack it to include its files.";
    public const string Encrypted = "It is password-protected inside the ZIP.";
    public const string TooBig = "It is too big to read from inside a ZIP (over 5 GB). Unpack it to include it.";
    public const string Damaged = "It is damaged inside the ZIP.";

    /// <summary>A ZIP whose list of files can't be read.</summary>
    public const string Unreadable = "This ZIP couldn't be read; it may be damaged or still downloading.";
}

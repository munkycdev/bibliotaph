namespace Bibliotaph.Api.Contracts;

/// <summary>
/// One file of a download the browser checks, POST /api/library/check: its name and where it is in the folder or ZIP
/// picked, its SHA-256, and for a PDF what its pages gave. The file never leaves the browser: only these do.
/// </summary>
/// <param name="Name">The file's name, without its folders.</param>
/// <param name="Location">Where it is, for the results: the folder picked and the folders below it, or the ZIP and its own.</param>
/// <param name="RelativePath">Where it is below the folder picked, with '/', which folder names are read from as Windows reads them.</param>
/// <param name="Hash">Its SHA-256 as lowercase hex; null when it couldn't be read.</param>
/// <param name="Problem">Why it couldn't be read, such as a ZIP inside a ZIP.</param>
/// <param name="Pages">
/// For a PDF, what its pages gave; null for any other file, and for a PDF sent first by its hash alone, so pages are
/// only read when the file itself isn't in the library.
/// </param>
public sealed record CheckFile(string Name, string Location, string RelativePath, string? Hash, string? Problem, CheckPages? Pages);

/// <summary>
/// A PDF's pages as the browser read them: each page's fingerprint (PageFingerprints), how many pages had text, and its
/// own information. <paramref name="Unread"/> is "password" or "invalid" when its pages couldn't be read.
/// </summary>
public sealed record CheckPages(IReadOnlyList<string?> Fingerprints, int PagesWithText, string? Unread = null, string? Title = null,
    string? Author = null, string? Subject = null, string? Keywords = null);

/// <summary>
/// A file's verdict, with the heading and the meaning of its group, the line under its name, and the book it matched.
/// <paramref name="Order"/> sorts the groups, New first, as the Windows page does. <paramref name="Owned"/> is the very
/// same file, whose pages needn't be read.
/// </summary>
public sealed record CheckResult(int Order, string Label, string Explanation, string? Detail, long? MatchEntryId, long? MatchDocumentId,
    string? MatchTitle, bool Owned = false);

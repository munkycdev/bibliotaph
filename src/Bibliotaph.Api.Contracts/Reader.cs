using System.Globalization;

namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A book opened in the reader, as GET /api/library/books/{id}/reader gives it: what the reader shows around the
/// pages, and where its file is. The file itself is read straight from the user's storage through
/// <see cref="ReaderLink"/>; the server never passes a book's bytes on.
/// </summary>
/// <param name="EntryId">The card opened.</param>
/// <param name="DocumentId">The file shown, which for a pack is one of its files.</param>
/// <param name="Title">The book's title; for an adventure inside a file, its own and the file's.</param>
/// <param name="Subtitle">"PDF · 320 pages · Adventures / Winter", under the title.</param>
/// <param name="Format">The file's format, such as pdf.</param>
/// <param name="ContentHash">The file's SHA-256, which names it in the browser's cache, so a cached copy is never stale.</param>
/// <param name="Size">The file's length in bytes.</param>
/// <param name="Labels">Each page's printed label, or null where the file has none.</param>
/// <param name="PageCount">How many pages the file has.</param>
/// <param name="StartPage">The page to open at: a search hit's, an adventure's first, or the one the book was left at.</param>
/// <param name="Part">The adventure being read, when the book is an adventure inside a file.</param>
/// <param name="Marks">The words of the search the book was opened from, to mark on the page it opens at.</param>
/// <param name="Favorite">Whether the card is a favorite, for the reader's heart.</param>
/// <param name="Problem">Why the book can't be read here, in place of its pages.</param>
/// <param name="Notice">A line that clears itself, such as "Back at page 42, where you left off."</param>
public sealed record ReaderBook(long EntryId, long DocumentId, string Title, string Subtitle, string Format, string ContentHash, long Size,
    IReadOnlyList<string?> Labels, int PageCount, int StartPage, ReaderPart? Part, IReadOnlyList<string> Marks, bool Favorite,
    ReaderProblem? Problem = null, string? Notice = null)
{
    /// <summary>The file's contents, as the Windows reader's contents panel lists them.</summary>
    public IReadOnlyList<ReaderOutline> Outline { get; init; } = [];

    /// <summary>An ordinary read, whose page is kept as the reader moves, so the book opens there next time.</summary>
    public bool KeepsPlace { get; init; }
}

/// <summary>One line of a file's contents.</summary>
/// <param name="Title">The line as the file names it.</param>
/// <param name="Page">The PDF page index it goes to.</param>
/// <param name="Depth">How far it is indented, from 0.</param>
/// <param name="PageText">The page as the page box shows it.</param>
/// <param name="InPart">Whether the line is inside the adventure being read, which the panel shows as such.</param>
/// <param name="Found">What the AI found reading the chapter it starts: "Found inside: Creatures: Goblin".</param>
public sealed record ReaderOutline(string Title, int Page, int Depth, string PageText, bool InPart, string? Found = null);

/// <summary>An adventure's pages in its file, as PDF page indexes, and how far into them the reader is.</summary>
public sealed record ReaderPart(int FirstPage, int LastPage)
{
    public bool Contains(int page) => page >= FirstPage && page <= LastPage;

    /// <summary>"page 3 of 19 of this adventure", or that the page in view is outside it.</summary>
    public string Position(int page) =>
        Contains(page)
            ? $"page {(page - FirstPage + 1).ToString("N0", CultureInfo.CurrentCulture)} of {(LastPage - FirstPage + 1).ToString("N0", CultureInfo.CurrentCulture)} of this adventure"
            : "outside this adventure";
}

/// <summary>Why a book can't be read, as the Windows reader says it in place of the pages.</summary>
public sealed record ReaderProblem(string Title, string Message);

/// <summary>Where the browser reads the file from now: a short-lived link to it in the user's storage.</summary>
public sealed record ReaderLink(string? Url, ReaderProblem? Problem = null);

/// <summary>Find in the book: every page with a match, in page order, and how many matches each holds.</summary>
public sealed record ReaderFind(IReadOnlyList<ReaderFindPage> Pages);

public sealed record ReaderFindPage(int Page, int Count);

/// <summary>The page a book was left at, kept so it opens there next time.</summary>
public sealed record ReaderPosition(int Page);

/// <summary>
/// Page numbers as a reader types and reads them: the printed label where the file has one, else the PDF page. The
/// web reader's copy of <c>PageNumbers</c> in Core, which a test keeps the same.
/// </summary>
public static class ReaderPageNumbers
{
    /// <summary>
    /// The PDF page index for what was typed in the page box: a printed label first ("xii", "42"), then a PDF page
    /// number. Null when neither matches.
    /// </summary>
    public static int? Find(string entry, IReadOnlyList<string?> labels, int pageCount)
    {
        entry = entry.Trim();
        if (entry.Length == 0) return null;
        for (var i = 0; i < Math.Min(labels.Count, pageCount); i++)
            if (string.Equals(labels[i]?.Trim(), entry, StringComparison.OrdinalIgnoreCase)) return i;
        return int.TryParse(entry, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) && number >= 1 && number <= pageCount
            ? number - 1
            : null;
    }

    /// <summary>What the page box shows for a page: its printed label, or its PDF page number.</summary>
    public static string Display(int index, IReadOnlyList<string?> labels) =>
        index < labels.Count && labels[index] is { Length: > 0 } label ? label.Trim() : (index + 1).ToString(CultureInfo.CurrentCulture);
}

using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Index;

/// <summary>
/// The words a library card, a matching page and the results around them show, kept here so the Windows app and the
/// web client say the same thing about the same book.
/// </summary>
public static class CardText
{
    /// <summary>
    /// "D&amp;D 5e · Adventure" once there is metadata, otherwise "PDF · 320 pages" or "PNG image"; with "still being
    /// read" until its text is searchable, or why it won't be by itself. A pack a search found by one of its images
    /// names that image. An adventure inside a file says which file and pages; a file holding adventures says how many.
    /// A file in a pack on a card of its own says which pack.
    /// </summary>
    public static string Meta(LibraryEntry entry) =>
        entry.MatchedMember ?? entry.PartLine ?? PackLine(entry) ?? (Catalogued(entry) ?? Describe(entry)) + PartsNote(entry) + TextNote(entry);

    /// <summary>"in Dungeon Maps", for a file in a pack on a card of its own; null for any other card.</summary>
    public static string? PackLine(LibraryEntry entry) => entry.IsPackFile ? $"in {entry.PackTitle ?? "its pack"}" : null;

    /// <summary>
    /// The publisher, or the folders when there isn't one, under the title in the list; for a pack a search found by an
    /// image, that image's name.
    /// </summary>
    public static string Byline(LibraryEntry entry) => entry.MatchedMember ?? entry.PartLine ?? PackLine(entry) ?? entry.Publisher ?? entry.FolderHint ?? "";

    /// <summary>
    /// Pages for a PDF, the format for an image, "120 images" for a pack ("6 files" with PDFs in it), "Print · Foundry
    /// VTT" for a book owned elsewhere.
    /// </summary>
    public static string Size(LibraryEntry entry) =>
        entry.IsPack ? Members(entry) : entry.IsElsewhere ? entry.AlsoOwn ?? "Elsewhere" : Pages(entry) is { Length: > 0 } pages ? pages : Format(entry);

    /// <summary>"PDF", "PNG", "PACK" or "ELSEWHERE", as the format mark on a card.</summary>
    public static string Format(LibraryEntry entry) => entry.IsPack ? "PACK" : entry.IsElsewhere ? "ELSEWHERE" : entry.Format.ToUpperInvariant();

    /// <summary>"320 pp.", or empty without a page count.</summary>
    public static string Pages(LibraryEntry entry) => entry.PageCount is { } pages ? $"{pages.ToString("N0", CultureInfo.CurrentCulture)} pp." : "";

    /// <summary>"2 copies" when the book is in more than one file, for the detail list; empty otherwise.</summary>
    public static string Copies(LibraryEntry entry) => entry.Copies > 1 ? $"{entry.Copies.ToString("N0", CultureInfo.CurrentCulture)} copies" : "";

    /// <summary>"5 adventures", for a file with adventures marked in it; empty otherwise.</summary>
    public static string Parts(LibraryEntry entry) => entry.Parts > 0 ? Adventures(entry.Parts) : "";

    /// <summary>The first letter of the title, for the placeholder cover.</summary>
    public static string Initial(string title) =>
        title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpper(c, CultureInfo.CurrentCulture).ToString() : "?";

    /// <summary>"Locked" or "Protected", for the mark on the cover; empty otherwise. A book owned elsewhere has no file to lock.</summary>
    public static string TextMark(LibraryEntry entry) => entry.IsElsewhere ? "" : entry.TextAccess switch
    {
        TextAccess.Locked => "Locked",
        TextAccess.Protected => "Protected",
        _ => "",
    };

    public static string? TextMarkTip(LibraryEntry entry) => entry.IsElsewhere ? null : entry.TextAccess switch
    {
        TextAccess.Locked => "It needs a password. Open it to enter it; its text becomes searchable once it's unlocked.",
        TextAccess.Protected => "Its protection is a scheme Bibliotaph can't open. Its details stay editable, and another app may open it.",
        _ => null,
    };

    /// <summary>
    /// The badge on a book owned elsewhere: where it is, its online place first ("D&amp;D Beyond"), else the first
    /// place it is owned ("Print"), else "Elsewhere".
    /// </summary>
    public static string Badge(LibraryEntry entry) => entry.OnlineLabel ?? entry.AlsoOwn?.Split(" · ")[0] ?? "Elsewhere";

    public static string? AiTip(LibraryEntry entry) => entry.AiModel is { } model ? $"Catalogued with AI ({model})" : null;

    public static string Adventures(int count) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "adventure" : "adventures")}";

    public static string Images(int count) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "image" : "images")}";

    public static string Files(int count) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "file" : "files")}";

    /// <summary>A pack's size: "120 images" while every file in it is an image, "6 files" once PDFs are among them.</summary>
    public static string Members(LibraryEntry pack) => pack.PdfMembers > 0 ? Files(pack.Members) : Images(pack.Members);

    public static string Describe(LibraryEntry entry) =>
        entry.IsPack ? entry.PdfMembers > 0 ? $"Pack · {Files(entry.Members)}" : $"Image pack · {Images(entry.Members)}"
        : entry.IsElsewhere ? entry.AlsoOwn is { } owned ? $"Owned elsewhere · {owned}" : "Owned elsewhere"
        : SourceFormats.IsImage(entry.Format) ? $"{entry.Format.ToUpperInvariant()} image"
        : entry.PageCount is { } pages ? $"{FormatName(entry.Format)} · {pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}"
        : FormatName(entry.Format);

    /// <summary>What a book's file is called on its card: "PDF", or an e-book's format ("EPUB").</summary>
    public static string FormatName(string format) => format.ToUpperInvariant();

    /// <summary>"Printed p. 12 · PDF page 16", or just the PDF page when the file has no labels of its own.</summary>
    public static string Where(PageHit hit)
    {
        var pdfPage = (hit.PdfPage + 1).ToString("N0", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(hit.Label) || hit.Label == pdfPage ? $"PDF page {pdfPage}" : $"Printed p. {hit.Label} · PDF page {pdfPage}";
    }

    public static string Source(PageHit hit) => hit.FromOcr ? "Read from a scan" : "Text match";

    /// <summary>"3 more pages match", when more matched than are shown; empty otherwise.</summary>
    public static string MorePages(long matching, int shown)
    {
        var more = matching - shown;
        return more <= 0 ? "" : $"{more.ToString("N0", CultureInfo.CurrentCulture)} more {(more == 1 ? "page matches" : "pages match")}";
    }

    /// <summary>"Searching by type arrives once books have metadata. (“type:adventure”)".</summary>
    public static string Issue(SearchQuery query, QueryIssue issue)
    {
        var start = Math.Clamp(issue.Start, 0, query.Text.Length);
        var token = query.Text.Substring(start, Math.Clamp(issue.Length, 0, query.Text.Length - start)).Trim();
        return token.Length == 0 ? issue.Message : $"{issue.Message} (“{token}”)";
    }

    /// <summary>How much of the library a search of the pages covers yet, while some books' text isn't searchable.</summary>
    public static string Coverage(IndexProgress progress)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = $"Searching text in {progress.Searchable.ToString("N0", culture)} of {progress.Documents.ToString("N0", culture)} documents.";
        var reading = progress.Documents - progress.Searchable - progress.Withheld;
        if (reading > 0) text += progress.Withheld == 0 ? " The rest are still being read." : $" {reading.ToString("N0", culture)} still being read.";
        if (progress.Withheld > 0)
            text += $" {progress.Withheld.ToString("N0", culture)} {(progress.Withheld == 1 ? "isn't" : "aren't")} read: locked, protected, or with their text forgotten.";
        return text;
    }

    static string PartsNote(LibraryEntry entry) => Parts(entry) is { Length: > 0 } parts ? $" · {parts}" : "";

    static string TextNote(LibraryEntry entry) => entry.IsElsewhere ? "" : entry.TextAccess switch
    {
        TextAccess.Locked => " · Locked: open it to unlock",
        TextAccess.Protected => " · Can't be read here",
        TextAccess.Forgotten => " · Text forgotten",
        _ => entry.Searchable ? "" : " · still being read",
    };

    static string? Catalogued(LibraryEntry entry) =>
        entry.System is null && entry.Kind is null ? null : string.Join(" · ", new[] { entry.System, entry.Kind }.OfType<string>());
}

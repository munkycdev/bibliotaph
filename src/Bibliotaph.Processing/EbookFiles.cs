using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Ebooks;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// E-books for the stages: the file opened read-only (or taken from its ZIP), read in this process by managed code
/// within <see cref="EbookLimits"/>, and closed again. There is no native library to contain, so unlike a PDF it needs no
/// worker process; a damaged or hostile file fails with a reason.
/// </summary>
static class EbookFiles
{
    /// <summary>
    /// The size every e-book page is recorded with: a 6 by 9 inch page in points. Its pages flow, so this is only so
    /// that nothing that reads a page's size sees zero.
    /// </summary>
    public const double PageWidth = 432, PageHeight = 648;

    /// <summary>What <paramref name="read"/> takes from the source's book, or the outcome that says why it couldn't be read.</summary>
    public static async Task<(T? Value, StageOutcome? Outcome)> ReadAsync<T>(StageServices s, DocumentSource source, Func<IEbook, CancellationToken, T> read,
        CancellationToken ct)
    {
        var (file, unreadable) = await StageHelpers.OpenFileAsync(s, source, ct);
        if (file is null) return (default, unreadable);
        await using (file)
        {
            try
            {
                await using var stream = s.Reader.OpenRead(file.Path);
                using var book = Ebook.Open(stream);
                return (read(book, ct), null);
            }
            catch (EbookException ex)
            {
                return (default, new StageOutcome.Failed(ex.Message, Retry: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (default, new StageOutcome.Failed("The file could not be read. It may be in use, or still downloading.", Retry: true));
            }
        }
    }

    /// <summary>
    /// What the package says, in the fields a PDF's document information fills, so rule hints read both the same way:
    /// the subjects and publisher go with the keywords, which are matched against the vocabulary, and the description
    /// is the subject, where levels and party sizes are looked for.
    /// </summary>
    public static DocRow WithMetadata(DocRow row, EbookMetadata metadata) => row with
    {
        MetaTitle = metadata.Title,
        MetaAuthor = metadata.Authors.Count > 0 ? string.Join("; ", metadata.Authors) : null,
        MetaSubject = metadata.Description is { Length: > 0 } description ? Shorten(description, 1000) : null,
        MetaKeywords = string.Join(", ", new[] { metadata.Publisher }.Concat(metadata.Subjects).OfType<string>()) is { Length: > 0 } keywords ? keywords : null,
    };

    static string Shorten(string text, int length)
    {
        if (text.Length <= length) return text;
        var cut = text.LastIndexOf(' ', length - 1);
        return text[..(cut > 0 ? cut : length)] + "…";
    }

    /// <summary>The pages as the index records them before their text is read.</summary>
    public static List<PageRow> Pages(EbookLayout layout) => [.. layout.Pages.Select(p => new PageRow(p.Index, p.Label, PageWidth, PageHeight))];

    public static List<OutlineRow> Outline(EbookLayout layout) => [.. layout.Outline.Select(o => new OutlineRow(o.Title, o.PageIndex, o.Depth))];

    /// <summary>The pages' text. An e-book's text is its own, never a scan, so nothing is flagged for OCR.</summary>
    public static List<PageTextRow> Text(EbookLayout layout) =>
        [.. layout.Pages.Select(p => new PageTextRow(p.Index, p.Text, p.Text.Length == 0 ? "none" : "pdf", p.Text.Length == 0 ? 0 : 1, NeedsOcr: false))];

    public static bool Is(DocumentSource source) => SourceFormats.IsEbook(source.Format);
}

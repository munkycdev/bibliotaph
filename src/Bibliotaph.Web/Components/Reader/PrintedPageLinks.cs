using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Reading;
using Bibliotaph.Web.Services;

namespace Bibliotaph.Web.Components.Reader;

/// <summary>A printed page number in a page's text, for the pages to make a link of: where it is, its page and its tooltip.</summary>
public sealed record PrintedLink(int Start, int Length, int PageIndex, string Tip);

/// <summary>
/// The printed page numbers on an index or contents page, by the Windows reader's rules. None when nothing says how
/// the book numbers its pages. How the book numbers them is asked once for each file opened.
/// </summary>
public sealed class PrintedPageLinks(ReaderClient readerApi)
{
    (long DocumentId, Task<(PrintedPages Printed, HashSet<int> Listed)?> Pages)? _printed;

    public async Task<PrintedLink[]> FindAsync(ReaderBook book, int page, string text)
    {
        if (_printed is not { } known || known.DocumentId != book.DocumentId) _printed = known = (book.DocumentId, PrintedPagesAsync(book.DocumentId));
        if (await known.Pages is not { } pages) return [];
        return [.. PageReferences.Find(text, pages.Printed, pages.Listed.Contains(page)).Select(r => new PrintedLink(r.Range.Start, r.Range.Length,
            r.PageIndex, PageReferences.Tip(r.Printed, r.PageIndex, r.PageIndex < book.Labels.Count ? book.Labels[r.PageIndex] : null)))];
    }

    async Task<(PrintedPages, HashSet<int>)?> PrintedPagesAsync(long documentId)
    {
        try
        {
            var found = await readerApi.PrintedPagesAsync(documentId);
            var source = found.FromLabels ? PrintedPagesSource.Labels : PrintedPagesSource.Folios;
            return (PrintedPages.FromTable(source, found.Pages), [.. found.Listed]);
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            // No links are guessed; the page's own still work.
            return null;
        }
    }
}

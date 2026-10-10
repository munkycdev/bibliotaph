using Bibliotaph.Core.Reading;

namespace Bibliotaph.Viewer;

/// <summary>
/// A link on a PDF page: its box in PDF points, and where it goes, a page of the same book (with the spot on it, in
/// points from the page's bottom, when the link names one) or a web address.
/// </summary>
public sealed record PageLink(PageRect Box, int PageIndex, double? Top, string? Uri)
{
    public bool IsWeb => Uri is not null;
}

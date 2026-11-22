namespace Bibliotaph.Core.Reading;

/// <summary>Where a search hit is: its page and the rectangles of its words in PDF points.</summary>
public sealed record PageHighlight(int PageIndex, IReadOnlyList<PageRect> Rects);

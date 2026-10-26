using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Where the reader opens a book: read/{entry}, with the file, the page and the search's words when they're given.</summary>
public static class ReaderUrl
{
    /// <summary>A card's book. A file in a pack shown on a card of its own opens at that file; any other card, at its own.</summary>
    public static string For(BookCard card, bool popout = false) =>
        For(card.EntryId, card.Id != card.EntryId.ToString(CultureInfo.InvariantCulture) && card.DocumentId > 0 ? card.DocumentId : null, popout: popout);

    /// <summary>A matching page, with the search's words marked on it.</summary>
    public static string For(BookCard card, PageMatch hit, string? q, bool popout = false) =>
        For(card.EntryId, hit.DocumentId > 0 ? hit.DocumentId : null, hit.PdfPage, q, popout: popout);

    public static string For(long entryId, long? documentId = null, int? page = null, string? q = null, bool keep = false, bool popout = false,
        long? item = null)
    {
        var query = new List<string>();
        if (item is { } binderItem) query.Add("item=" + binderItem.ToString(CultureInfo.InvariantCulture));
        if (documentId is { } doc) query.Add("doc=" + doc.ToString(CultureInfo.InvariantCulture));
        if (page is { } at) query.Add("page=" + at.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(q)) query.Add("q=" + Uri.EscapeDataString(q));
        if (keep) query.Add("keep=true");
        if (popout) query.Add("popout=true");
        return "read/" + entryId.ToString(CultureInfo.InvariantCulture) + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }
}

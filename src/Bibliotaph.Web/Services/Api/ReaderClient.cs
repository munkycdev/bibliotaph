using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>The reader: the book open, its link, passwords, Find, the page kept, and the Notes panel.</summary>
public sealed class ReaderClient(ApiHttp http)
{
    /// <summary>A book to read: the card's own file, or <paramref name="documentId"/> in a pack; at a page, or with a search's words.</summary>
    public Task<ReaderBook> ReaderAsync(long entryId, long? documentId, int? page, string? q, bool keep, long? item = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (item is { } binderItem) query.Add($"item={binderItem.ToString(CultureInfo.InvariantCulture)}");
        if (documentId is { } doc) query.Add($"doc={doc.ToString(CultureInfo.InvariantCulture)}");
        if (page is { } at) query.Add($"page={at.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(q)) query.Add($"q={Uri.EscapeDataString(q)}");
        if (keep) query.Add("keep=true");
        var url = $"{Book(entryId)}/reader" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        return http.SendAsync<ReaderBook>(HttpMethod.Get, url, null, ct);
    }

    public Task<ReaderLink> ReaderLinkAsync(long documentId, CancellationToken ct = default) =>
        http.SendAsync<ReaderLink>(HttpMethod.Get, $"{Document(documentId)}/link", null, ct);

    /// <summary>The password remembered for a locked PDF, tried before the reader asks.</summary>
    public Task<ReaderPassword> RememberedPasswordAsync(long documentId, CancellationToken ct = default) =>
        http.SendAsync<ReaderPassword>(HttpMethod.Get, $"{Document(documentId)}/password", null, ct);

    /// <summary>A password that opened the book, for indexing to read it with and, when asked, to remember.</summary>
    public Task UsePasswordAsync(long documentId, ReaderPasswordUse use, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Post, $"{Document(documentId)}/password", use, ct);

    /// <summary>Forgets a remembered password that no longer opens the book.</summary>
    public Task ForgetPasswordAsync(long documentId, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"{Document(documentId)}/password", null, ct);

    /// <summary>Settings › Passwords: the books with a remembered password.</summary>
    public Task<List<RememberedPassword>> RememberedPasswordsAsync(CancellationToken ct = default) =>
        http.SendAsync<List<RememberedPassword>>(HttpMethod.Get, "api/library/passwords", null, ct);

    public Task<List<RememberedPassword>> ForgetRememberedPasswordAsync(string contentHash, CancellationToken ct = default) =>
        http.SendAsync<List<RememberedPassword>>(HttpMethod.Delete, $"api/library/passwords/{Uri.EscapeDataString(contentHash)}", null, ct);

    public Task<List<RememberedPassword>> ForgetAllPasswordsAsync(CancellationToken ct = default) =>
        http.SendAsync<List<RememberedPassword>>(HttpMethod.Delete, "api/library/passwords", null, ct);

    /// <summary>Every page with a match, in pages <paramref name="from"/> to <paramref name="to"/> when given (an adventure's).</summary>
    public Task<ReaderFind> FindInBookAsync(long documentId, string text, int? from, int? to, CancellationToken ct = default)
    {
        var url = $"{Document(documentId)}/find?q={Uri.EscapeDataString(text)}";
        if (from is { } first) url += $"&from={first.ToString(CultureInfo.InvariantCulture)}";
        if (to is { } last) url += $"&to={last.ToString(CultureInfo.InvariantCulture)}";
        return http.SendAsync<ReaderFind>(HttpMethod.Get, url, null, ct);
    }

    public Task SavePositionAsync(long documentId, int page, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, $"{Document(documentId)}/position", new ReaderPosition(page), ct);

    /// <summary>Use this page as the cover: a page of a whole PDF, or 0 for its first page again.</summary>
    public Task<ReaderCoverUsed> UseCoverPageAsync(long documentId, int page, CancellationToken ct = default) =>
        http.SendAsync<ReaderCoverUsed>(HttpMethod.Put, $"{Document(documentId)}/cover-page", new ReaderCover(page), ct);

    /// <summary>Mark as adventure in the reader: pages of the file open there, typed as the reader names them.</summary>
    public Task<EditResult> MarkAdventureAsync(long documentId, PartEdit part, CancellationToken ct = default) =>
        http.SendAsync<EditResult>(HttpMethod.Post, $"{Document(documentId)}/parts", part, ct);

    /// <summary>How the book numbers its pages, for links made from the printed numbers on its index and contents pages.</summary>
    public Task<ReaderPrintedPages> PrintedPagesAsync(long documentId, CancellationToken ct = default) =>
        http.SendAsync<ReaderPrintedPages>(HttpMethod.Get, $"{Document(documentId)}/printed-pages", null, ct);

    /// <summary>The bookmark a page sits under, which Add pages… starts its label with.</summary>
    public Task<ReaderBookmark> BookmarkAsync(long documentId, int page, CancellationToken ct = default) =>
        http.SendAsync<ReaderBookmark>(HttpMethod.Get, $"{Document(documentId)}/bookmark?page={page.ToString(CultureInfo.InvariantCulture)}", null, ct);

    public Task<ReaderNotes> ReaderNotesAsync(long documentId, CancellationToken ct = default) =>
        http.SendAsync<ReaderNotes>(HttpMethod.Get, $"{Document(documentId)}/notes", null, ct);

    public Task<ReaderNotesChanged> AddPageNoteAsync(long documentId, PageNoteEdit note, CancellationToken ct = default) =>
        http.SendAsync<ReaderNotesChanged>(HttpMethod.Post, $"{Document(documentId)}/notes", note, ct);

    public Task<ReaderNotesChanged> UpdatePageNoteAsync(long documentId, long noteId, PageNoteEdit note, CancellationToken ct = default) =>
        http.SendAsync<ReaderNotesChanged>(HttpMethod.Put, $"{Document(documentId)}/notes/{noteId.ToString(CultureInfo.InvariantCulture)}", note, ct);

    public Task<ReaderNotesChanged> DeletePageNoteAsync(long documentId, long noteId, CancellationToken ct = default) =>
        http.SendAsync<ReaderNotesChanged>(HttpMethod.Delete, $"{Document(documentId)}/notes/{noteId.ToString(CultureInfo.InvariantCulture)}", null, ct);

    static string Book(long entryId) => $"api/library/books/{entryId.ToString(CultureInfo.InvariantCulture)}";

    static string Document(long documentId) => $"api/library/documents/{documentId.ToString(CultureInfo.InvariantCulture)}";
}

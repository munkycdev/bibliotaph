using System.Globalization;
using System.Net;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>A book's details and the changes made from them, and books owned elsewhere.</summary>
public sealed class BooksClient(ApiHttp http)
{
    /// <summary>A book's details, or null when it isn't in the library (any more).</summary>
    public Task<BookDetails?> BookAsync(long entryId, CancellationToken ct = default) =>
        http.FindAsync<BookDetails>(Book(entryId), HttpStatusCode.NotFound, ct);

    /// <summary>A value typed for a field, a list comma-separated: the details as they are now, or what's wrong with it.</summary>
    public Task<EditResult> SetFieldAsync(long entryId, string key, string text, CancellationToken ct = default) =>
        http.SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}", new FieldEdit(text), ct);

    /// <summary>Keep this, Use this, Not right or Reset to suggestion on a field's evidence; <paramref name="text"/> is the value meant.</summary>
    public Task<EditResult> FieldActionAsync(long entryId, string key, string action, string text = "", CancellationToken ct = default) =>
        http.SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}/{action}", new FieldEdit(text), ct);

    public async Task<string> ResolutionAsync(string key, string text, CancellationToken ct = default) =>
        (await http.SendAsync<Resolution>(HttpMethod.Get, $"api/library/fields/{Uri.EscapeDataString(key)}/resolution?text={Uri.EscapeDataString(text)}", null, ct)).Text;

    public Task EndCorrectionAsync(long entryId, bool decided, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Post, $"{Book(entryId)}/correction", new CorrectionEnd(decided), ct);

    public Task SetFavoriteAsync(long entryId, bool favorite, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, $"{Book(entryId)}/favorite", new SetFavorite(favorite), ct);

    public Task<BookNotes> NotesAsync(long entryId, CancellationToken ct = default) => http.SendAsync<BookNotes>(HttpMethod.Get, $"{Book(entryId)}/notes", null, ct);

    /// <summary>The book's own note, saved; the Notes tab as it is now.</summary>
    public Task<BookNotes> SetNoteAsync(long entryId, string text, CancellationToken ct = default) =>
        http.SendAsync<BookNotes>(HttpMethod.Put, $"{Book(entryId)}/notes", new FieldEdit(text), ct);

    /// <summary>Reads the file again; with <paramref name="ocr"/>, every page by OCR.</summary>
    public Task<BookDetails> ReprocessAsync(long entryId, bool ocr = false, CancellationToken ct = default) =>
        http.SendAsync<BookDetails>(HttpMethod.Post, $"{Book(entryId)}/reprocess{(ocr ? "?ocr=true" : "")}", null, ct);

    /// <summary>Forget its text: what reading the book made goes, and nothing reads it until Read it again.</summary>
    public Task<ActionResult> ForgetTextAsync(long entryId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/forget-text", null, ct);

    public Task<ActionResult> ReadAgainAsync(long entryId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/read-again", null, ct);

    public Task<EditResult> ScanAgainAsync(long entryId, CancellationToken ct = default) =>
        http.SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/scan-again", null, ct);

    public Task<EditResult> AddPartAsync(long entryId, PartEdit part, CancellationToken ct = default) =>
        http.SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/parts", part, ct);

    public Task<ActionResult> SetPartPagesAsync(long partId, PartEdit pages, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, $"{Book(partId)}/pages", pages, ct);

    public Task<ActionResult> RemovePartAsync(long partId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, $"{Book(partId)}/part", null, ct);

    /// <summary>A pack's cover: one of its files, or null for the mosaic.</summary>
    public Task<BookDetails> UsePackCoverAsync(long packId, long? member, CancellationToken ct = default) =>
        http.SendAsync<BookDetails>(HttpMethod.Put, $"{Book(packId)}/cover", new PackCover(member), ct);

    public Task<ActionResult> SplitPackAsync(long packId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Book(packId)}/split", null, ct);

    public Task<ActionResult> MakeCurrentAsync(long entryId, long documentId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(CultureInfo.InvariantCulture)}/current", null, ct);

    public Task<ActionResult> NotSameBookAsync(long entryId, long documentId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(CultureInfo.InvariantCulture)}/not-same", null, ct);

    public Task<ElsewhereChoices> ElsewhereChoicesAsync(CancellationToken ct = default) =>
        http.SendAsync<ElsewhereChoices>(HttpMethod.Get, "api/library/elsewhere/choices", null, ct);

    public Task<ElsewhereAdded> AddElsewhereAsync(ElsewhereAdd add, CancellationToken ct = default) =>
        http.SendAsync<ElsewhereAdded>(HttpMethod.Post, "api/library/elsewhere", add, ct);

    /// <summary>A picture picked in the Add dialog, read as a cover before the book is made.</summary>
    public Task<ChosenCoverRead> ReadCoverAsync(Stream picture, string contentType, CancellationToken ct = default) =>
        http.SendAsync<ChosenCoverRead>(HttpMethod.Post, "api/library/elsewhere/cover", ApiHttp.Content(picture, contentType), ct);

    public Task<BookDetails> ChooseCoverAsync(long entryId, Stream picture, string contentType, CancellationToken ct = default) =>
        http.SendAsync<BookDetails>(HttpMethod.Put, $"api/library/books/{ApiHttp.Id(entryId)}/chosen-cover", ApiHttp.Content(picture, contentType), ct);

    public Task<BookDetails> RemoveChosenCoverAsync(long entryId, CancellationToken ct = default) =>
        http.SendAsync<BookDetails>(HttpMethod.Delete, $"api/library/books/{ApiHttp.Id(entryId)}/chosen-cover", null, ct);

    public Task<ActionResult> RemoveElsewhereAsync(long entryId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, $"api/library/books/{ApiHttp.Id(entryId)}/elsewhere", null, ct);

    static string Book(long entryId) => $"api/library/books/{entryId.ToString(CultureInfo.InvariantCulture)}";
}

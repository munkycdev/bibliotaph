using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Web.Services;

/// <summary>The library's books as Home, the Library page and Summary show them, how far it is from open, and Undo.</summary>
public sealed class LibraryClient(ApiHttp http)
{
    public async Task<LibraryOverview> LibraryOverviewAsync(CancellationToken ct = default) =>
        await http.GetAsync<LibraryOverview>("api/library/summary", new LibraryOverview(0, 0), ct);

    /// <summary>The Summary page's figures, each folder named where it is in the user's storage.</summary>
    public Task<LibrarySummary> LibraryNumbersAsync(CancellationToken ct = default) =>
        http.SendAsync<LibrarySummary>(HttpMethod.Get, "api/library/numbers", null, ct);

    /// <summary>How far the library is from open; null when signed out. Answers at once, however long the opening takes.</summary>
    public Task<LibraryOpening?> LibraryOpeningAsync(CancellationToken ct = default) =>
        http.FindAsync<LibraryOpening>("api/library/opening", HttpStatusCode.Unauthorized, ct);

    public Task<LibraryStatus?> LibraryStatusAsync(CancellationToken ct = default) =>
        http.FindAsync<LibraryStatus>("api/library/status", HttpStatusCode.Unauthorized, ct);

    public Task RescanAsync(CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Post, "api/library/rescan", null, ct);

    /// <summary>What a type-ahead filter offers for what was typed, among the books the query keeps.</summary>
    public Task<List<ValueSuggestion>> SuggestAsync(string field, string text, LibraryQuery query, CancellationToken ct = default)
    {
        var filters = LibraryUrl.QueryString(query, forApi: true);
        var url = $"api/library/values/{Uri.EscapeDataString(field)}{(filters.Length == 0 ? "?" : filters + "&")}text={Uri.EscapeDataString(text)}";
        return http.SendAsync<List<ValueSuggestion>>(HttpMethod.Get, url, null, ct);
    }

    /// <summary>The search box's field guide: a field's values among the books the Library shows, most common first.</summary>
    public Task<List<GuideValue>> GuideValuesAsync(string field, CancellationToken ct = default) =>
        http.SendAsync<List<GuideValue>>(HttpMethod.Get, $"api/library/guide/{Uri.EscapeDataString(field)}", null, ct);

    public Task<LibraryResults> LibraryAsync(LibraryQuery query, CancellationToken ct = default) =>
        http.SendAsync<LibraryResults>(HttpMethod.Get, $"api/library{LibraryUrl.QueryString(query, forApi: true)}", null, ct);

    public Task AiLaterAsync(CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Post, "api/home/ai-later", null, ct);

    public async Task<HomeShelves> HomeAsync(CancellationToken ct = default) =>
        await http.GetAsync<HomeShelves>("api/home", new HomeShelves([], [], ""), ct);

    /// <summary>An Undo, or Change them, by the token the server gave for it.</summary>
    public Task<ActionResult> RunHeldAsync(string token, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/library/held/{Uri.EscapeDataString(token)}", null, ct);

    /// <summary>Check a download: one file's verdict, from its hash and, for a PDF, its pages' fingerprints.</summary>
    public Task<CheckResult> CheckAsync(CheckFile file, CancellationToken ct = default) =>
        http.SendAsync<CheckResult>(HttpMethod.Post, "api/library/check", file, ct);
}

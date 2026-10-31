using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Needs review: its suggestions, terms, overlaps and offers, and the files that need attention.</summary>
public sealed class ReviewClient(ApiHttp http)
{
    public Task<ReviewSuggestions> ReviewAsync(CancellationToken ct = default) => http.SendAsync<ReviewSuggestions>(HttpMethod.Get, "api/review", null, ct);

    public Task<ActionResult> AcceptSuggestionAsync(ReviewFieldDecision decision, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/accept", decision, ct);

    public Task<ActionResult> RejectSuggestionAsync(ReviewFieldDecision decision, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/reject", decision, ct);

    public Task<ActionResult> EditSuggestionAsync(ReviewFieldEdit edit, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/fields/edit", edit, ct);

    public Task<ReviewAcceptedAll> AcceptAllSuggestionsAsync(ReviewAcceptAll request, CancellationToken ct = default) =>
        http.SendAsync<ReviewAcceptedAll>(HttpMethod.Post, "api/review/fields/accept-all", request, ct);

    public Task<ActionResult> AddTermAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(CultureInfo.InvariantCulture)}/add", null, ct);

    public Task<ActionResult> RejectTermAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(CultureInfo.InvariantCulture)}/reject", null, ct);

    public Task<ActionResult> FileTermAsync(long id, string target, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/review/terms/{id.ToString(CultureInfo.InvariantCulture)}/map", new ReviewTermMap(target), ct);

    public Task<ActionResult> CombineOverlapAsync(string id, string stamp, long keepId, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/review/overlaps/{Uri.EscapeDataString(id)}/combine", new ReviewOverlapCombine(stamp, keepId), ct);

    public Task<ActionResult> KeepOverlapApartAsync(string id, string stamp, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/review/overlaps/{Uri.EscapeDataString(id)}/keep-apart", new ReviewOverlapKeepApart(stamp), ct);

    public Task<ActionResult> AnswerOfferAsync(ReviewOfferAnswer answer, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/answer", answer, ct);

    public Task<ActionResult> AcceptPartsAsync(ReviewPartsAccept accept, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/parts/accept", accept, ct);

    public Task<ActionResult> SetPartPagesAsync(ReviewPartPagesSet set, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/parts/pages", set, ct);

    public Task<ActionResult> UsePlaceAsync(ReviewPlaceUse use, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/review/offers/places/use", use, ct);

    public Task<ReviewFiles> AttentionAsync(CancellationToken ct = default) => http.SendAsync<ReviewFiles>(HttpMethod.Get, "api/review/files", null, ct);

    public Task<ReviewFiles> RetryAsync(IReadOnlyList<long> documents, CancellationToken ct = default) =>
        http.SendAsync<ReviewFiles>(HttpMethod.Post, "api/review/files/retry", new ReviewRetry(documents), ct);
}

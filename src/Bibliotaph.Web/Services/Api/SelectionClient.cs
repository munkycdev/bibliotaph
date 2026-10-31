using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Select mode's actions on the ticked books.</summary>
public sealed class SelectionClient(ApiHttp http)
{
    public Task<SelectionInfo> SelectionAsync(SelectionQuery query, CancellationToken ct = default) =>
        http.SendAsync<SelectionInfo>(HttpMethod.Post, "api/library/selection", query, ct);

    public Task<SameBookPreview> SameBookAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        http.SendAsync<SameBookPreview>(HttpMethod.Post, "api/library/selection/same-book", new SelectedBooks(books), ct);

    public Task<ActionResult> JoinAsync(SameBookJoin join, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/same-book/join", join, ct);

    public Task<PackTogetherPreview> PackPreviewAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        http.SendAsync<PackTogetherPreview>(HttpMethod.Post, "api/library/selection/pack", new SelectedBooks(books), ct);

    public Task<ActionResult> PackAsync(PackTogetherRequest request, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/pack/make", request, ct);

    public Task<ActionResult> RemoveAdventuresAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, "api/library/selection/remove-adventures", new SelectedBooks(books), ct);

    public Task<ExportedCsv> ExportAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        http.SendAsync<ExportedCsv>(HttpMethod.Post, "api/library/selection/export", new SelectedBooks(books), ct);

    public Task<BulkEditForm> BulkEditAsync(IReadOnlyList<long> books, CancellationToken ct = default) =>
        http.SendAsync<BulkEditForm>(HttpMethod.Post, "api/library/selection/edit", new SelectedBooks(books), ct);

    public Task<BulkReadResult> BulkReadAsync(BulkReadRequest request, CancellationToken ct = default) =>
        http.SendAsync<BulkReadResult>(HttpMethod.Post, "api/library/selection/edit/read", request, ct);

    public Task<IReadOnlyList<BulkSummaryItem>> BulkReviewAsync(BulkEditRequest request, CancellationToken ct = default) =>
        http.SendAsync<IReadOnlyList<BulkSummaryItem>>(HttpMethod.Post, "api/library/selection/edit/review", request, ct);

    public Task<BulkApplyResult> BulkApplyAsync(BulkEditRequest request, CancellationToken ct = default) =>
        http.SendAsync<BulkApplyResult>(HttpMethod.Post, "api/library/selection/edit/apply", request, ct);
}

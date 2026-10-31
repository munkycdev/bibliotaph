using System.Globalization;
using System.Net;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>The sidebar's pins, and saved searches.</summary>
public sealed class SearchesClient(ApiHttp http)
{
    public Task<PinList> PinsAsync(CancellationToken ct = default) => http.SendAsync<PinList>(HttpMethod.Get, "api/pins", null, ct);

    public Task<PinList> PinAsync(PinRef pin, CancellationToken ct = default) => http.SendAsync<PinList>(HttpMethod.Post, "api/pins", pin, ct);

    public Task<PinList> UnpinAsync(PinRef pin, CancellationToken ct = default) => http.SendAsync<PinList>(HttpMethod.Post, "api/pins/remove", pin, ct);

    public Task<PinList> MovePinAsync(PinMove move, CancellationToken ct = default) => http.SendAsync<PinList>(HttpMethod.Post, "api/pins/move", move, ct);

    public Task<List<SavedSearchInfo>> SavedSearchesAsync(CancellationToken ct = default) => http.SendAsync<List<SavedSearchInfo>>(HttpMethod.Get, "api/searches", null, ct);

    /// <summary>A saved search, or null when it has gone.</summary>
    public Task<SavedSearch?> SavedSearchAsync(long id, CancellationToken ct = default) =>
        http.FindAsync<SavedSearch>(Search(id), HttpStatusCode.NotFound, ct);

    public Task<SavedSearchCreated> SaveSearchAsync(SavedSearchSave search, CancellationToken ct = default) =>
        http.SendAsync<SavedSearchCreated>(HttpMethod.Post, "api/searches", search, ct);

    public Task<ActionResult> UpdateSearchAsync(long id, SavedSearchChange change, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, Search(id), change, ct);

    public Task<ActionResult> RenameSearchAsync(long id, string name, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, $"{Search(id)}/name", new SavedSearchRename(name), ct);

    public Task<ActionResult> DeleteSearchAsync(long id, CancellationToken ct = default) => http.SendAsync<ActionResult>(HttpMethod.Delete, Search(id), null, ct);

    static string Search(long id) => $"api/searches/{id.ToString(CultureInfo.InvariantCulture)}";
}

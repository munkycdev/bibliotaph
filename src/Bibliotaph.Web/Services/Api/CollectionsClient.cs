using System.Globalization;
using System.Net;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Collections, and the books in them.</summary>
public sealed class CollectionsClient(ApiHttp http)
{
    public Task<CollectionList> CollectionsAsync(CancellationToken ct = default) => http.SendAsync<CollectionList>(HttpMethod.Get, "api/collections", null, ct);

    /// <summary>The cards of the collections at the top, or inside <paramref name="parent"/>.</summary>
    public Task<List<CollectionCard>> CollectionCardsAsync(long? parent = null, CancellationToken ct = default) =>
        http.SendAsync<List<CollectionCard>>(HttpMethod.Get,
            parent is { } id ? $"api/collections/cards?parent={id.ToString(CultureInfo.InvariantCulture)}" : "api/collections/cards", null, ct);

    /// <summary>A collection's page, or null when it has gone.</summary>
    public Task<CollectionPage?> CollectionAsync(long id, CancellationToken ct = default) =>
        http.FindAsync<CollectionPage>(Collection(id), HttpStatusCode.NotFound, ct);

    public Task<CollectionCreated> CreateCollectionAsync(CollectionName name, CancellationToken ct = default) =>
        http.SendAsync<CollectionCreated>(HttpMethod.Post, "api/collections", name, ct);

    public Task<ActionResult> RenameCollectionAsync(long id, CollectionName name, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, Collection(id), name, ct);

    public Task<ActionResult> MoveCollectionAsync(long id, CollectionMove move, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, $"{Collection(id)}/parent", move, ct);

    public Task<ActionResult> DeleteCollectionAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, Collection(id), null, ct);

    public Task<ActionResult> AddToCollectionAsync(long id, CollectionBooks books, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Collection(id)}/books", books, ct);

    public Task<ActionResult> RemoveFromCollectionAsync(long id, CollectionBooks books, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Collection(id)}/books/remove", books, ct);

    static string Collection(long id) => $"api/collections/{id.ToString(CultureInfo.InvariantCulture)}";
}

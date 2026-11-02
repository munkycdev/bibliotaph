using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>The cloud storage connected to the library, and the library folders in it.</summary>
public sealed class StorageClient(ApiHttp http)
{
    public async Task<IReadOnlyList<StorageAccount>> StorageAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<StorageAccount>>("api/storage", [], ct);

    public async Task<IReadOnlyList<StorageProviderInfo>> StorageProvidersAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<StorageProviderInfo>>("api/storage/providers", [], ct);

    /// <summary>Connects a WebDAV folder, or throws <see cref="ApiProblemException"/> saying why it couldn't.</summary>
    public Task<StorageAccount> ConnectWebDavAsync(WebDavConnect connect, CancellationToken ct = default) =>
        http.SendAsync<StorageAccount>(HttpMethod.Post, "api/storage/webdav", connect, ct);

    public Task DisconnectStorageAsync(string id, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/storage/{Uri.EscapeDataString(id)}", null, ct);

    /// <summary>Asks Nornis to send its worlds again, which makes any world's missing collection again.</summary>
    public Task ResyncNornisAsync(string id, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Post, $"api/storage/{Uri.EscapeDataString(id)}/nornis/resync", null, ct);

    public Task<StorageFolderList> StorageFoldersAsync(string id, string path, CancellationToken ct = default) =>
        http.SendAsync<StorageFolderList>(HttpMethod.Get, $"api/storage/{Uri.EscapeDataString(id)}/folders?path={Uri.EscapeDataString(path)}", null, ct);

    public async Task<IReadOnlyList<LibraryFolder>> LibraryFoldersAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<LibraryFolder>>("api/library/folders", [], ct);

    public Task<IReadOnlyList<LibraryFolder>> AddLibraryFolderAsync(AddLibraryFolder request, CancellationToken ct = default) =>
        http.SendAsync<IReadOnlyList<LibraryFolder>>(HttpMethod.Post, "api/library/folders", request, ct);

    public Task<IReadOnlyList<LibraryFolder>> RemoveLibraryFolderAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<IReadOnlyList<LibraryFolder>>(HttpMethod.Delete, $"api/library/folders/{id}", null, ct);
}

using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Import from an online library.</summary>
public sealed class OnlineClient(ApiHttp http)
{
    public async Task<List<OnlineService>> OnlineServicesAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<OnlineService>>("api/online/services", [], ct);

    /// <summary>Reads a library page saved from a service: its preview, or why it isn't one.</summary>
    public Task<OnlinePageRead> ReadOnlinePageAsync(Stream page, CancellationToken ct = default) =>
        http.SendAsync<OnlinePageRead>(HttpMethod.Post, "api/online/pages", ApiHttp.Content(page, "text/html"), ct);

    public Task<OnlinePreview> OnlinePreviewAsync(string token, CancellationToken ct = default) =>
        http.SendAsync<OnlinePreview>(HttpMethod.Get, $"api/online/pages/{Uri.EscapeDataString(token)}", null, ct);

    /// <summary>Gives an image saved with the page, for the covers that name it.</summary>
    public Task<OnlinePreview> AddOnlineImageAsync(string token, string name, Stream image, string contentType, CancellationToken ct = default) =>
        http.SendAsync<OnlinePreview>(HttpMethod.Put, $"api/online/pages/{Uri.EscapeDataString(token)}/images/{Uri.EscapeDataString(name)}", ApiHttp.Content(image, contentType), ct);

    public Task<OnlineImported> ImportOnlineAsync(string token, OnlineImportChoice choice, CancellationToken ct = default) =>
        http.SendAsync<OnlineImported>(HttpMethod.Post, $"api/online/pages/{Uri.EscapeDataString(token)}/import", choice, ct);
}

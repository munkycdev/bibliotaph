using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Settings › AI, and AI reading.</summary>
public sealed class AiClient(ApiHttp http)
{
    public Task<AiPage> AiAsync(CancellationToken ct = default) => http.SendAsync<AiPage>(HttpMethod.Get, "api/ai", null, ct);

    public Task<AiConnection> ConnectAiAsync(AiConnect connect, CancellationToken ct = default) =>
        http.SendAsync<AiConnection>(HttpMethod.Post, "api/ai/connect", connect, ct);

    public Task<AiNote> SaveAiAsync(AiSave save, CancellationToken ct = default) => http.SendAsync<AiNote>(HttpMethod.Put, "api/ai", save, ct);

    public Task<AiNote> SwitchAiAsync(bool on, CancellationToken ct = default) => http.SendAsync<AiNote>(HttpMethod.Put, "api/ai/enabled", new AiSwitch(on), ct);

    public Task ForgetAiKeyAsync(string endpoint, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/ai/key?endpoint={Uri.EscapeDataString(endpoint)}", null, ct);

    public Task SendFolderToAiAsync(long id, bool send, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, $"api/ai/folders/{id}", new AiFolderSend(send), ct);

    public Task ScanWholeBooksAsync(bool on, CancellationToken ct = default) => http.SendAsync(HttpMethod.Put, "api/ai/scan", new AiSwitch(on), ct);

    /// <summary>"Read the library for meaning in the background".</summary>
    public Task ReadLibraryAsync(bool on, CancellationToken ct = default) => http.SendAsync(HttpMethod.Put, "api/ai/library", new AiSwitch(on), ct);

    public Task<AiNote> ReclassifyAsync(CancellationToken ct = default) => http.SendAsync<AiNote>(HttpMethod.Post, "api/ai/reclassify", null, ct);

    /// <summary>Test with a book: held open while the model reads, and cancelled with <paramref name="ct"/>.</summary>
    public Task<AiTestReport> TestAiAsync(AiTest test, CancellationToken ct = default) => http.SendAsync<AiTestReport>(HttpMethod.Post, "api/ai/test", test, ct);

    /// <summary>AI reading: the book the AI is on, its answer so far, and the last books read.</summary>
    public Task<AiReadingPage> AiReadingAsync(CancellationToken ct = default) => http.SendAsync<AiReadingPage>(HttpMethod.Get, "api/ai/reading", null, ct);
}

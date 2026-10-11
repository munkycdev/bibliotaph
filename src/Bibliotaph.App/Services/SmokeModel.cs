using Bibliotaph.Classification;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>
/// The smoke test's stand-in model server, at an address nothing else uses, so Ask this book can be driven in CI
/// without a real model: it lists a chat model and an embedding model, answers every chat request with nothing, and
/// makes vectors by hashing words, so pages that share a question's words come out closest to it. Any other address
/// gets the usual client, so the smoke test's checks of a server that isn't there still reach nothing.
/// </summary>
sealed class SmokeModel : IModelClient, IEmbeddingClient
{
    /// <summary>A name with no dot, which counts as this computer's network, so no "Send excerpts" tick is needed.</summary>
    public const string Endpoint = "http://smoke-model";

    public const string ChatModel = "smoke-chat";

    public const string EmbeddingModel = "smoke-embed";

    const int Dimensions = 64;

    static readonly SmokeModel Instance = new();

    /// <summary>The model server clients for the smoke test: this stand-in at <see cref="Endpoint"/>, the real client elsewhere.</summary>
    public static Func<string, IModelClient> ClientsFor(IApiKeyStore keys) =>
        endpoint => LocalModelClient.Tidy(endpoint) == Endpoint ? Instance : AiSettings.ClientFor(endpoint, keys);

    public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) =>
        Task.FromResult(new EndpointInfo(EndpointKind.OpenAiCompatible, null, [ChatModel, EmbeddingModel]));

    /// <summary>An empty answer: the smoke test never lets the Classify lane run against it.</summary>
    public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default) => Task.FromResult("{}");

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);

    /// <summary>Each word (after any "search_…:" prefix) adds one to a slot chosen by a stable hash of it.</summary>
    static float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (text.StartsWith("search_", StringComparison.Ordinal) && colon > 0) text = text[(colon + 2)..];
        foreach (var word in AskRetrieval.Words(text))
        {
            var hash = 17u;
            foreach (var c in word) hash = unchecked(hash * 31 + c);
            vector[hash % Dimensions] += 1;
        }
        return vector;
    }
}

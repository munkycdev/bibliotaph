using System.Collections.Concurrent;
using Bibliotaph.Classification;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Every model service a test's libraries reach: it lists its models only for the key it expects, as a hosted service
/// does, and answers each request with what the test says. It records the address and key each request came with.
/// </summary>
sealed class FakeModelService
{
    public const string Key = "sk-test-0123456789abcd";

    public ConcurrentQueue<(string Endpoint, string? Key, ModelRequest Request)> Requests { get; } = new();

    public Func<ModelRequest, string> Answer { get; set; } = _ => "{}";

    public IModelClient ClientFor(string endpoint, Func<string?> key) => new Client(this, endpoint, key);

    sealed class Client(FakeModelService service, string endpoint, Func<string?> key) : IModelClient
    {
        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) => key() == Key
            ? Task.FromResult(new EndpointInfo(EndpointKind.OpenAiCompatible, null, ["fake-mini", "fake-large"]))
            : throw new ModelEndpointException("The endpoint needs a key, or didn't accept the one saved.");

        public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (key() != Key) throw new ModelEndpointException("The endpoint needs a key, or didn't accept the one saved.");
            service.Requests.Enqueue((endpoint, key(), request));
            return Task.FromResult(service.Answer(request));
        }
    }
}

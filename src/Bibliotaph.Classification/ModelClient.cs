using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibliotaph.Classification;

/// <summary>What kind of server an endpoint turned out to be.</summary>
public enum EndpointKind
{
    /// <summary>Ollama, talked to through its own chat call so the context window can be set per request (choice 8).</summary>
    Ollama,

    /// <summary>Anything that speaks the OpenAI chat completions API: LM Studio, llama.cpp's server, vLLM and the rest.</summary>
    OpenAiCompatible,
}

/// <summary>An endpoint that answered: what it is, its version when it says, and the models it has.</summary>
public sealed record EndpointInfo(EndpointKind Kind, string? Version, IReadOnlyList<string> Models)
{
    public string Provider => Kind == EndpointKind.Ollama ? "ollama" : "openai-compatible";
}

/// <summary>One classification request: the model, the two messages, the answer's schema and the context it needs.</summary>
public sealed record ModelRequest(string Model, string System, string User, string SchemaJson, int ContextTokens);

/// <summary>
/// The endpoint couldn't be used: it isn't running, it refused the request, or it doesn't have the model. This is about
/// the endpoint, not the book, so the Classify lane waits and says so rather than failing books one by one.
/// </summary>
public sealed class ModelEndpointException : Exception
{
    public ModelEndpointException(string message, Exception? inner = null) : base(message, inner) { }

    public ModelEndpointException() { }

    public ModelEndpointException(string message) : base(message) { }
}

/// <summary>A local model server, as the classifier needs it.</summary>
public interface IModelClient
{
    /// <summary>Finds out what the endpoint is and lists its models.</summary>
    /// <exception cref="ModelEndpointException">It can't be reached or doesn't answer like a model server.</exception>
    Task<EndpointInfo> ConnectAsync(CancellationToken ct = default);

    /// <summary>Asks the model for an answer in the request's schema and returns the answer's text.</summary>
    /// <exception cref="ModelEndpointException">The endpoint can't be reached or refused the request.</exception>
    Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}

/// <summary>
/// One adapter over HttpClient and System.Text.Json, with no SDK (choice 8). It asks Ollama's own version call first;
/// an endpoint that answers it is Ollama and gets Ollama's chat call with <c>num_ctx</c> set, and anything else gets
/// OpenAI-compatible chat completions with a JSON-schema response format. It sends only what it is given: no tools,
/// no functions, nothing from any other file.
/// </summary>
public sealed class LocalModelClient : IModelClient
{
    readonly HttpClient _http;
    readonly Func<string?> _apiKey;
    EndpointKind? _kind;

    /// <param name="http">Shared; its timeout has to allow for a model loading and reading a long excerpt.</param>
    /// <param name="endpoint">As the user typed it: "http://localhost:11434", or ".../v1".</param>
    /// <param name="apiKey">Read when a request is made, so a key is never kept in this object longer than it needs.</param>
    public LocalModelClient(HttpClient http, string endpoint, Func<string?>? apiKey = null)
    {
        _http = http;
        _apiKey = apiKey ?? (() => null);
        (Root, OpenAiBase) = Normalize(endpoint);
    }

    /// <summary>The endpoint without a trailing /v1, where Ollama's own calls live.</summary>
    public Uri Root { get; }

    /// <summary>Where the OpenAI-compatible calls live: the root plus /v1.</summary>
    public Uri OpenAiBase { get; }

    /// <summary>The address in the form it is stored and shown, or null when it isn't an http(s) address.</summary>
    public static string? Tidy(string endpoint)
    {
        try
        {
            return Normalize(endpoint).Root.ToString().TrimEnd('/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static (Uri Root, Uri OpenAiBase) Normalize(string endpoint)
    {
        var text = endpoint.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("An endpoint is an http:// or https:// address.", nameof(endpoint));
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        var root = new UriBuilder(uri) { Path = path + "/", Query = "", Fragment = "" }.Uri;
        return (root, new Uri(root, "v1/"));
    }

    /// <summary>Whether the endpoint is on this computer, or on the local network, rather than somewhere on the internet.</summary>
    public static bool IsLocal(string endpoint)
    {
        Uri root;
        try
        {
            root = Normalize(endpoint).Root;
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (root.IsLoopback) return true;
        if (!IPAddress.TryParse(root.Host, out var address))
            return root.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !root.Host.Contains('.', StringComparison.Ordinal);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)
            : address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    public async Task<EndpointInfo> ConnectAsync(CancellationToken ct = default)
    {
        var ollama = await TryOllamaAsync(ct);
        if (ollama is not null)
        {
            _kind = EndpointKind.Ollama;
            return ollama;
        }

        using var response = await SendAsync(HttpMethod.Get, new Uri(OpenAiBase, "models"), null, ct);
        var json = await ReadJsonAsync(response, ct);
        var models = json?["data"] is JsonArray data
            ? data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : throw new ModelEndpointException("The endpoint answered, but not like a model server: it has no model list.");
        _kind = EndpointKind.OpenAiCompatible;
        return new EndpointInfo(EndpointKind.OpenAiCompatible, null, models);
    }

    async Task<EndpointInfo?> TryOllamaAsync(CancellationToken ct)
    {
        string? version;
        try
        {
            using var response = await _http.GetAsync(new Uri(Root, "api/version"), ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            version = json?["version"]?.GetValue<string>();
            if (version is null) return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null; // Something answered, but it isn't Ollama.
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            throw new ModelEndpointException($"Nothing is answering at {Root}. Is the model server running?", ex);
        }

        using var tags = await SendAsync(HttpMethod.Get, new Uri(Root, "api/tags"), null, ct);
        var list = await ReadJsonAsync(tags, ct);
        var models = list?["models"] is JsonArray array
            ? array.Select(m => m?["name"]?.GetValue<string>()).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new EndpointInfo(EndpointKind.Ollama, version, models);
    }

    public async Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        _kind ??= (await ConnectAsync(ct)).Kind;
        var schema = JsonNode.Parse(request.SchemaJson)!;
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.System },
            new JsonObject { ["role"] = "user", ["content"] = request.User },
        };

        if (_kind == EndpointKind.Ollama)
        {
            var body = new JsonObject
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                ["stream"] = false,
                ["format"] = schema,
                ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = request.ContextTokens },
            };
            using var response = await SendAsync(HttpMethod.Post, new Uri(Root, "api/chat"), body, ct);
            var json = await ReadJsonAsync(response, ct);
            return json?["message"]?["content"]?.GetValue<string>()
                ?? throw new ClassifierAnswerException("The model's answer was empty.");
        }

        var request1 = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["temperature"] = 0,
            ["stream"] = false,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "book_metadata", ["strict"] = true, ["schema"] = schema },
            },
        };
        HttpResponseMessage completion;
        try
        {
            completion = await SendAsync(HttpMethod.Post, new Uri(OpenAiBase, "chat/completions"), request1, ct);
        }
        catch (ModelEndpointException ex) when (ex.InnerException is HttpRequestException { StatusCode: HttpStatusCode.BadRequest })
        {
            // Some servers don't take a schema; plain JSON mode still keeps the answer to an object, and the evidence
            // check doesn't care which.
            request1 = (JsonObject)request1.DeepClone();
            request1["response_format"] = new JsonObject { ["type"] = "json_object" };
            completion = await SendAsync(HttpMethod.Post, new Uri(OpenAiBase, "chat/completions"), request1, ct);
        }
        using (completion)
        {
            var json = await ReadJsonAsync(completion, ct);
            return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                ?? throw new ClassifierAnswerException("The model's answer was empty.");
        }
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, JsonObject? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, uri);
        if (body is not null) message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (_apiKey() is { Length: > 0 } key) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, ct);
        }
        catch (Exception ex) when (Unreachable(ex, ct))
        {
            throw new ModelEndpointException($"Nothing is answering at {Root}. Is the model server running?", ex);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var detail = await ErrorDetailAsync(response, ct);
            var status = (int)response.StatusCode;
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The endpoint needs a key, or didn't accept the one saved.",
                HttpStatusCode.NotFound => "The endpoint doesn't have that model, or isn't a model server.",
                _ => $"The endpoint refused the request ({status}).",
            };
            throw new ModelEndpointException(detail is null ? reason : $"{reason} It said: {detail}",
                new HttpRequestException(detail, null, response.StatusCode));
        }
    }

    static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException ex)
        {
            throw new ModelEndpointException("The endpoint answered with something that isn't JSON.", ex);
        }
    }

    /// <summary>The server's own error message, short, for the notice.</summary>
    static async Task<string?> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var json = JsonNode.Parse(text);
            var message = json?["error"] switch
            {
                JsonValue value => value.GetValue<string>(),
                JsonObject error => error["message"]?.GetValue<string>(),
                _ => null,
            };
            return message is null ? null : message.Length > 200 ? message[..200] + "…" : message;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Connection refused, a name that doesn't resolve, or a timeout that wasn't the caller cancelling.</summary>
    static bool Unreachable(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException { StatusCode: null } || (ex is TaskCanceledException && !ct.IsCancellationRequested);
}

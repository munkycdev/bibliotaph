using Bibliotaph.Classification;

namespace Bibliotaph.Server.Ai;

/// <summary>
/// How a library reaches a model service: by address, with the key read when a request is made. <see cref="QuietPeriod"/>
/// is how long reading whole books waits after the last request a person or the catalogue made; half a minute unless
/// tests give a shorter one.
/// </summary>
public sealed record ModelClientFactory(Func<string, Func<string?>, IModelClient> Create, TimeSpan? QuietPeriod = null);

/// <summary>
/// Where the server may send a user's book excerpts: a model service on the internet, over https. Anything else would
/// let an address typed in Settings reach the server's own network or the cloud's metadata endpoints.
/// </summary>
public static class ModelServices
{
    /// <summary>A model can take minutes to read a long excerpt.</summary>
    static readonly Lazy<HttpClient> Http = new(() => new HttpClient(PublicNetwork.Handler()) { Timeout = TimeSpan.FromMinutes(10) });

    /// <summary>The client for a model service, reading its key from <paramref name="key"/> when a request is made.</summary>
    public static IModelClient ClientFor(string endpoint, Func<string?> key) => new LocalModelClient(Http.Value, endpoint, key);

    /// <summary>The address as it is kept, https:// when no scheme was typed, or null when it isn't an address.</summary>
    public static string? Tidy(string endpoint) =>
        LocalModelClient.Tidy(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "https://" + endpoint.Trim());

    /// <summary>Why the server can't use <paramref name="endpoint"/>, or null when it can try.</summary>
    public static string? Problem(string endpoint) =>
        Tidy(endpoint) is not { } tidy || !Uri.TryCreate(tidy, UriKind.Absolute, out var uri)
            ? "An address is like https://api.openai.com/v1."
            : PublicNetwork.Problem(uri, "a model service");
}

using Bibliotaph.Server.Auth;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// The "Nornis" configuration section: the audience Nornis's tokens for the Bibliotaph API carry, Nornis's web origin
/// (where browsers are sent back to) and its API's address. With any of them missing, and Auth0 sign-in not set up,
/// the server offers nothing of Nornis.
/// </summary>
public sealed class NornisOptions
{
    public const string Section = "Nornis";

    public string? Audience { get; set; }
    public string? Origin { get; set; }
    public string? ApiBase { get; set; }

    public bool IsSet =>
        Audience is { Length: > 0 }
        && Uri.TryCreate(Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
        && Uri.TryCreate(ApiBase, UriKind.Absolute, out var api) && api.Scheme == Uri.UriSchemeHttps;

    /// <summary>The section when it's complete and Auth0 sign-in is set up, else null.</summary>
    public static NornisOptions? From(IConfiguration configuration) =>
        AuthSetup.Auth0(configuration) is not null && configuration.GetSection(Section).Get<NornisOptions>() is { IsSet: true } options ? options : null;

    /// <summary>The API's address with a trailing slash, so relative paths keep its own path.</summary>
    public Uri ApiRoot => new(ApiBase!.EndsWith('/') ? ApiBase : ApiBase + "/");

    /// <summary>
    /// Whether an address is on Nornis's own site: the same scheme, host and port as <see cref="Origin"/>, so a look-alike
    /// such as https://nornis.app.example.com doesn't pass for https://nornis.app.
    /// </summary>
    public bool IsOwn(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && Uri.TryCreate(Origin, UriKind.Absolute, out var origin)
        && uri.Scheme == origin.Scheme && string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port
        && uri.UserInfo.Length == 0;
}

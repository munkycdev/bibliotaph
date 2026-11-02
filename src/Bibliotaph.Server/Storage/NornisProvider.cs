using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Nornis;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Nornis;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Files a game master uploaded to their worlds in Nornis, read through Nornis's API. Nornis signs in through the same
/// Auth0 tenant, so the grant is Auth0's, asked for with Nornis's API as the audience, and made with Bibliotaph's own
/// Auth0 application unless Storage:Nornis names another. Auth0 rotates refresh tokens, which <see cref="DriveTokens"/>
/// already keeps. Nornis decides which files are listed; Bibliotaph only reads them.
/// </summary>
public sealed class NornisProvider : OAuthStorageProvider
{
    public const string Key = "nornis";

    /// <summary>Where Nornis's grant and API are, as this server's configuration says.</summary>
    public sealed record Setup(string Auth0Domain, string Audience, Uri ApiRoot);

    readonly Setup? _setup;

    public NornisProvider() { }

    NornisProvider(Setup setup) => _setup = setup;

    public override string Id => Key;
    public override string Name => "Nornis";
    public override string Owner => "Nornis";
    protected override string ConfigurationName => "Nornis";

    public override string About => "Nornis can search and share books in your Nornis collections, and Bibliotaph reads files you uploaded to Nornis.";

    public override bool StartsInItsOwnApp => true;

    Setup Bound => _setup ?? throw new InvalidOperationException("Nornis isn't configured on this server.");

    public override string AuthorizationEndpoint => $"https://{Bound.Auth0Domain}/authorize";
    public override string TokenEndpoint => $"https://{Bound.Auth0Domain}/oauth/token";

    public override IReadOnlyList<string> Scopes { get; } = ["openid", "offline_access", "published:read"];

    public override IReadOnlyDictionary<string, string> AuthorizationParameters =>
        new Dictionary<string, string> { ["audience"] = Bound.Audience };

    protected override IReadOnlyDictionary<string, string> ScopeText { get; } = new Dictionary<string, string>
    {
        ["openid"] = Unlisted,
        ["offline_access"] = Unlisted,
        ["published:read"] = "read the books you uploaded to Nornis",
    };

    public override bool IsAvailable(IConfiguration configuration) => Read(configuration) is not null && App(configuration) is not null;

    /// <summary>Storage:Nornis's application, or Bibliotaph's own Auth0 one, which is the same tenant Nornis signs in with.</summary>
    public override StorageApp? App(IConfiguration configuration) =>
        base.App(configuration) ?? (AuthSetup.Auth0(configuration) is { } auth0 ? new StorageApp { ClientId = auth0.ClientId, ClientSecret = auth0.ClientSecret } : null);

    public override StorageProvider Bind(IConfiguration configuration) => Read(configuration) is { } setup ? new NornisProvider(setup) : this;

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var me = await new NornisClient(http, Bound.ApiRoot, _ => Task.FromResult(accessToken)).MeAsync(ct);
        return new StorageIdentity(me.UserId, me.DisplayName is { Length: > 0 } name ? $"Nornis · {name}" : "Nornis", null);
    }

    public override IStorageDrive Drive(DriveContext context) =>
        _setup is { } setup ? new NornisDrive(new NornisClient(context.Http, setup.ApiRoot, context.Credential, context.Refused), context.Clock) : new Unconfigured();

    static Setup? Read(IConfiguration configuration) =>
        NornisOptions.From(configuration) is { } nornis && AuthSetup.Auth0(configuration) is { Domain: { } domain }
            && configuration[$"Storage:Nornis:Audience"] is { Length: > 0 } audience
            ? new Setup(domain, audience, nornis.ApiRoot)
            : null;

    /// <summary>A connection kept after Nornis's settings were taken off this server: unreachable, as an offline drive is.</summary>
    sealed class Unconfigured : IStorageDrive
    {
        public string ProviderName => "Nornis";
        public bool HasReadLinks => false;

        public Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default) => throw Unreachable();
        public Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default) => throw Unreachable();
        public Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default) => throw Unreachable();
        public Task<string?> ReadLinkAsync(string path, CancellationToken ct = default) => throw Unreachable();

        static StorageException Unreachable() => new(System.Net.HttpStatusCode.ServiceUnavailable, "Nornis isn't set up on this server.");
    }
}

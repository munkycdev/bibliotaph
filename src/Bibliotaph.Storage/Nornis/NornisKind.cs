namespace Bibliotaph.Storage.Nornis;

/// <summary>
/// Files a game master uploaded to their worlds in Nornis, read through Nornis's API. Nornis signs in through the same
/// Auth0 tenant, so the grant is Auth0's, asked for with Nornis's API as the audience. Auth0 rotates refresh tokens,
/// which keeping the newest already covers. Nornis decides which files are listed; Bibliotaph only reads them. Where
/// its sign-in and API are comes from the web server's configuration, so it isn't among <see cref="StorageKinds.All"/>.
/// </summary>
public sealed class NornisKind(NornisKind.Setup? setup = null) : OAuthStorageKind
{
    public const string Key = "nornis";

    /// <summary>Where Nornis's grant and API are.</summary>
    public sealed record Setup(string Auth0Domain, string Audience, Uri ApiRoot);

    public override string Id => Key;
    public override string Name => "Nornis";
    public override string Owner => "Nornis";

    public override string About => "Nornis can search and share books in your Nornis collections, and Bibliotaph reads files you uploaded to Nornis.";

    public override bool StartsInItsOwnApp => true;

    public bool IsSetUp => setup is not null;

    Setup Bound => setup ?? throw new InvalidOperationException("Nornis isn't configured on this server.");

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

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var me = await new NornisClient(http, Bound.ApiRoot, _ => Task.FromResult(accessToken)).MeAsync(ct);
        return new StorageIdentity(me.UserId, me.DisplayName is { Length: > 0 } name ? $"Nornis · {name}" : "Nornis", null);
    }

    public override IStorageDrive Drive(DriveContext context) =>
        setup is { } bound ? new NornisDrive(new NornisClient(context.Http, bound.ApiRoot, context.Credential, context.Refused), context.Clock) : new Unconfigured();

    /// <summary>A connection kept after Nornis's settings were taken off the server: unreachable, as an offline drive is.</summary>
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

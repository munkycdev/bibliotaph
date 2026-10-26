using Bibliotaph.Storage;
using Bibliotaph.Storage.Box;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Box, through its OAuth 2 endpoints and API 2.0. Box hands out a new refresh token with every access token and the
/// old one stops working, so <see cref="DriveTokens"/> keeping the newest is what keeps the connection.
/// </summary>
public sealed class BoxProvider : OAuthStorageProvider
{
    public const string Key = "box";

    public override string Id => Key;
    public override string Name => "Box";
    public override string Owner => "Box";
    protected override string ConfigurationName => "Box";

    public override string AuthorizationEndpoint => "https://account.box.com/api/oauth2/authorize";
    public override string TokenEndpoint => "https://api.box.com/oauth2/token";

    /// <summary>Read every file and folder, and nothing that writes. Library folders can be anywhere in the account.</summary>
    public override IReadOnlyList<string> Scopes { get; } = ["root_readonly"];

    protected override IReadOnlyDictionary<string, string> ScopeText { get; } = new Dictionary<string, string>
    {
        ["root_readonly"] = "see and read your files",
    };

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var user = await new BoxClient(http, _ => Task.FromResult(accessToken)).MeAsync(ct);
        var who = user.Name is { Length: > 0 } name ? name : user.Login;
        return new StorageIdentity(user.Id, who is { Length: > 0 } ? $"Box · {who}" : "Box", null);
    }

    public override IStorageDrive Drive(DriveContext context) =>
        new BoxDrive(new BoxClient(context.Http, context.Credential, context.Clock), context.Clock);
}

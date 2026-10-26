using Bibliotaph.Storage;
using Bibliotaph.Storage.Dropbox;

namespace Bibliotaph.Server.Storage;

/// <summary>Dropbox, personal or a team's, through its OAuth 2 endpoints and API v2.</summary>
public sealed class DropboxProvider : StorageProvider
{
    public const string Key = "dropbox";

    // A team account's kind keeps the team space's root, which every call names.
    const string TeamKind = "team:";

    public override string Id => Key;
    public override string Name => "Dropbox";
    public override string Owner => "Dropbox";
    protected override string ConfigurationName => "Dropbox";

    public override string AuthorizationEndpoint => "https://www.dropbox.com/oauth2/authorize";
    public override string TokenEndpoint => "https://api.dropboxapi.com/oauth2/token";

    /// <summary>Who the account is, and read its files and folders. Nothing that writes.</summary>
    public override IReadOnlyList<string> Scopes { get; } = ["account_info.read", "files.metadata.read", "files.content.read"];

    /// <summary>Dropbox hands out a refresh token only when asked for offline access.</summary>
    public override IReadOnlyDictionary<string, string> AuthorizationParameters { get; } = new Dictionary<string, string> { ["token_access_type"] = "offline" };

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var account = await new DropboxClient(http, _ => Task.FromResult(accessToken)).CurrentAccountAsync(ct);
        return new StorageIdentity(account.Id, account.Name is { Length: > 0 } name ? $"Dropbox · {name}" : "Dropbox",
            account.TeamRoot is { } root ? TeamKind + root : "personal");
    }

    public override IStorageDrive Drive(HttpClient http, ConnectedAccount account, Func<CancellationToken, Task<string>> accessToken, TimeProvider clock)
    {
        var teamRoot = account.AccountKind is { } kind && kind.StartsWith(TeamKind, StringComparison.Ordinal) ? kind[TeamKind.Length..] : null;
        return new DropboxDrive(new DropboxClient(http, accessToken, teamRoot, clock));
    }
}

using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Nornis;
using Bibliotaph.Storage.Nornis;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Nornis on this server: its sign-in is this server's Auth0 tenant, made with Bibliotaph's own Auth0 application
/// unless Storage:Nornis names another, and its API is wherever the Nornis configuration says.
/// </summary>
public sealed class NornisProvider : OAuthStorageProvider
{
    public const string Key = NornisKind.Key;

    public NornisProvider() : base(new NornisKind(), "Nornis") { }

    NornisProvider(NornisKind.Setup setup) : base(new NornisKind(setup), "Nornis") { }

    public override bool IsAvailable(IConfiguration configuration) => Read(configuration) is not null && App(configuration) is not null;

    /// <summary>Storage:Nornis's application, or Bibliotaph's own Auth0 one, which is the same tenant Nornis signs in with.</summary>
    public override StorageApp? App(IConfiguration configuration) =>
        base.App(configuration) ?? (AuthSetup.Auth0(configuration) is { } auth0 ? new StorageApp { ClientId = auth0.ClientId, ClientSecret = auth0.ClientSecret } : null);

    public override StorageProvider Bind(IConfiguration configuration) => Read(configuration) is { } setup ? new NornisProvider(setup) : this;

    static NornisKind.Setup? Read(IConfiguration configuration) =>
        NornisOptions.From(configuration) is { } nornis && AuthSetup.Auth0(configuration) is { Domain: { } domain }
            && configuration[$"Storage:Nornis:Audience"] is { Length: > 0 } audience
            ? new NornisKind.Setup(domain, audience, nornis.ApiRoot)
            : null;
}

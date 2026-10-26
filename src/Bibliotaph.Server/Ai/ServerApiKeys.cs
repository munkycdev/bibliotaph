using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Ai;

/// <summary>
/// The keys a user gave for model services, one per address, sealed in their catalog so a copy of the catalog, such
/// as a backup, never holds a key anyone can read. A key is decrypted only when a request needs it, and never given
/// back to the browser.
/// </summary>
public sealed class ServerApiKeys(SettingsStore settings, IDataProtector protector) : IApiKeyStore
{
    readonly SealedSettings _keys = new(settings, SettingKeys.ServerAiKeys, protector);

    public Task LoadAsync(CancellationToken ct = default) => _keys.LoadAsync(ct);

    public string? Find(string endpoint) => _keys.Find(endpoint);

    /// <summary>The key's last four characters, so the page can say which key is saved without showing it.</summary>
    public string? Ending(string endpoint) => Find(endpoint) is { Length: > 0 } key ? key[^Math.Min(4, key.Length)..] : null;

    public bool Remember(string endpoint, string key)
    {
        RememberAsync(endpoint, key).GetAwaiter().GetResult();
        return true;
    }

    public void Forget(string endpoint) => ForgetAsync(endpoint).GetAwaiter().GetResult();

    public Task RememberAsync(string endpoint, string key, CancellationToken ct = default) => _keys.SetAsync(endpoint, key.Trim(), ct);

    public Task ForgetAsync(string endpoint, CancellationToken ct = default) => _keys.RemoveAsync(endpoint, ct);
}

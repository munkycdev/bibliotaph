using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Ai;

/// <summary>
/// The keys a user gave for model services, one per address, kept in their catalog encrypted with the server's Data
/// Protection keys (which Key Vault protects), so a copy of the catalog, such as a backup, never holds a key anyone can
/// read. A key is decrypted only when a request needs it, and never given back to the browser.
/// </summary>
public sealed class ServerApiKeys(SettingsStore settings, IDataProtector protector) : IApiKeyStore
{
    const string Setting = SettingKeys.ServerAiKeys;

    readonly ConcurrentDictionary<string, string> _sealed = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _sealed.Clear();
        if (await settings.GetAsync(Setting, ct) is not { Length: > 0 } json) return;
        foreach (var (endpoint, key) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []) _sealed[endpoint] = key;
    }

    public string? Find(string endpoint)
    {
        if (!_sealed.TryGetValue(endpoint, out var key)) return null;
        try
        {
            return protector.Unprotect(key);
        }
        catch (CryptographicException)
        {
            // Sealed with keys the server no longer has: as good as no key, and the user is asked for it again.
            return null;
        }
    }

    /// <summary>The key's last four characters, so the page can say which key is saved without showing it.</summary>
    public string? Ending(string endpoint) => Find(endpoint) is { Length: > 0 } key ? key[^Math.Min(4, key.Length)..] : null;

    public bool Remember(string endpoint, string key)
    {
        RememberAsync(endpoint, key).GetAwaiter().GetResult();
        return true;
    }

    public void Forget(string endpoint) => ForgetAsync(endpoint).GetAwaiter().GetResult();

    public Task RememberAsync(string endpoint, string key, CancellationToken ct = default)
    {
        _sealed[endpoint] = protector.Protect(key.Trim());
        return SaveAsync(ct);
    }

    public Task ForgetAsync(string endpoint, CancellationToken ct = default)
    {
        _sealed.TryRemove(endpoint, out _);
        return SaveAsync(ct);
    }

    Task SaveAsync(CancellationToken ct) =>
        settings.SetAsync(Setting, JsonSerializer.Serialize(_sealed.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary()), ct);
}

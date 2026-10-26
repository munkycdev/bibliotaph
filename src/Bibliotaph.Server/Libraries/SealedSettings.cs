using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Bibliotaph.Catalog;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// Secrets kept in one of a user's catalog settings, by name, each encrypted with the server's Data Protection keys
/// (which Key Vault protects), so a copy of the catalog never holds one anyone can read. A secret is decrypted only
/// when it's asked for.
/// </summary>
public sealed class SealedSettings(SettingsStore settings, string setting, IDataProtector protector)
{
    readonly ConcurrentDictionary<string, string> _sealed = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _sealed.Clear();
        if (await settings.GetAsync(setting, ct) is not { Length: > 0 } json) return;
        foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []) _sealed[name] = value;
    }

    public string? Find(string name)
    {
        if (!_sealed.TryGetValue(name, out var value)) return null;
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Sealed with keys the server no longer has: as good as none, and the user is asked again.
            return null;
        }
    }

    /// <summary>The names with a secret. Never the secrets.</summary>
    public IReadOnlyList<string> Names => [.. _sealed.Keys.Order(StringComparer.Ordinal)];

    public Task SetAsync(string name, string secret, CancellationToken ct = default)
    {
        _sealed[name] = protector.Protect(secret);
        return SaveAsync(ct);
    }

    public Task RemoveAsync(string name, CancellationToken ct = default) => _sealed.TryRemove(name, out _) ? SaveAsync(ct) : Task.CompletedTask;

    /// <summary>Removes every secret. Returns how many there were.</summary>
    public async Task<int> ClearAsync(CancellationToken ct = default)
    {
        var count = _sealed.Count;
        _sealed.Clear();
        await SaveAsync(ct);
        return count;
    }

    Task SaveAsync(CancellationToken ct) =>
        settings.SetAsync(setting, JsonSerializer.Serialize(_sealed.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary()), ct);
}

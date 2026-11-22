using System.Collections.Concurrent;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Bibliotaph.Storage.Desktop;

namespace Bibliotaph.Mac.Platform;

/// <summary>Secrets for this sitting only, for a library kept somewhere else with --data-root (see MacComposition.AddSecrets).</summary>
public sealed class MemorySecrets : IPasswordVault, IApiKeyStore, IConnectionSecrets
{
    readonly ConcurrentDictionary<string, string> _passwords = new();
    readonly ConcurrentDictionary<string, string> _keys = new();
    readonly ConcurrentDictionary<string, string> _connections = new();

    string? IPasswordVault.Find(string contentHash) => _passwords.GetValueOrDefault(contentHash);

    bool IPasswordVault.Remember(string contentHash, string password) => Replace(_passwords, contentHash, password);

    void IPasswordVault.Forget(string contentHash) => _passwords.TryRemove(contentHash, out _);

    int IPasswordVault.ForgetAll()
    {
        var count = _passwords.Count;
        _passwords.Clear();
        return count;
    }

    IReadOnlyList<string> IPasswordVault.List() => [.. _passwords.Keys];

    string? IApiKeyStore.Find(string endpoint) => _keys.GetValueOrDefault(endpoint);

    bool IApiKeyStore.Remember(string endpoint, string key) => Replace(_keys, endpoint, key);

    void IApiKeyStore.Forget(string endpoint) => _keys.TryRemove(endpoint, out _);

    string? IConnectionSecrets.Find(string connectionId) => _connections.GetValueOrDefault(connectionId);

    bool IConnectionSecrets.Remember(string connectionId, string secret) => Replace(_connections, connectionId, secret);

    void IConnectionSecrets.Forget(string connectionId) => _connections.TryRemove(connectionId, out _);

    static bool Replace(ConcurrentDictionary<string, string> secrets, string key, string value)
    {
        secrets[key] = value;
        return true;
    }
}

/// <summary>
/// The passwords indexing may open a book with: one entered this sitting with "Make its text searchable" ticked, or
/// else a remembered one.
/// </summary>
public sealed class IndexPasswords(UnlockedPasswords unlocked, IPasswordVault vault) : IPasswordStore
{
    public string? Find(string contentHash) => unlocked.FindForIndexing(contentHash) ?? vault.Find(contentHash);
}

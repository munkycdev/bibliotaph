using Bibliotaph.Processing;

namespace Bibliotaph.Presentation.Services;

/// <summary>Remembered passwords held in memory, for the smoke test, which must not write to Credential Manager.</summary>
public sealed class MemoryPasswordVault : IPasswordVault
{
    readonly Lock _lock = new();
    readonly Dictionary<string, string> _passwords = [];

    public string? Find(string contentHash)
    {
        lock (_lock) return _passwords.GetValueOrDefault(contentHash);
    }

    public bool Remember(string contentHash, string password)
    {
        lock (_lock) _passwords[contentHash] = password;
        return true;
    }

    public void Forget(string contentHash)
    {
        lock (_lock) _passwords.Remove(contentHash);
    }

    public int ForgetAll()
    {
        lock (_lock)
        {
            var count = _passwords.Count;
            _passwords.Clear();
            return count;
        }
    }

    public IReadOnlyList<string> List()
    {
        lock (_lock) return [.. _passwords.Keys];
    }
}

/// <summary>
/// The passwords indexing may open a book with: one entered this sitting with "Make its text searchable" ticked, which
/// is held in memory only, or else a remembered one.
/// </summary>
public sealed class IndexPasswords(UnlockedPasswords unlocked, IPasswordVault vault) : IPasswordStore
{
    public string? Find(string contentHash) => unlocked.FindForIndexing(contentHash) ?? vault.Find(contentHash);
}

/// <summary>Storage sign-ins in memory, for the smoke test, which connects no real storage.</summary>
public sealed class MemoryConnectionSecrets : Bibliotaph.Storage.Desktop.IConnectionSecrets
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();

    public string? Find(string connectionId) => _secrets.GetValueOrDefault(connectionId);

    public bool Remember(string connectionId, string secret)
    {
        _secrets[connectionId] = secret;
        return true;
    }

    public void Forget(string connectionId) => _secrets.TryRemove(connectionId, out _);
}

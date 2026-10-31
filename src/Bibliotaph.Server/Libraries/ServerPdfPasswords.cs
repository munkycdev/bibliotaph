using System.Collections.Concurrent;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.AspNetCore.DataProtection;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// PDF passwords on the web, by content hash: the ones the reader chose to remember,
/// sealed in the catalog where the Windows app has Credential Manager, and the ones entered with "Make its text
/// searchable" ticked, which only indexing uses and which live in memory while the library is open. Indexing reads both,
/// so a locked book is read once its password is given.
/// </summary>
public sealed class ServerPdfPasswords(SettingsStore settings, IDataProtector protector) : IPasswordStore
{
    readonly SealedSettings _remembered = new(settings, SettingKeys.ServerPdfPasswords, protector);
    readonly ConcurrentDictionary<string, string> _searchable = new(StringComparer.Ordinal);

    public Task LoadAsync(CancellationToken ct = default) => _remembered.LoadAsync(ct);

    public string? Find(string contentHash) => _searchable.GetValueOrDefault(contentHash) ?? _remembered.Find(contentHash);

    /// <summary>The password the reader chose to remember, which the browser tries before asking.</summary>
    public string? Remembered(string contentHash) => _remembered.Find(contentHash);

    /// <summary>The books with a remembered password, by content hash. Never the passwords.</summary>
    public IReadOnlyList<string> List() => _remembered.Names;

    /// <summary>
    /// Keeps a password that opened the book: for indexing while the library is open when it may make the book
    /// searchable, and in the catalog too when the reader asked to remember it.
    /// </summary>
    public async Task KeepAsync(string contentHash, string password, bool remember, bool searchable, CancellationToken ct = default)
    {
        if (searchable || remember) _searchable[contentHash] = password;
        if (remember) await _remembered.SetAsync(contentHash, password, ct);
    }

    /// <summary>Forgets a book's password, as when it no longer opens the book or the reader forgot it in Settings.</summary>
    public Task ForgetAsync(string contentHash, CancellationToken ct = default)
    {
        _searchable.TryRemove(contentHash, out _);
        return _remembered.RemoveAsync(contentHash, ct);
    }

    /// <summary>Forgets every password. Returns how many were remembered.</summary>
    public Task<int> ForgetAllAsync(CancellationToken ct = default)
    {
        _searchable.Clear();
        return _remembered.ClearAsync(ct);
    }
}

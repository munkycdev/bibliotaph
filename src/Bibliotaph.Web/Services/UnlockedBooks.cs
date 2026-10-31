namespace Bibliotaph.Web.Services;

/// <summary>
/// Passwords that opened a PDF in this tab, by content hash, so the book opening again (another file of its pack and
/// back, or from a binder) isn't asked for again. Never stored.
/// </summary>
public sealed class UnlockedBooks
{
    readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal);

    public string? Find(string contentHash) => _passwords.GetValueOrDefault(contentHash);

    public void Add(string contentHash, string password) => _passwords[contentHash] = password;

    public void Forget(string contentHash) => _passwords.Remove(contentHash);

    public void ForgetAll() => _passwords.Clear();
}

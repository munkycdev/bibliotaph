namespace Bibliotaph.Presentation.Services;

/// <summary>
/// PDF passwords the reader chose to remember, by content hash: listed, and forgotten one by one or all together, in
/// Settings, Passwords. An interface so the smoke test never touches Credential Manager.
/// </summary>
public interface IPasswordVault
{
    string? Find(string contentHash);

    /// <summary>Remembers a password that opened the book. Returns false when it couldn't be stored.</summary>
    bool Remember(string contentHash, string password);

    /// <summary>Forgets a remembered password, as when it no longer opens the book.</summary>
    void Forget(string contentHash);

    /// <summary>Forgets every remembered password. Returns how many there were.</summary>
    int ForgetAll();

    /// <summary>The content hashes of the books with a remembered password. Never the passwords.</summary>
    IReadOnlyList<string> List();
}

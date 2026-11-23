using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Bibliotaph.Storage.Desktop;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// This Mac's secrets, where Credential Manager keeps them on Windows: the key to the web library, remembered PDF
/// passwords by content hash, model endpoints' keys (the book catalogues' too) and storage sign-ins. They're in
/// <see cref="AppPaths.Secrets"/>, owner-only and kept out of Time Machine, never in Bibliotaph's databases, backups or
/// logs. Not the keychain: the data-protection keychain needs an entitlement only a provisioned, team-signed build has,
/// and the login keychain asks for its password whenever a build signed differently from the one that saved an item
/// reads it, which a local build always is. What earlier builds saved there is moved once, by <see cref="LegacyKeychain"/>.
/// </summary>
public sealed class MacSecrets(AppPaths paths, ILogger<MacSecrets> log) : IPasswordVault, IApiKeyStore, IConnectionSecrets, IWebLibraryKey, IForgetsSecrets
{
    const string DeviceKey = "web:device";
    const string PdfPrefix = "pdf:";
    const string AiPrefix = "ai:";
    const string StoragePrefix = "storage:";

    readonly Lazy<SecretFile> _file = new(() => Open(paths, log));

    SecretFile Store => _file.Value;

    /// <summary>Start over: every secret Bibliotaph saved on this Mac.</summary>
    public int ForgetEverything() => Store.ForgetAll("");

    string? IPasswordVault.Find(string contentHash) => Store.Find(PdfPrefix + contentHash);

    bool IPasswordVault.Remember(string contentHash, string password) => Store.Remember(PdfPrefix + contentHash, password);

    void IPasswordVault.Forget(string contentHash) => Store.Forget(PdfPrefix + contentHash);

    int IPasswordVault.ForgetAll() => Store.ForgetAll(PdfPrefix);

    IReadOnlyList<string> IPasswordVault.List() => [.. Store.Names(PdfPrefix).Select(n => n[PdfPrefix.Length..])];

    string? IApiKeyStore.Find(string endpoint) => Store.Find(AiPrefix + Endpoint(endpoint));

    bool IApiKeyStore.Remember(string endpoint, string key) => Store.Remember(AiPrefix + Endpoint(endpoint), key);

    void IApiKeyStore.Forget(string endpoint) => Store.Forget(AiPrefix + Endpoint(endpoint));

    string? IConnectionSecrets.Find(string connectionId) => Store.Find(StoragePrefix + connectionId);

    bool IConnectionSecrets.Remember(string connectionId, string secret) => Store.Remember(StoragePrefix + connectionId, secret);

    void IConnectionSecrets.Forget(string connectionId) => Store.Forget(StoragePrefix + connectionId);

    string? IWebLibraryKey.Find() => Store.Find(DeviceKey);

    bool IWebLibraryKey.Remember(string key) => Store.Remember(DeviceKey, key);

    void IWebLibraryKey.Forget() => Store.Forget(DeviceKey);

    static string Endpoint(string endpoint) => LocalModelClient.Tidy(endpoint) ?? endpoint;

    /// <summary>
    /// The file, made at the first start of a build that has it from whatever the keychain gives up without asking. The
    /// keychain's items go only once the file holds them, and the file's being there means the keychain is never
    /// looked at again, so a secret that couldn't be moved is asked for again (connect this Mac again, say) rather than
    /// with the keychain's password.
    /// </summary>
    static SecretFile Open(AppPaths paths, ILogger log)
    {
        var file = new SecretFile(paths.Secrets, log, KeepOutOfBackups);
        if (file.Exists) return file;
        var moved = LegacyKeychain.TakeWithoutAsking(log);
        if (file.Create(moved.Secrets))
        {
            moved.Remove();
            log.LogInformation("Saved secrets now kept in {File}; {Count} moved from the keychain", paths.Secrets, moved.Secrets.Count);
        }
        return file;

        // A Time Machine disk can be read on any Mac, and the file isn't protected there; Credential Manager's items
        // don't travel with a backup either.
        void KeepOutOfBackups(string path)
        {
            using var url = NSUrl.FromFilename(path);
            if (!url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out var error))
                log.LogWarning("Keeping the saved secrets out of Time Machine failed: {Error}", error?.LocalizedDescription);
        }
    }
}

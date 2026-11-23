using Bibliotaph.Classification;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Bibliotaph.Storage.Desktop;
using Foundation;
using Microsoft.Extensions.Logging;
using Security;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// This Mac's secrets in the login keychain, where Credential Manager keeps them on Windows: the key to the web library,
/// remembered PDF passwords by content hash, model endpoints' keys and storage sign-ins. Never in Bibliotaph's
/// databases or logs. A build that isn't signed changes signature with every build, and macOS asks once per build
/// whether it may read the items an earlier build saved.
/// </summary>
public sealed class KeychainSecrets(ILogger<KeychainSecrets> log) : IPasswordVault, IApiKeyStore, IConnectionSecrets, IWebLibraryKey, IForgetsSecrets
{
    /// <summary>Start over: every item Bibliotaph saved in the keychain.</summary>
    public int ForgetEverything()
    {
        var accounts = List("");
        foreach (var account in accounts) Delete(account);
        return accounts.Count;
    }

    const string Service = "online.bibliotaph.mac";
    const string DeviceKey = "web:device";
    const string PdfPrefix = "pdf:";
    const string AiPrefix = "ai:";
    const string StoragePrefix = "storage:";

    string? IPasswordVault.Find(string contentHash) => Read(PdfPrefix + contentHash);

    bool IPasswordVault.Remember(string contentHash, string password) => Write(PdfPrefix + contentHash, password, "A PDF password remembered by Bibliotaph");

    void IPasswordVault.Forget(string contentHash) => Delete(PdfPrefix + contentHash);

    int IPasswordVault.ForgetAll()
    {
        var accounts = List(PdfPrefix);
        foreach (var account in accounts) Delete(account);
        return accounts.Count;
    }

    IReadOnlyList<string> IPasswordVault.List() => [.. List(PdfPrefix).Select(a => a[PdfPrefix.Length..])];

    string? IApiKeyStore.Find(string endpoint) => Read(AiPrefix + Endpoint(endpoint));

    bool IApiKeyStore.Remember(string endpoint, string key) => Write(AiPrefix + Endpoint(endpoint), key, "A model endpoint's key, saved by Bibliotaph");

    void IApiKeyStore.Forget(string endpoint) => Delete(AiPrefix + Endpoint(endpoint));

    string? IConnectionSecrets.Find(string connectionId) => Read(StoragePrefix + connectionId);

    bool IConnectionSecrets.Remember(string connectionId, string secret) => Write(StoragePrefix + connectionId, secret, "A storage sign-in kept by Bibliotaph");

    void IConnectionSecrets.Forget(string connectionId) => Delete(StoragePrefix + connectionId);

    string? IWebLibraryKey.Find() => Read(DeviceKey);

    bool IWebLibraryKey.Remember(string key) => Write(DeviceKey, key, "This Mac's key to your web library, saved by Bibliotaph");

    void IWebLibraryKey.Forget() => Delete(DeviceKey);

    static string Endpoint(string endpoint) => LocalModelClient.Tidy(endpoint) ?? endpoint;

    string? Read(string account)
    {
        using var query = Query(account);
        var found = SecKeyChain.QueryAsRecord(query, out var status);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound)) log.LogWarning("Reading a keychain item failed: {Status}", status);
        return status == SecStatusCode.Success && found?.ValueData is { } data ? data.ToString(NSStringEncoding.UTF8) : null;
    }

    /// <summary>
    /// Saves a secret in place of any it replaces. Why it failed goes to the log by its status alone, so nothing logged
    /// comes from the call that was handed the secret.
    /// </summary>
    bool Write(string account, string secret, string label)
    {
        Delete(account);
        using var record = Query(account);
        record.ValueData = NSData.FromString(secret, NSStringEncoding.UTF8);
        record.Label = label;
        var status = SecKeyChain.Add(record);
        if (status == SecStatusCode.Success) return true;
        log.LogWarning("Saving a keychain item failed: {Status}", status);
        return false;
    }

    void Delete(string account)
    {
        using var query = Query(account);
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound)) log.LogWarning("Removing a keychain item failed: {Status}", status);
    }

    List<string> List(string prefix)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service };
        var found = SecKeyChain.QueryAsRecord(query, int.MaxValue, out var status);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound)) log.LogWarning("Listing keychain items failed: {Status}", status);
        return [.. (found ?? []).Select(r => r.Account).OfType<string>().Where(a => a.StartsWith(prefix, StringComparison.Ordinal))];
    }

    static SecRecord Query(string account) => new(SecKind.GenericPassword) { Service = Service, Account = account };
}

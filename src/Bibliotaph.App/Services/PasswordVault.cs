using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// PDF passwords the reader chose to remember, kept in Windows Credential Manager under this Windows account and
/// keyed by the book's content hash, so a remembered password follows the book if it moves. Indexing reads them
/// too, so a locked book is indexed once its password is remembered. Passwords never go to Bibliotaph's databases
/// or logs.
/// </summary>
public sealed class PasswordVault(ILogger<PasswordVault> log) : IPasswordStore
{
    const string TargetPrefix = "Bibliotaph:pdf:";   // the architecture doc's Bibliotaph: prefix for secrets

    public string? Find(string contentHash)
    {
        var (secret, error) = WindowsCredentials.Read(Target(contentHash));
        if (error is not null) log.LogWarning("Reading a remembered password failed: {Error}", error);
        return secret;
    }

    /// <summary>Remembers a password that opened the book. Returns false when Windows refused to store it.</summary>
    public bool Remember(string contentHash, string password)
    {
        var error = WindowsCredentials.Write(Target(contentHash), password, "A PDF password remembered by Bibliotaph");
        if (error is null) return true;
        log.LogWarning("Remembering a PDF password failed: {Error}", error);
        return false;
    }

    /// <summary>Forgets a remembered password, as when it no longer opens the book.</summary>
    public void Forget(string contentHash)
    {
        if (WindowsCredentials.Delete(Target(contentHash)) is { } error) log.LogWarning("Forgetting a PDF password failed: {Error}", error);
    }

    /// <summary>Forgets every password Bibliotaph remembered, for Start over. Returns how many there were.</summary>
    public int ForgetAll()
    {
        var (targets, listError) = WindowsCredentials.List(TargetPrefix);
        if (listError is not null) log.LogWarning("Listing remembered passwords failed: {Error}", listError);
        foreach (var target in targets)
            if (WindowsCredentials.Delete(target) is { } error) log.LogWarning("Forgetting a PDF password failed: {Error}", error);
        return targets.Count;
    }

    static string Target(string contentHash) => TargetPrefix + contentHash;
}

/// <summary>
/// API keys for model endpoints that need one, in Windows Credential Manager under this Windows account, keyed by the
/// endpoint's address. Never in catalog.db, the logs or a prompt.
/// </summary>
public sealed class ApiKeyVault(ILogger<ApiKeyVault> log) : IApiKeyStore
{
    const string TargetPrefix = "Bibliotaph:ai:";

    public string? Find(string endpoint)
    {
        var (secret, error) = WindowsCredentials.Read(Target(endpoint));
        if (error is not null) log.LogWarning("Reading an endpoint's key failed: {Error}", error);
        return secret;
    }

    public bool Remember(string endpoint, string key)
    {
        var error = WindowsCredentials.Write(Target(endpoint), key, "A model endpoint's key, saved by Bibliotaph");
        if (error is null) return true;
        log.LogWarning("Saving an endpoint's key failed: {Error}", error);
        return false;
    }

    public void Forget(string endpoint)
    {
        if (WindowsCredentials.Delete(Target(endpoint)) is { } error) log.LogWarning("Forgetting an endpoint's key failed: {Error}", error);
    }

    /// <summary>Forgets every endpoint key, for Start over. Returns how many there were.</summary>
    public int ForgetAll()
    {
        var (targets, _) = WindowsCredentials.List(TargetPrefix);
        foreach (var target in targets)
            if (WindowsCredentials.Delete(target) is { } error) log.LogWarning("Forgetting an endpoint's key failed: {Error}", error);
        return targets.Count;
    }

    static string Target(string endpoint) => TargetPrefix + (Bibliotaph.Classification.LocalModelClient.Tidy(endpoint) ?? endpoint);
}

/// <summary>Generic credentials in Windows Credential Manager, local to this machine. Errors come back as text for the log.</summary>
static class WindowsCredentials
{
    const uint GenericCredential = 1;
    const uint PersistLocalMachine = 2;
    const int NotFound = 1168;

    public static (string? Secret, string? Error) Read(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return (null, error == NotFound ? null : new Win32Exception(error).Message);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0) return (null, null);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return (secret, null);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static string? Write(string target, string secret, string comment)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "Bibliotaph",
            };
            return CredWrite(ref credential, 0) ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    /// <summary>Deletes a credential; one that isn't there is not an error.</summary>
    public static string? Delete(string target)
    {
        if (CredDelete(target, GenericCredential, 0)) return null;
        var error = Marshal.GetLastWin32Error();
        return error == NotFound ? null : new Win32Exception(error).Message;
    }

    public static (IReadOnlyList<string> Targets, string? Error) List(string prefix)
    {
        if (!CredEnumerate(prefix + "*", 0, out var count, out var list))
        {
            var error = Marshal.GetLastWin32Error();
            return ([], error == NotFound ? null : new Win32Exception(error).Message);
        }
        var targets = new List<string>((int)count);
        try
        {
            for (var i = 0; i < count; i++)
                targets.Add(Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(list, i * nint.Size)).TargetName);
        }
        finally
        {
            CredFree(list);
        }
        return (targets, null);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32", EntryPoint = "CredEnumerateW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredEnumerate(string filter, uint flags, out uint count, out nint credentials);

    [DllImport("advapi32")]
    static extern void CredFree(nint buffer);
}

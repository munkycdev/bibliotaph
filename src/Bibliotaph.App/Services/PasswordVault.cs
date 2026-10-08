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
    const string TargetPrefix = "Bibliotaph/pdf/";
    const uint GenericCredential = 1;
    const uint PersistLocalMachine = 2;
    const int NotFound = 1168;

    public string? Find(string contentHash)
    {
        if (!CredRead(Target(contentHash), GenericCredential, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) log.LogWarning("Reading a remembered password failed: {Error}", new Win32Exception(error).Message);
            return null;
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var password = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return password;
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary>Remembers a password that opened the book. Returns false when Windows refused to store it.</summary>
    public bool Remember(string contentHash, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = Target(contentHash),
                Comment = "A PDF password remembered by Bibliotaph",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "Bibliotaph",
            };
            if (CredWrite(ref credential, 0)) return true;
            log.LogWarning("Remembering a PDF password failed: {Error}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return false;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    /// <summary>Forgets a remembered password, as when it no longer opens the book.</summary>
    public void Forget(string contentHash)
    {
        if (!CredDelete(Target(contentHash), GenericCredential, 0) && Marshal.GetLastWin32Error() is var error and not NotFound)
            log.LogWarning("Forgetting a PDF password failed: {Error}", new Win32Exception(error).Message);
    }

    static string Target(string contentHash) => TargetPrefix + contentHash;

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

    [DllImport("advapi32")]
    static extern void CredFree(nint buffer);
}

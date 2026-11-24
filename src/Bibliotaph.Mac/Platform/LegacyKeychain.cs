using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Extensions.Logging;
using Security;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// The login keychain items that builds before <see cref="MacSecrets"/> saved, read once with the keychain's dialogs
/// turned off for the whole process, so an item a differently signed build saved is skipped rather than asked for with
/// the keychain's password. Asked of nothing else.
/// </summary>
static class LegacyKeychain
{
    const string Service = "online.bibliotaph.mac";

    const string Security = "/System/Library/Frameworks/Security.framework/Security";

    /// <summary>What could be read, and a way to remove every item afterwards, read or not.</summary>
    public sealed record Taken(IReadOnlyDictionary<string, string> Secrets, Action Remove);

    static readonly Taken Nothing = new(new Dictionary<string, string>(), () => { });

    /// <summary>
    /// Reads what the keychain gives without a dialog. Only when its dialogs could be turned off: otherwise nothing is
    /// read or removed, as reading could ask.
    /// </summary>
    public static Taken TakeWithoutAsking(ILogger log)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = WithoutDialogs(log, () =>
        {
            using var query = new SecRecord(SecKind.GenericPassword) { Service = Service };
            var found = SecKeyChain.QueryAsRecord(query, int.MaxValue, out var status);
            // One item a build can't read without asking can fail the whole query; then nothing moves.
            if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
                log.LogInformation("Earlier builds' keychain items couldn't be read without asking: {Status}", status);
            foreach (var record in found ?? [])
                if (record.Account is { } account && record.ValueData?.ToString(NSStringEncoding.UTF8) is { } secret) secrets[account] = secret;
        });
        if (!read) return Nothing;
        return new Taken(secrets, () => _ = WithoutDialogs(log, () =>
        {
            // SecItemDelete may take one match a call on the file-based keychain, so until none is left, or one won't go.
            for (var i = 0; i < 10_000; i++)
            {
                using var query = new SecRecord(SecKind.GenericPassword) { Service = Service };
                var status = SecKeyChain.Remove(query);
                if (status == SecStatusCode.ItemNotFound) break;
                if (status == SecStatusCode.Success) continue;
                log.LogInformation("An earlier build's keychain item was left in place: {Status}", status);
                break;
            }
        }));
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the keychain's dialogs off, putting them back after. Returns false, without
    /// running it, when they can't be turned off (SecKeychainSetUserInteractionAllowed is deprecated, so it may go).
    /// </summary>
    static bool WithoutDialogs(ILogger log, Action work)
    {
        try
        {
            if (SecKeychainGetUserInteractionAllowed(out var before) != 0 || SecKeychainSetUserInteractionAllowed(0) != 0)
            {
                log.LogInformation("The keychain's dialogs couldn't be turned off, so earlier builds' items were left alone");
                return false;
            }
            try
            {
                work();
                return true;
            }
            finally
            {
                _ = SecKeychainSetUserInteractionAllowed(before);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            log.LogInformation("The keychain's dialogs couldn't be turned off, so earlier builds' items were left alone");
            return false;
        }
    }

    // Boolean is an unsigned char in MacTypes.h.
    [DllImport(Security)]
    static extern int SecKeychainSetUserInteractionAllowed(byte state);

    [DllImport(Security)]
    static extern int SecKeychainGetUserInteractionAllowed(out byte state);
}

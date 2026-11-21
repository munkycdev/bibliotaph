using System.Collections.Concurrent;
using System.ComponentModel;
using AppKit;
using Foundation;
using Security;

namespace Bibliotaph.MacSpike;

/// <summary>
/// The whole binding engine AppKit lacks: run an update now and again whenever a property changes, on the main thread.
/// Views here live as long as the window, so nothing unsubscribes.
/// </summary>
static class Ui
{
    public static void Watch(INotifyPropertyChanged source, string property, Action update)
    {
        update();
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != property && !string.IsNullOrEmpty(e.PropertyName)) return;
            OnMain(update);
        };
    }

    public static void OnMain(Action action)
    {
        if (NSThread.IsMain) action();
        else NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
    }
}

/// <summary>The device key in the login keychain, where Credential Manager keeps it on Windows.</summary>
sealed class KeychainDeviceKey : IDeviceKey
{
    const string Service = "online.bibliotaph.macspike";
    const string Account = "device-key";

    public string? Find()
    {
        var found = SecKeyChain.QueryAsRecord(Query(), out var status);
        return status == SecStatusCode.Success && found?.ValueData is { } data ? data.ToString(NSStringEncoding.UTF8) : null;
    }

    public void Remember(string key)
    {
        Forget();
        var record = Query();
        record.ValueData = NSData.FromString(key, NSStringEncoding.UTF8);
        record.Label = "Bibliotaph device key";
        SecKeyChain.Add(record);
    }

    public void Forget() => SecKeyChain.Remove(Query());

    static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };
}

/// <summary>
/// Covers by content hash: memory, then ~/Library/Caches, then bibliotaph.online, a few at a time so scrolling a big
/// library doesn't fire thousands of requests at once.
/// </summary>
sealed class Covers(WebShelfClient web)
{
    readonly ConcurrentDictionary<string, NSImage?> _memory = new();
    readonly SemaphoreSlim _gate = new(6);
    readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Caches", "online.bibliotaph.macspike", "covers");

    public NSImage? Cached(string hash) => _memory.TryGetValue(hash, out var image) ? image : null;

    public async Task<NSImage?> GetAsync(string hash)
    {
        if (_memory.TryGetValue(hash, out var known)) return known;
        var bytes = await ReadAsync(hash).ConfigureAwait(false);
        var image = bytes is null ? null : new NSImage(NSData.FromArray(bytes));
        _memory[hash] = image;
        return image;
    }

    async Task<byte[]?> ReadAsync(string hash)
    {
        var file = Path.Combine(_folder, hash + ".jpg");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await web.ReadCoverAsync(hash).ConfigureAwait(false);
            if (bytes is null) return null;
            Directory.CreateDirectory(_folder);
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            return bytes;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

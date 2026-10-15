using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Site icons for the badge on books owned online, decoded once per site and frozen. The "Show site icons" setting
/// (Settings > Library) turns them off, and then nothing is fetched; cards show a plain mark instead.
/// <see cref="Changed"/> tells the pages to show or hide them on the cards already made.
/// </summary>
public sealed class SiteIconImages(SiteIcons icons, SettingsStore settings, ILogger<SiteIconImages> log)
{
    /// <summary>Badges are 14 px; the icon frame nearest twice that stays sharp at 200%.</summary>
    const int Size = 28;

    readonly Lock _lock = new();
    readonly Dictionary<string, ImageSource?> _byHost = [];
    Task<bool>? _isOn;

    /// <summary>Raised on the UI thread when the setting changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether site icons show: the setting, read once.</summary>
    public Task<bool> IsOnAsync() => _isOn ??= ReadAsync();

    async Task<bool> ReadAsync()
    {
        try
        {
            return await settings.GetAsync(SettingKeys.SiteIcons) != bool.FalseString;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the site icons setting failed");
            return true;
        }
    }

    public async Task SetOnAsync(bool on)
    {
        await settings.SetAsync(SettingKeys.SiteIcons, on.ToString());
        _isOn = Task.FromResult(on);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets what was decoded for a site, after the in-app browser kept its icon, so the next card shows it.</summary>
    public void Kept(string host)
    {
        lock (_lock) _byHost.Remove(host);
    }

    /// <summary>An icon already decoded for the site a link is on, so a recycled card can show it at once.</summary>
    public ImageSource? TryGet(string url)
    {
        if (SiteIcons.HostOf(url) is not { } host) return null;
        lock (_lock) return _byHost.GetValueOrDefault(host);
    }

    /// <summary>The icon for the site a link is on, fetching it the first time; null when icons are off or the site has none.</summary>
    public async Task<ImageSource?> GetAsync(string url)
    {
        if (!await IsOnAsync() || SiteIcons.HostOf(url) is not { } host) return null;
        lock (_lock)
        {
            if (_byHost.TryGetValue(host, out var known)) return known;
        }
        var image = await Task.Run(async () => await icons.GetAsync(url) is { } path ? Decode(path) : null);
        lock (_lock) _byHost[host] = image;
        return image;
    }

    /// <summary>The frame of the icon nearest <see cref="Size"/> pixels wide, frozen; null when it can't be decoded.</summary>
    BitmapFrame? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - Size)).ThenByDescending(f => f.PixelWidth).FirstOrDefault();
            frame?.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            log.LogInformation("The site icon {Path} couldn't be shown: {Message}", path, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// The smoke test's stand-in for the web, for site icons: every site answers with the same tiny picture, so a card
/// shows an icon and nothing leaves the PC.
/// </summary>
sealed class SmokeSiteIcons : System.Net.Http.HttpMessageHandler
{
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(Png) });
}

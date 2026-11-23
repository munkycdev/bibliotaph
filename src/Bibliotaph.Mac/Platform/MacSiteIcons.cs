using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Site icons for the badge on books owned online, as on Windows: each site's own icon, fetched once and kept, decoded
/// once per site. The "Show site icons" setting (Settings > Online) turns them off, and then nothing is fetched and
/// cards show a plain cloud. <see cref="Changed"/> tells the cards already made.
/// </summary>
public sealed class MacSiteIcons(SiteIcons icons, SettingsStore settings, ILogger<MacSiteIcons> log)
{
    readonly Lock _lock = new();
    readonly Dictionary<string, NSImage?> _byHost = [];
    Task<bool>? _isOn;

    /// <summary>Raised when the setting changes.</summary>
    public event EventHandler? Changed;

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

    /// <summary>The icon for the site a link is on, fetching it the first time; null when icons are off or the site has none.</summary>
    public async Task<NSImage?> GetAsync(string url)
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

    /// <summary>The icon kept for a site; null when it can't be decoded.</summary>
    NSImage? Decode(string path)
    {
        try
        {
            return new NSImage(path);
        }
        catch (Exception ex)
        {
            log.LogInformation("The site icon {Path} couldn't be shown: {Message}", path, ex.Message);
            return null;
        }
    }
}

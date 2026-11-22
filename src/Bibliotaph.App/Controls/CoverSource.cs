using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.Services;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Fills an Image from a cover's name in the cover cache, or from a site's address with its icon, so view models hold
/// names rather than WPF images. A cover decoded already shows at once, as a recycled grid cell needs; otherwise it
/// loads, and shows only if the Image still wants that name by then. Site icons follow "Show site icons" as it changes.
/// </summary>
public static class CoverSource
{
    static CoverImages? _covers;

    /// <summary>The app's cover cache, set once at startup, before any window shows.</summary>
    public static void Use(CoverImages covers) => _covers = covers;

    /// <summary>The cover's name in the cover cache.</summary>
    public static readonly DependencyProperty CoverProperty = DependencyProperty.RegisterAttached(
        "Cover", typeof(string), typeof(CoverSource), new PropertyMetadata(null, (d, _) => ShowCover((Image)d)));

    public static string? GetCover(Image image) => (string?)image.GetValue(CoverProperty);

    public static void SetCover(Image image, string? value) => image.SetValue(CoverProperty, value);

    /// <summary>An address whose site's icon to show.</summary>
    public static readonly DependencyProperty SiteProperty = DependencyProperty.RegisterAttached(
        "Site", typeof(string), typeof(CoverSource), new PropertyMetadata(null, (d, _) => ShowSiteIcon((Image)d)));

    public static string? GetSite(Image image) => (string?)image.GetValue(SiteProperty);

    public static void SetSite(Image image, string? value) => image.SetValue(SiteProperty, value);

    // The Image's handler for site icon changes. The weak event holds it only weakly, so the Image keeps it alive.
    static readonly DependencyProperty SitesWatcherProperty = DependencyProperty.RegisterAttached(
        "SitesWatcher", typeof(EventHandler<EventArgs>), typeof(CoverSource), new PropertyMetadata(null));

    static void ShowCover(Image image)
    {
        var name = GetCover(image);
        image.Source = name is null ? null : _covers?.TryGet(name);
        if (name is null || image.Source is not null || _covers is not { } covers) return;
        LoadCoverAsync(image, name, covers).FireAndForget();
    }

    static async Task LoadCoverAsync(Image image, string name, CoverImages covers)
    {
        var loaded = await covers.LoadAsync(name);
        if (loaded is not null && GetCover(image) == name) image.Source = loaded;
    }

    static void ShowSiteIcon(Image image)
    {
        var site = GetSite(image);
        if (_covers?.Sites is not { } sites)
        {
            image.Source = null;
            return;
        }
        if (image.GetValue(SitesWatcherProperty) is null)
        {
            // Switching icons on or off in Settings shows or hides them on the cards already made; weakly, since cells
            // come and go as the grid scrolls.
            EventHandler<EventArgs> watcher = (_, _) => ShowSiteIcon(image);
            image.SetValue(SitesWatcherProperty, watcher);
            WeakEventManager<SiteIconImages, EventArgs>.AddHandler(sites, nameof(SiteIconImages.Changed), watcher);
        }
        image.Source = site is null ? null : sites.TryGet(site);
        if (site is null || image.Source is not null) return;
        LoadSiteIconAsync(image, site, sites).FireAndForget();
    }

    static async Task LoadSiteIconAsync(Image image, string site, SiteIconImages sites)
    {
        var icon = await sites.GetAsync(site);
        if (icon is not null && GetSite(image) == site) image.Source = icon;
    }
}

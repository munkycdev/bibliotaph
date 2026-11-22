using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.Composition;

public static partial class DesktopComposition
{
    /// <summary>
    /// This PC's connection to bibliotaph.online, and the web library's folders and copies. The host adds the device key's
    /// store (<see cref="IWebLibraryKey"/>); a test passes a stand-in for the site.
    /// </summary>
    public static IServiceCollection AddWebLibrary(this IServiceCollection services, HttpMessageHandler? web = null)
    {
        // Each request sets how long it waits (WebLibrary.Patience), since a lookup can wait its turn far longer than the rest.
        services.AddSingleton(sp => new WebLibrary(
            web is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(web),
            sp.GetRequiredService<IWebLibraryKey>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WebLibrary>>()));
        services.AddSingleton<IWebBooks>(sp => sp.GetRequiredService<WebLibrary>());
        // The web library's folders list from its list of files, and their books are read through it, never from a disk.
        services.AddSingleton<WebShelfCache>();
        services.AddSingleton<IKnownHashes>(sp => sp.GetRequiredService<WebShelfCache>());
        // Storage connected on this PC lists through its drives; the web library's folders from its list of files.
        services.AddSingleton<ISourceLister>(sp => new Bibliotaph.Storage.Desktop.StorageAwareSourceLister(
            new Bibliotaph.Storage.StorageSourceLister(sp.GetRequiredService<Bibliotaph.Storage.IStorageDrives>()),
            new WebAwareSourceLister(new FileSystemSourceLister(), sp.GetRequiredService<WebShelfCache>())));
        services.AddSingleton<WebLibraryCopies>();
        services.AddSingleton<IKeptCopies>(sp => sp.GetRequiredService<WebLibraryCopies>());
        services.AddSingleton<WebReadings>();
        services.AddSingleton<ICloudFiles, CloudFiles>();
        services.AddSingleton<OnlineOnlyReturns>();
        return services;
    }
}

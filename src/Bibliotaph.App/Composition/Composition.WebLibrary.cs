using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>This PC's connection to bibliotaph.online, and the web library's folders and copies.</summary>
    public static IServiceCollection AddWebLibrary(this IServiceCollection services, bool smokeTest)
    {
        // This PC's connection to bibliotaph.online; the smoke test's key stays in memory.
        if (smokeTest) services.AddSingleton<IWebLibraryKey, MemoryWebLibraryKey>();
        else services.AddSingleton<IWebLibraryKey, WebLibraryKeyVault>();
        // The smoke test's bibliotaph.online is a stand-in, which it connects to only to show the web library's books. Each
        // request sets how long it waits (WebLibrary.Patience), since a lookup can wait its turn far longer than the rest.
        services.AddSingleton(sp => new WebLibrary(
            smokeTest ? new System.Net.Http.HttpClient(new SmokeWebLibrary()) : new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IWebLibraryKey>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WebLibrary>>()));
        services.AddSingleton<IWebBooks>(sp => sp.GetRequiredService<WebLibrary>());
        // The web library's folders list from its list of files, and their books are read through it, never from a disk.
        services.AddSingleton<WebShelfCache>();
        services.AddSingleton<IKnownHashes>(sp => sp.GetRequiredService<WebShelfCache>());
        services.AddSingleton<ISourceLister>(sp => new WebAwareSourceLister(new FileSystemSourceLister(), sp.GetRequiredService<WebShelfCache>()));
        services.AddSingleton<WebLibraryCopies>();
        services.AddSingleton<IKeptCopies>(sp => sp.GetRequiredService<WebLibraryCopies>());
        services.AddSingleton<WebReadings>();
        services.AddSingleton<ICloudFiles, CloudFiles>();
        services.AddSingleton<OnlineOnlyReturns>();
        return services;
    }
}

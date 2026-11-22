using Bibliotaph.Core;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.Composition;

public static partial class DesktopComposition
{
    /// <summary>
    /// What the pages share, and other apps. The host adds the window's own services: theme, dialogs, file picker, prompts,
    /// clipboard, cover and icon images, thumbnails and updates.
    /// </summary>
    public static IServiceCollection AddShell(this IServiceCollection services, bool dev, bool smokeTest, HttpMessageHandler? siteIcons = null)
    {
        // Settings > Start over, shown only with --dev.
        services.AddSingleton(new DevMode(dev));
        services.AddSingleton<LibrarySorts>();
        services.AddSingleton<LibraryPackView>();
        services.AddSingleton<SettingsLinks>();
        services.AddSingleton<LibraryFolders>();
        services.AddSingleton<LibraryActivity>(); // creates its timer on the UI thread, where the shell resolves it
        services.AddSingleton<SearchState>();
        services.AddSingleton<LibraryAskScope>();
        // Site icons for books owned online, fetched once per site; a test passes a stand-in for the web.
        services.AddSingleton(sp => new SiteIcons(sp.GetRequiredService<AppPaths>(),
            siteIcons is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : new HttpClient(siteIcons),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SiteIcons>>()));
        services.AddSingleton<OnlineBooks>();
        // The smoke test opens no other app.
        if (smokeTest) services.AddSingleton<IShellLauncher, SmokeShellLauncher>();
        else services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<OtherApps>();
        services.AddSingleton<BookTextActions>();
        return services;
    }
}

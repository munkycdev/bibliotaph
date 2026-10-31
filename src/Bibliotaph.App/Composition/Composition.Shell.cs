using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>The window's services: theme, dialogs, the clipboard and other apps, and what the pages share.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services, bool dev, bool smokeTest)
    {
        // Settings > Start over, shown only with --dev.
        services.AddSingleton(new DevMode(dev));
        // Shell
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LibrarySorts>();
        services.AddSingleton<LibraryPackView>();
        services.AddSingleton<IFilePicker, WindowsFilePicker>();
        services.AddSingleton<IUserPrompts, WpfUserPrompts>();
        services.AddSingleton<IClipboard, WpfClipboard>();
        services.AddSingleton<IDesktopShell, WindowsDesktopShell>();
        services.AddSingleton<SettingsLinks>();
        services.AddSingleton<LibraryFolders>();
        services.AddSingleton<LibraryActivity>(); // creates its timer on the UI thread, where the shell resolves it
        services.AddSingleton<SearchState>();
        services.AddSingleton<LibraryAskScope>();
        services.AddSingleton<CoverImages>();
        // Site icons for books owned online, fetched once per site; the smoke test has a stand-in for the web.
        services.AddSingleton(sp => new SiteIcons(sp.GetRequiredService<AppPaths>(),
            smokeTest ? new System.Net.Http.HttpClient(new SmokeSiteIcons()) : new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SiteIcons>>()));
        services.AddSingleton<SiteIconImages>();
        services.AddSingleton<OnlineBooks>();
        services.AddSingleton<PageThumbnails>();
        if (smokeTest)
        {
            services.AddSingleton<IPasswordPrompt, SmokePasswordPrompt>();
            services.AddSingleton<IShellLauncher, SmokeShellLauncher>();
            services.AddSingleton<IUpdateService, SmokeUpdateService>();
        }
        else
        {
            services.AddSingleton<IPasswordPrompt, PasswordPrompt>();
            services.AddSingleton<IShellLauncher, ShellLauncher>();
            services.AddSingleton<IUpdateService, UpdateService>();
        }
        services.AddSingleton<OtherApps>();
        services.AddSingleton<BookTextActions>();
        services.AddSingleton<AboutBox>();
        services.AddSingleton<ShortcutsBox>();
        services.AddSingleton<AiTestBox>();
        services.AddSingleton<AppRestart>();
        return services;
    }
}

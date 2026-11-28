using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Bibliotaph.Storage.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>
    /// What the shared services need from Windows: WPF's images, dialogs and windows, WebView2's browsers, and secrets in
    /// Credential Manager.
    /// </summary>
    public static IServiceCollection AddWindowsServices(this IServiceCollection services, bool smokeTest, bool everyday)
    {
        services.AddSingleton<WpfImageCodec>();
        services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<WpfImageCodec>());
        services.AddSingleton<IPictureDecoder>(sp => sp.GetRequiredService<WpfImageCodec>());
        services.AddSingleton<CoverImages>();
        services.AddSingleton<SiteIconImages>();
        services.AddSingleton<IPageThumbnails, PageThumbnails>();
        services.AddSingleton<IGraphicsMemory, GraphicsMemory>();

        // The smoke test keeps remembered passwords and storage sign-ins in memory.
        if (smokeTest) services.AddSingleton<IPasswordVault, MemoryPasswordVault>();
        else services.AddSingleton<IPasswordVault, PasswordVault>();
        if (smokeTest) services.AddSingleton<IConnectionSecrets, MemoryConnectionSecrets>();
        else services.AddSingleton<IConnectionSecrets, StorageSecretVault>();
        services.AddSingleton<ApiKeyVault>();
        services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<ApiKeyVault>());
        // This PC's connection to bibliotaph.online. The key in Credential Manager belongs to the Windows account, not
        // the data root, so a smoke test, measurement or screenshot run keeps its own in memory: otherwise it would sync
        // its throwaway library into the user's web library as this PC.
        if (!everyday) services.AddSingleton<IWebLibraryKey, MemoryWebLibraryKey>();
        else services.AddSingleton<IWebLibraryKey, WebLibraryKeyVault>();
        services.AddSingleton<StartOver>();

        services.AddSingleton<OnlineBrowsers>();
        services.AddSingleton<IInAppBrowser>(sp => sp.GetRequiredService<OnlineBrowsers>());
        services.AddSingleton<EbookBrowser>();
        services.AddSingleton<IEbookBrowser>(sp => sp.GetRequiredService<EbookBrowser>());
        services.AddSingleton<OnlineDownloads>();

        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IFilePicker, WindowsFilePicker>();
        services.AddSingleton<IUserPrompts, WpfUserPrompts>();
        services.AddSingleton<IClipboard, WpfClipboard>();
        services.AddSingleton<IDesktopShell, WindowsDesktopShell>();
        services.AddSingleton<IAudioOutput, WindowsAudioOutput>();
        if (smokeTest)
        {
            services.AddSingleton<IPasswordPrompt, SmokePasswordPrompt>();
            services.AddSingleton<IUpdateService, SmokeUpdateService>();
        }
        else
        {
            services.AddSingleton<IPasswordPrompt, PasswordPrompt>();
            services.AddSingleton<IUpdateService, UpdateService>();
        }
        services.AddSingleton<AboutBox>();
        services.AddSingleton<ShortcutsBox>();
        services.AddSingleton<AiTestBox>();
        services.AddSingleton<AppRestart>();
        services.AddSingleton<IAppDialogs, WpfAppDialogs>();
        services.AddSingleton<IAppLifetime, AppLifetime>();

        // One Settings > Online libraries, so a preview is still there after a look at the Library.
        services.AddSingleton<OnlineLibrariesViewModel>();
        services.AddSingleton<OnlineLibrariesSection>(sp => sp.GetRequiredService<OnlineLibrariesViewModel>());
        services.AddSingleton<MainWindow>();
        // Readers, in the main window or a pop-out, are made by ReaderWindows with the book they open.
        services.AddSingleton<ReaderWindows>();
        services.AddSingleton<IReaderWindows>(sp => sp.GetRequiredService<ReaderWindows>());
        return services;
    }
}

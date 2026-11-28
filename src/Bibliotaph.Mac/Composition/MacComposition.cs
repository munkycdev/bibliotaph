using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Mac.Views;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Composition;

/// <summary>
/// What the Mac registers beside the shared DesktopComposition.AddDesktop: its platform services, where its secrets are
/// kept, and where its PDF worker is.
/// </summary>
static class MacComposition
{
    /// <summary>
    /// The PDF worker in the bundle's Resources, which replaces the shared registration's: the worker pool looks beside
    /// the app's assemblies by default, and a bundle keeps programs other than its own out of there.
    /// </summary>
    public static IServiceCollection AddMacPdfWorker(this IServiceCollection services)
    {
        var worker = Path.Combine(Foundation.NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory, "pdfworker", "Bibliotaph.PdfWorker");
        services.AddSingleton(sp => new PdfWorkerPool(new PdfWorkerPoolOptions { WorkerPath = worker }, sp.GetRequiredService<ILoggerFactory>()));
        return services;
    }

    /// <summary>
    /// The web library key, PDF passwords, endpoint keys and storage sign-ins: in the everyday data folder's secrets file,
    /// or for a library kept somewhere else with --data-root, in memory. They belong to this Mac, not to a library, so a
    /// throwaway library would otherwise sync into the user's web library as this Mac, as the Windows app's smoke test
    /// would.
    /// </summary>
    public static IServiceCollection AddSecrets(this IServiceCollection services, bool everyday)
    {
        if (everyday)
        {
            services.AddSingleton<MacSecrets>();
            services.AddSingleton<IWebLibraryKey>(sp => sp.GetRequiredService<MacSecrets>());
            Forward<MacSecrets>(services);
        }
        else
        {
            services.AddSingleton<MemorySecrets>();
            services.AddSingleton<IWebLibraryKey, MemoryWebLibraryKey>();
            Forward<MemorySecrets>(services);
        }
        return services;

        static void Forward<T>(IServiceCollection services) where T : class, IPasswordVault, IApiKeyStore, IConnectionSecrets, IForgetsSecrets
        {
            services.AddSingleton<IForgetsSecrets>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IPasswordVault>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IConnectionSecrets>(sp => sp.GetRequiredService<T>());
        }
    }

    /// <summary>
    /// The Mac's own services, which the shared registrations leave to the host (DesktopComposition.HostServices): the
    /// main thread, ImageIO, AppKit's dialogs and windows, and the PDF reader's documents.
    /// </summary>
    public static IServiceCollection AddMacHost(this IServiceCollection services)
    {
        services.AddSingleton<IUiThread, MacUiThread>();
        services.AddSingleton<MacImageCodec>();
        services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<MacImageCodec>());
        services.AddSingleton<IPictureDecoder>(sp => sp.GetRequiredService<MacImageCodec>());
        services.AddSingleton<IPageThumbnails, MacPageThumbnails>();
        services.AddSingleton<IGraphicsMemory, MacGraphicsMemory>();
        services.AddSingleton<IInAppBrowser, MacInAppBrowser>();
        services.AddSingleton<IEbookBrowser, NoEbookBrowser>();
        services.AddSingleton<MacTheme>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<MacTheme>());
        services.AddSingleton<IFilePicker, MacFilePicker>();
        services.AddSingleton<IUserPrompts, MacUserPrompts>();
        services.AddSingleton<IClipboard, MacClipboard>();
        services.AddSingleton<IDesktopShell, MacDesktopShell>();
        services.AddSingleton<IAudioOutput, MacAudioOutput>();
        services.AddSingleton<IPasswordPrompt, MacPasswordPrompt>();
        services.AddSingleton<IUpdateService, MacUpdates>();
        services.AddSingleton<IAppDialogs, MacAppDialogs>();
        services.AddSingleton<IAppLifetime, MacAppLifetime>();
        services.AddSingleton<MacOnlineDownloads>();
        services.AddSingleton<MacOnlineLibraries>();
        services.AddSingleton<OnlineLibrariesSection>(sp => sp.GetRequiredService<MacOnlineLibraries>());
        services.AddSingleton<MacSiteIcons>();
        services.AddSingleton<MacCovers>();
        services.AddSingleton<IReaderDocuments, MacReaderDocuments>();
        services.AddSingleton<MacReaderWindows>();
        services.AddSingleton<IReaderWindows>(sp => sp.GetRequiredService<MacReaderWindows>());
        return services;
    }
}

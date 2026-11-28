using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Bibliotaph.Storage.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Presentation.Composition;

/// <summary>Stand-ins a test run uses for the web, so it reaches no real site, storage provider or model.</summary>
public sealed record DesktopStandIns(
    HttpMessageHandler? SiteIcons = null,
    HttpMessageHandler? WebLibrary = null,
    HttpMessageHandler? WebDav = null,
    Func<IApiKeyStore, Func<string, IModelClient>>? ModelClients = null);

public static partial class DesktopComposition
{
    /// <summary>
    /// What a desktop host registers itself, since each is the platform's own: the UI thread, the PDF surface, images,
    /// windows, dialogs, browsers, and where passwords, keys and sign-ins are kept. <see cref="AddDesktop"/> needs every
    /// one of them, and the host also adds logging.
    /// </summary>
    public static IReadOnlyList<Type> HostServices { get; } =
    [
        typeof(AppPaths), typeof(TimeProvider), typeof(IUiThread), typeof(IReaderDocuments), typeof(IReaderWindows),
        typeof(IImageCodec), typeof(IPictureDecoder), typeof(IPageThumbnails), typeof(IGraphicsMemory),
        typeof(IPasswordVault), typeof(IConnectionSecrets), typeof(IApiKeyStore), typeof(IWebLibraryKey),
        typeof(IInAppBrowser), typeof(IEbookBrowser), typeof(IThemeService), typeof(IFilePicker), typeof(IUserPrompts),
        typeof(IClipboard), typeof(IDesktopShell), typeof(IPasswordPrompt), typeof(IUpdateService), typeof(IAppDialogs),
        typeof(IAppLifetime), typeof(OnlineLibrariesSection), typeof(IAudioOutput),
    ];

    /// <summary>
    /// Every service the desktop app shares across platforms, in the order its hosted services start. A test run passes
    /// <paramref name="standIns"/> and opens no other app.
    /// </summary>
    public static IServiceCollection AddDesktop(this IServiceCollection services, AppPaths paths, bool pilot = false, bool dev = false,
        DesktopStandIns? standIns = null) =>
        services
            .AddCatalog(paths)
            .AddIndex(paths)
            .AddPdfWorkers()
            .AddLibrary(standIns?.SiteIcons)
            .AddWebLibrary(standIns?.WebLibrary)
            .AddStorage(standIns?.WebDav)
            .AddAi(paths, pilot, standIns?.ModelClients)
            .AddStages()
            // Hosted services stop in reverse order: indexing after the index writer, so it stops first and can still
            // record the job it was on; sync after indexing, so it writes this PC's file one last time.
            .AddIndexing()
            .AddSync()
            .AddShell(dev, standIns is not null, standIns?.SiteIcons)
            .AddPages();
}

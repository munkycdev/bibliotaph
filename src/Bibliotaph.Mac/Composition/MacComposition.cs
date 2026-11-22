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
/// The Mac's registrations of what the Windows app registers in its own Composition: the same services, with the
/// Mac's platform services where Windows has WPF's or Credential Manager's. Kept in the Windows app's order and
/// grouping so the two are easy to compare.
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
    /// The web library key, PDF passwords, endpoint keys and storage sign-ins: in the Keychain, or for a library kept
    /// somewhere else with --data-root, in memory. The Keychain belongs to the Mac account, not the data folder, so a
    /// throwaway library would otherwise sync into the user's web library as this Mac, as the Windows app's smoke test
    /// would.
    /// </summary>
    public static IServiceCollection AddSecrets(this IServiceCollection services, bool everyday)
    {
        if (everyday)
        {
            services.AddSingleton<KeychainSecrets>();
            services.AddSingleton<IWebLibraryKey>(sp => sp.GetRequiredService<KeychainSecrets>());
            Forward<KeychainSecrets>(services);
        }
        else
        {
            services.AddSingleton<MemorySecrets>();
            services.AddSingleton<IWebLibraryKey, MemoryWebLibraryKey>();
            Forward<MemorySecrets>(services);
        }
        return services;

        static void Forward<T>(IServiceCollection services) where T : class, IPasswordVault, IApiKeyStore, IConnectionSecrets
        {
            services.AddSingleton<IPasswordVault>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<T>());
            services.AddSingleton<IConnectionSecrets>(sp => sp.GetRequiredService<T>());
        }
    }

    /// <summary>The library's own stores and services.</summary>
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<EntryStore>();
        services.AddSingleton<VersionStore>();
        services.AddSingleton<VersionFinder>();
        services.AddSingleton<CopiesService>();
        services.AddSingleton<PackStore>();
        services.AddSingleton<PartStore>();
        services.AddSingleton<PartsService>();
        services.AddSingleton<CompanionStore>();
        services.AddSingleton<SourceBooksService>();
        services.AddSingleton<OrganizeService>();
        services.AddSingleton<PartProposalStore>();
        services.AddSingleton<PartProposalService>();
        services.AddSingleton<PackService>();
        services.AddSingleton<ElsewhereStore>();
        services.AddSingleton<LookupRecordStore>();
        services.AddSingleton<ElsewhereService>();
        services.AddSingleton<LookupMatches>();
        services.AddSingleton<LookupLinks>();
        services.AddSingleton<ElsewhereLookups>();
        services.AddSingleton<ChosenCovers>();
        services.AddSingleton<FavoriteStore>();
        services.AddSingleton<ReadingStore>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<CollectionStore>();
        services.AddSingleton<FolderCollectionStore>();
        services.AddSingleton<NornisWorldStore>();
        services.AddSingleton<CollectionsService>();
        services.AddSingleton<FolderCollections>();
        services.AddSingleton<CollectionSuggestions>();
        services.AddSingleton<PageRefStore>();
        services.AddSingleton<PagePlaces>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SessionsService>();
        services.AddSingleton<SmartViewStore>();
        services.AddSingleton<PinStore>();
        services.AddSingleton<SummaryStore>();
        services.AddSingleton<LibrarySummaryService>();
        services.AddSingleton<PinsService>();
        services.AddSingleton<NoteStore>();
        services.AddSingleton<AskHistoryStore>();
        services.AddSingleton<NotesService>();
        services.AddSingleton<ReadingService>();
        services.AddSingleton<DownloadCheck>();
        services.AddSingleton(sp => new LostCovers(sp.GetRequiredService<EntryStore>(), sp.GetRequiredService<CoverCache>(), sp.GetRequiredService<IImageCodec>(),
            sp.GetRequiredService<MetadataProjector>(), LostCovers.CreateClient(), sp.GetRequiredService<ILogger<LostCovers>>()));
        services.AddSingleton<OnlineImports>();
        services.AddSingleton<IInAppBrowser, NoInAppBrowser>();
        services.AddSingleton<IEbookBrowser, NoEbookBrowser>();
        services.AddSingleton<OnlineReaders>();
        services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CoverCache>();
        services.AddSingleton<PageThumbnailCache>();
        services.AddSingleton<MacImageCodec>();
        services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<MacImageCodec>());
        services.AddSingleton<IPictureDecoder>(sp => sp.GetRequiredService<MacImageCodec>());
        services.AddSingleton<UnlockedPasswords>();
        services.AddSingleton<IPasswordStore, IndexPasswords>();
        services.AddSingleton<CatalogExport>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<FileHasher>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton<SourceFiles>();
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<IFileIdentity>(new StorageAwareFileIdentity(new FileIdentity()));
        services.AddSingleton<ForgetText>();
        return services;
    }

    /// <summary>This Mac's connection to bibliotaph.online, and the web library's folders and copies.</summary>
    public static IServiceCollection AddWebLibrary(this IServiceCollection services)
    {
        services.AddSingleton(sp => new WebLibrary(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IWebLibraryKey>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WebLibrary>>()));
        services.AddSingleton<IWebBooks>(sp => sp.GetRequiredService<WebLibrary>());
        services.AddSingleton<WebShelfCache>();
        services.AddSingleton<IKnownHashes>(sp => sp.GetRequiredService<WebShelfCache>());
        services.AddSingleton<ISourceLister>(sp => new StorageAwareSourceLister(
            new StorageSourceLister(sp.GetRequiredService<IStorageDrives>()),
            new WebAwareSourceLister(new FileSystemSourceLister(), sp.GetRequiredService<WebShelfCache>())));
        services.AddSingleton<WebLibraryCopies>();
        services.AddSingleton<IKeptCopies>(sp => sp.GetRequiredService<WebLibraryCopies>());
        services.AddSingleton<WebReadings>();
        services.AddSingleton<ICloudFiles, CloudFiles>();
        services.AddSingleton<OnlineOnlyReturns>();
        return services;
    }

    /// <summary>Storage connected on this Mac (OneDrive, Dropbox, Google Drive, WebDAV), read through each provider's API.</summary>
    public static IServiceCollection AddStorage(this IServiceCollection services)
    {
        services.AddSingleton(_ => new DesktopStorageApps());
        services.AddSingleton<IDesktopClients>(sp => sp.GetRequiredService<DesktopStorageApps>());
        services.AddSingleton(_ => new StorageHttp());
        services.AddSingleton(sp => new PcConnections(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IConnectionSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PcTokens(sp.GetRequiredService<StorageHttp>().Tokens, sp.GetRequiredService<PcConnections>(),
            sp.GetRequiredService<IDesktopClients>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PcTokens>>()));
        services.AddSingleton(sp => new PcDrives(sp.GetRequiredService<StorageHttp>().For, sp.GetRequiredService<PcConnections>(),
            sp.GetRequiredService<PcTokens>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IStorageDrives>(sp => sp.GetRequiredService<PcDrives>());
        services.AddSingleton<IStoragePathNames>(sp => sp.GetRequiredService<PcDrives>());
        services.AddSingleton<StorageChangeFeed>();
        services.AddSingleton<IFolderWatcher>(sp => new StorageAwareFolderWatcher(sp.GetRequiredService<StorageChangeFeed>(), new FileSystemFolderWatcher()));
        services.AddSingleton(sp => new StorageFileReader(sp.GetRequiredService<IStorageDrives>(), Path.Combine(sp.GetRequiredService<AppPaths>().Cache, "storage"),
            new SourceFileReader()));
        services.AddSingleton<ISourceFileReader>(sp => sp.GetRequiredService<StorageFileReader>());
        services.AddSingleton<IRemoteFiles>(sp => sp.GetRequiredService<StorageFileReader>());
        services.AddSingleton(sp => new PcStorageChanges(sp.GetRequiredService<PcDrives>(), sp.GetRequiredService<StorageChangeFeed>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PcStorageChanges>>()));
        services.AddHostedService(sp => sp.GetRequiredService<PcStorageChanges>());
        services.AddSingleton<StorageConnecting>();
        return services;
    }

    /// <summary>
    /// The AI. The built-in engine is llama.cpp's Windows build, so on the Mac only a model server the user names can
    /// read books; the engine's services are registered so the settings that name it still load.
    /// </summary>
    public static IServiceCollection AddAi(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(new BuiltInEngineOptions { ModelsFolder = paths.Models, DownloadFolder = paths.Engine });
        services.AddSingleton(sp => new BuiltInEngine(sp.GetRequiredService<BuiltInEngineOptions>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInEngine>>()));
        services.AddSingleton(sp => new BuiltInEngineDownload(sp.GetRequiredService<BuiltInEngine>(),
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, null, sp.GetRequiredService<ILogger<BuiltInEngineDownload>>()));
        services.AddSingleton(sp => new BuiltInModelDownloads(sp.GetRequiredService<BuiltInEngineOptions>(),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInModelDownloads>>()));
        services.AddSingleton<IGraphicsMemory, MacGraphicsMemory>();
        services.AddKeyedSingleton(BuiltInEngine.EmbeddingKey, (sp, _) => new BuiltInEngine(
            sp.GetRequiredService<BuiltInEngineOptions>() with { Embedding = true, MinContextTokens = 2_048 },
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInEngine>>()));
        services.AddSingleton(sp => new ModelActivity(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AiSettings(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IApiKeyStore>(), null,
            sp.GetRequiredService<BuiltInEngine>(), sp.GetRequiredKeyedService<BuiltInEngine>(BuiltInEngine.EmbeddingKey),
            sp.GetRequiredService<ModelActivity>()));
        services.AddSingleton<AskQueries>();
        services.AddSingleton(sp => new AiUse(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<AskService>();
        services.AddSingleton<LibraryAskQueries>();
        services.AddSingleton<BookVectorQueries>();
        services.AddSingleton<BookVectors>();
        services.AddSingleton<LibraryAskService>();
        services.AddSingleton<NornisScope>();
        services.AddSingleton<MeaningSearchService>();
        services.AddSingleton<ExampleFinder>();
        services.AddSingleton<ClassifierInputs>();
        services.AddSingleton<ClassificationResults>();
        services.AddSingleton<AiReadingFeed>();
        services.AddSingleton<AiService>();
        services.AddSingleton<PlainSearchService>();
        services.AddSingleton(sp => new PilotStore(paths.Pilot, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PilotService>();
        services.AddSingleton(new PilotMode(false));
        services.AddSingleton<PilotSession>();
        return services;
    }

    /// <summary>The window's services: theme, dialogs, the clipboard and other apps, and what the pages share.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton(new DevMode(false));
        services.AddSingleton<MacTheme>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<MacTheme>());
        services.AddSingleton<LibrarySorts>();
        services.AddSingleton<LibraryPackView>();
        services.AddSingleton<IFilePicker, MacFilePicker>();
        services.AddSingleton<IUserPrompts, MacUserPrompts>();
        services.AddSingleton<IClipboard, MacClipboard>();
        services.AddSingleton<IDesktopShell, MacDesktopShell>();
        services.AddSingleton<SettingsLinks>();
        services.AddSingleton<LibraryFolders>();
        services.AddSingleton<LibraryActivity>();
        services.AddSingleton<SearchState>();
        services.AddSingleton<LibraryAskScope>();
        services.AddSingleton<MacCovers>();
        services.AddSingleton(sp => new SiteIcons(sp.GetRequiredService<AppPaths>(), new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SiteIcons>>()));
        services.AddSingleton<OnlineBooks>();
        services.AddSingleton<IPageThumbnails, NoPageThumbnails>();
        services.AddSingleton<IPasswordPrompt, MacPasswordPrompt>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IUpdateService, NoUpdates>();
        services.AddSingleton<OtherApps>();
        services.AddSingleton<BookTextActions>();
        services.AddSingleton<IAppDialogs, MacAppDialogs>();
        services.AddSingleton<IAppLifetime, MacAppLifetime>();
        return services;
    }

    /// <summary>The pages and their view models, navigation and the readers.</summary>
    public static IServiceCollection AddPages(this IServiceCollection services)
    {
        services.AddSingleton<INavigationService>(sp => new NavigationService(route => CreatePage(sp, route)));
        services.AddSingleton<Func<LibraryViewModel>>(sp => () => sp.GetRequiredService<LibraryViewModel>());
        services.AddSingleton<LibraryPages>();
        services.AddSingleton<CollectionDirectory>();
        services.AddSingleton<SmartViewDirectory>();
        services.AddSingleton<PinDirectory>();
        services.AddSingleton<PinOpener>();
        services.AddSingleton<InspectorFactory>();
        services.AddSingleton<DetailsPaneViewModel>();
        services.AddSingleton<LibrarySelectionFactory>();
        services.AddTransient<CollectionActions>();
        services.AddSingleton<SessionDirectory>();
        services.AddTransient<SessionActions>();
        services.AddSingleton<Func<SessionActions>>(sp => () => sp.GetRequiredService<SessionActions>());
        services.AddSingleton<Func<SessionPackViewModel>>(sp => () => sp.GetRequiredService<SessionPackViewModel>());
        services.AddSingleton<Func<SessionReaderViewModel>>(sp => () => sp.GetRequiredService<SessionReaderViewModel>());
        services.AddSingleton<SessionPages>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<TourViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<CollectionsViewModel>();
        services.AddTransient<SessionsViewModel>();
        services.AddTransient<SessionPackViewModel>();
        services.AddTransient<SessionReaderViewModel>();
        services.AddTransient<NeedsReviewViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<StorageAccountsViewModel>();
        services.AddTransient<LibrarySectionViewModel>();
        services.AddTransient<ProcessingSectionViewModel>();
        services.AddTransient<PreferencesSectionViewModel>();
        services.AddTransient<VocabularyViewModel>();
        services.AddTransient<PasswordsSectionViewModel>();
        services.AddTransient<DevicesSectionViewModel>();
        services.AddTransient<StartOverSectionViewModel>();
        services.AddTransient<BackupSectionViewModel>();
        services.AddTransient<AiSettingsViewModel>();
        services.AddTransient<BuiltInAiViewModel>();
        services.AddTransient<AiReadingViewModel>();
        services.AddTransient<SummaryViewModel>();
        services.AddTransient<PilotPanelViewModel>();
        services.AddTransient<PilotReviewViewModel>();
        services.AddSingleton<DownloadCheckViewModel>();
        services.AddSingleton<OnlineLibrariesSection, MacOnlineLibraries>();
        services.AddSingleton<SearchGuideViewModel>();
        services.AddSingleton<LibraryAskViewModel>();
        services.AddSingleton<PlainSearchViewModel>();
        services.AddSingleton<CoverPages>();
        services.AddSingleton<DocumentOpener>();
        services.AddSingleton<IReaderDocuments, MacReaderDocuments>();
        services.AddSingleton<ViewerServices>();
        services.AddSingleton<MacReaderWindows>();
        services.AddSingleton<IReaderWindows>(sp => sp.GetRequiredService<MacReaderWindows>());
        return services;
    }

    static PageViewModel CreatePage(IServiceProvider services, Route route) => route switch
    {
        Route.Home => services.GetRequiredService<HomeViewModel>(),
        Route.Library => services.GetRequiredService<LibraryViewModel>(),
        Route.Collections => services.GetRequiredService<CollectionsViewModel>(),
        Route.Sessions or Route.SessionPack or Route.SessionEdit => services.GetRequiredService<SessionsViewModel>(),
        Route.NeedsReview => services.GetRequiredService<NeedsReviewViewModel>(),
        Route.Settings => services.GetRequiredService<SettingsViewModel>(),
        Route.PilotReview => services.GetRequiredService<PilotReviewViewModel>(),
        Route.DownloadCheck => services.GetRequiredService<DownloadCheckViewModel>(),
        Route.AiReading => services.GetRequiredService<AiReadingViewModel>(),
        Route.Summary => services.GetRequiredService<SummaryViewModel>(),
        Route.OnlineReader => services.GetRequiredService<LibraryViewModel>(),
        Route.Viewer => services.GetRequiredService<MacReaderWindows>().Create(null),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };
}

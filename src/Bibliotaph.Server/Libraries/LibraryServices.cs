using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Server.Ai;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// What every library shares with the server: logging, the clock, the way to reach a user's drives, the PDF worker
/// and image codec, so one worker reads for every user in turn rather than one each, where covers are kept, the keys
/// that seal a user's AI keys and PDF passwords, how a model service is reached, and the online catalogue books are
/// looked up in, so its pace is kept across every library rather than per user.
/// </summary>
public sealed record LibraryHost(ILoggerFactory Logs, TimeProvider Clock, UserDriveFactory Drives, string TemporaryFolder, IPdfEngine Pdf,
    IImageCodec Images, ICoverStore Covers, IDataProtectionProvider Protection, ModelClientFactory Models, ForegroundActivity? Foreground = null,
    UploadMemory? Uploads = null, IBookCatalogue? Catalogue = null);

/// <summary>
/// One library's services: the same stores, queries and indexing the Windows app composes in its own host, built once
/// per open library so nothing is shared between users. What the Windows app gets from Windows (WPF's image codec, the
/// password vault, the file system's watcher, cloud files) is replaced by what the server has.
/// </summary>
public static class LibraryServices
{
    /// <summary>
    /// The most of a user's books kept at once while being read. Copies go as soon as their stages are done, and books
    /// are read one at a time, so this is only a backstop.
    /// </summary>
    const long FetchedBytes = 1L * 1024 * 1024 * 1024;

    public static ServiceProvider Build(string userId, AppPaths paths, string backups, LibraryHost host, SyncFiles syncFiles)
    {
        var services = new ServiceCollection();
        services.AddSingleton(host.Logs);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(host.Clock);
        services.AddSingleton(paths);

        // catalog.db: the user's work.
        services.AddSingleton(sp => new CatalogDatabase(paths.CatalogDatabase, backups, host.Clock, sp.GetRequiredService<ILogger<CatalogDatabase>>()));
        services.AddDbContextFactory<CatalogDbContext>((sp, options) => sp.GetRequiredService<CatalogDatabase>().Configure(options));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SourceRootStore>();
        services.AddSingleton(sp => new VocabularyStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton(sp => new MetadataStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton(sp => new ClassificationStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton(sp => new CorrectionStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton(sp => new UserTagStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));

        // index.db: derived, rebuildable. One writer; reads on their own connections.
        services.AddSingleton(sp => IndexDatabase.ForFile(paths.IndexDatabase, sp.GetRequiredService<ILogger<IndexDatabase>>()));
        services.AddSingleton<IndexWriter>();
        services.AddSingleton<IndexQueries>();
        services.AddSingleton<UserTagQueries>();
        services.AddSingleton<ChapterQueries>();
        services.AddSingleton<LibraryQueries>();
        services.AddSingleton<SummaryQueries>();
        services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), host.Clock,
            bookAtATime: true));
        services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), host.Clock));

        // The user's connected storage, read through each provider's API; a book is downloaded only while it's being read.
        services.AddSingleton(_ => host.Drives.For(userId));
        services.AddSingleton<IStorageDrives>(sp => sp.GetRequiredService<UserDrives>());
        services.AddSingleton<ISourceLister, StorageSourceLister>();
        services.AddSingleton(sp => new StorageFileReader(sp.GetRequiredService<IStorageDrives>(), host.TemporaryFolder, new SourceFileReader()));
        services.AddSingleton<ISourceFileReader>(sp => sp.GetRequiredService<StorageFileReader>());
        services.AddSingleton<StorageChangeFeed>();
        services.AddSingleton(host.Pdf);
        services.AddSingleton(host.Images);
        // Passwords the reader gave a locked PDF, sealed as AI keys are, which indexing opens it with as the Windows app's do.
        services.AddSingleton(sp => new ServerPdfPasswords(sp.GetRequiredService<SettingsStore>(),
            host.Protection.CreateProtector($"Bibliotaph.Pdf.Passwords.{userId}")));
        services.AddSingleton<IPasswordStore>(sp => sp.GetRequiredService<ServerPdfPasswords>());
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<IFileIdentity, NoFileIdentity>();
        services.AddSingleton<ICloudFiles, NoCloudFiles>();

        // The stores and services indexing reaches, as the Windows app registers them.
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<EntryStore>();
        services.AddSingleton<VersionStore>();
        services.AddSingleton<VersionFinder>();
        services.AddSingleton<CopiesService>();
        services.AddSingleton<PackStore>();
        services.AddSingleton<PartStore>();
        services.AddSingleton<PartsService>();
        services.AddSingleton<PartProposalStore>();
        services.AddSingleton<PartProposalService>();
        services.AddSingleton<PackService>();
        services.AddSingleton<ElsewhereStore>();
        services.AddSingleton<LookupRecordStore>();
        services.AddSingleton<ChosenCovers>();
        services.AddSingleton<CoverPages>();
        services.AddSingleton<FavoriteStore>();
        services.AddSingleton<ReadingStore>();
        services.AddSingleton<CollectionStore>();
        services.AddSingleton<FolderCollectionStore>();
        services.AddSingleton<CollectionsService>();
        services.AddSingleton<FolderCollections>();
        services.AddSingleton<PageRefStore>();
        services.AddSingleton<PagePlaces>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<SessionsService>();
        services.AddSingleton<PinStore>();
        services.AddSingleton<SummaryStore>();
        services.AddSingleton<LibrarySummaryService>();
        services.AddSingleton<PinsService>();
        services.AddSingleton(sp => new SmartViewStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton<NoteStore>();
        services.AddSingleton<CatalogExport>();
        services.AddSingleton<ExportService>();
        // A backup is made to download: its working copy of catalog.db goes with the user's other backups, outside the
        // users folder, where Litestream would take any database for one to replicate, and outside the books being read,
        // which the PDF worker may read.
        services.AddSingleton(sp => new BackupService(new AppPaths(Path.Combine(backups, "making-" + Guid.NewGuid().ToString("N"))),
            sp.GetRequiredService<CatalogDatabase>(), sp.GetRequiredService<IndexDatabase>(),
            sp.GetRequiredService<SettingsStore>(), host.Clock, sp.GetRequiredService<ILogger<BackupService>>()));
        services.AddSingleton<HeldRestore>();
        services.AddSingleton<CoverCache>();
        services.AddSingleton(sp => new CoverMirror(userId, sp.GetRequiredService<CoverCache>(), host.Covers, sp.GetRequiredService<ILogger<CoverMirror>>()));
        // The reader draws its thumbnails in the browser, so this folder stays empty; Forget its text clears it all the same.
        services.AddSingleton(sp => new PageThumbnailCache(paths));
        services.AddSingleton<ForgetText>();
        services.AddSingleton<FileHasher>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton(sp => new SourceFiles(paths, sp.GetRequiredService<ArchiveReader>(), sp.GetRequiredService<IDiskSpace>(),
            sp.GetRequiredService<IndexingOptions>(), FetchedBytes, sp.GetRequiredService<StorageFileReader>()));
        services.AddSingleton<OnlineOnlyReturns>();
        services.AddSingleton<StageServices>();
        services.AddSingleton<MarkStores>();
        services.AddSingleton<MetadataProjector>();
        services.AddSingleton<MetadataHints>();
        services.AddSingleton<FolderRules>();

        // Library sync, with the web library as one more device: its PCs' files are in sync.db, not a folder.
        services.AddSingleton(syncFiles);
        services.AddSingleton<ServerSyncChannel>();
        services.AddSingleton(sp => new LibrarySyncStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(),
            sp.GetRequiredService<MetadataStore>(), sp.GetRequiredService<VocabularyStore>(), host.Clock, sp.GetRequiredService<ILogger<LibrarySyncStore>>(),
            sp.GetRequiredService<LookupRecordStore>()));
        services.AddSingleton(sp => new LibrarySync(sp.GetRequiredService<LibrarySyncStore>(), sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<CatalogDatabase>(), sp.GetRequiredService<MetadataProjector>(), host.Clock, sp.GetRequiredService<ILogger<LibrarySync>>(),
            SyncFiles.WebMachine, [sp.GetRequiredService<ServerSyncChannel>()], sp.GetRequiredService<UserTagService>()));
        // What the AI worked out about files, shared with the user's PCs by content through sync.db.
        services.AddSingleton<SharedResultCache>();
        services.AddSingleton<IResultChannel, ServerResultChannel>();
        services.AddSingleton<SharedPageText>();
        services.AddSingleton<SharedResults>();
        // What a connected PC shows of the web library: its files, and what was read from each.
        services.AddSingleton<WebShelfPublisher>();
        services.AddSingleton<SharedReadings>();

        // What the details change: the Windows inspector's services.
        services.AddSingleton(sp => new ChapterScanStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton<ChapterProjection>();
        services.AddSingleton<ChapterScanService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<CorrectionService>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<ReadingService>();
        services.AddSingleton<NotesService>();
        services.AddSingleton(new HeldActions(host.Clock));
        services.AddSingleton(new PendingCovers(host.Clock));

        // Indexing: the Windows app's stages that read a book and match it with others. OCR is Tesseract in the PDF
        // worker, where the Windows app has Windows OCR.
        services.AddSingleton<IStage, ProbeStage>();
        services.AddSingleton<IStage, TextStage>();
        services.AddSingleton<IStage, CoversStage>();
        services.AddSingleton<IStage, RuleHintsStage>();
        services.AddSingleton<IStage, OcrStage>();
        services.AddSingleton<UserTagService>();
        services.AddSingleton<IStage, MatchStage>();
        services.AddSingleton<IStage, IdentifyStage>();
        services.AddSingleton(host.Catalogue ?? new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(PublicNetwork.Handler())));
        services.AddSingleton<BookLookups>();
        services.AddSingleton<IStage, LookUpStage>();
        // AI: the Windows app's Classify and whole-book scan stages and Settings > AI, over a model service the user chose, with their key.
        services.AddSingleton(sp => new ServerApiKeys(sp.GetRequiredService<SettingsStore>(), host.Protection.CreateProtector($"Bibliotaph.Ai.Keys.{userId}")));
        services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<ServerApiKeys>());
        services.AddSingleton(host.Models);
        services.AddSingleton(sp =>
        {
            var keys = sp.GetRequiredService<ServerApiKeys>();
            var clients = sp.GetRequiredService<ModelClientFactory>();
            // Reading whole books, and the whole library for meaning, wait to be switched on: each of their requests is paid for on the user's key.
            return new AiSettings(sp.GetRequiredService<SettingsStore>(), keys, endpoint => clients.Create(endpoint, () => keys.Find(endpoint)),
                activity: new ModelActivity(host.Clock, clients.QuietPeriod), scanWholeBooksByDefault: false, libraryVectorsByDefault: false);
        });
        services.AddSingleton(new AiReadingFeed(host.Clock));
        services.AddSingleton<ExampleFinder>();
        services.AddSingleton<ClassifierInputs>();
        services.AddSingleton<ClassificationResults>();
        services.AddSingleton<IStage, ClassifyStage>();
        services.AddSingleton<IStage, ChapterScanStage>();
        services.AddSingleton<AiService>();
        services.AddSingleton<RunAgainService>();
        // Ask this book: its pages found by words and, with an embedding model, by meaning, and answered by the model.
        services.AddSingleton(sp => new AskHistoryStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton<AskQueries>();
        services.AddSingleton(new AiUse(host.Clock));
        services.AddSingleton<AskService>();
        // Ask in the top bar: a request read as a search, or a question answered from the books the Library shows, which
        // find pages by meaning as the library is read for it in the background.
        services.AddSingleton<PlainSearchService>();
        services.AddSingleton<LibraryAskQueries>();
        services.AddSingleton<LibraryAskService>();
        services.AddSingleton<LibraryVectors>();
        services.AddSingleton<IStageCatalog, StageCatalog>();
        services.AddSingleton(new IndexingOptions { OnlineOnlyReserveBytes = 0, Foreground = host.Foreground });
        services.AddSingleton<IndexingFiles>();
        services.AddSingleton(sp => new IndexingService(
            sp.GetRequiredService<SourceRootStore>(), sp.GetRequiredService<LibraryStore>(), sp.GetRequiredService<JobBoard>(),
            sp.GetRequiredService<IStageCatalog>(), sp.GetRequiredService<IndexingFiles>(), sp.GetRequiredService<PackService>(),
            sp.GetRequiredService<PartsService>(), sp.GetRequiredService<FolderCollections>(), sp.GetRequiredService<IndexingOptions>(),
            sp.GetRequiredService<ILogger<IndexingService>>(), host.Clock, sp.GetRequiredService<StorageChangeFeed>()));
        services.AddSingleton<IJobControl>(sp => sp.GetRequiredService<IndexingService>());
        services.AddSingleton<IIndexingStatus>(sp => sp.GetRequiredService<IndexingService>());

        // Needs review: its cards come from these, and each decision goes through them, as in the Windows app.
        services.AddSingleton<ElsewhereService>();
        services.AddSingleton<LookupMatches>();
        services.AddSingleton<OnlineImports>();
        services.AddSingleton(new HeldOnlinePages(host.Clock, host.Uploads));
        services.AddSingleton<CollectionSuggestions>();
        services.AddSingleton<TermOverlapFinder>();
        services.AddSingleton<VocabularyService>();
        services.AddSingleton<ReviewCards>();
        services.AddSingleton<ReviewService>();

        // Check a download: only its verdicts, since the browser reads the files and sends their hashes and fingerprints.
        services.AddSingleton<DownloadCheck>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

/// <summary>A drive's files have no disk or file ID: moves are recognised by content, as on any disk without IDs.</summary>
sealed class NoFileIdentity : IFileIdentity
{
    public VolumeIdentity? Volume(string folder) => null;

    public string? FileId(string path) => null;
}

/// <summary>Nothing on the server is a Windows cloud file: a book is downloaded only while it's read, then deleted.</summary>
sealed class NoCloudFiles : ICloudFiles
{
    public CloudFileState StateOf(string path) => CloudFileState.Unknown;

    public void ReturnToOnlineOnly(string path) => throw new IOException("There are no cloud files on the server.");
}

using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// What every library shares with the server: logging, the clock, the way to reach a user's drives, the PDF worker
/// and image codec, so one worker reads for every user in turn rather than one each, and where covers are kept.
/// </summary>
public sealed record LibraryHost(ILoggerFactory Logs, TimeProvider Clock, UserDriveFactory Drives, string TemporaryFolder, IPdfEngine Pdf,
    IImageCodec Images, ICoverStore Covers);

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

    public static ServiceProvider Build(string userId, AppPaths paths, string backups, LibraryHost host)
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
        services.AddSingleton<LibraryQueries>();
        services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), host.Clock,
            bookAtATime: true));
        services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), host.Clock));

        // The user's drives, read through Graph; a book is downloaded only while it's being read.
        services.AddSingleton(_ => host.Drives.For(userId));
        services.AddSingleton<IDriveClients>(sp => sp.GetRequiredService<UserDrives>());
        services.AddSingleton<ISourceLister, OneDriveSourceLister>();
        services.AddSingleton(sp => new OneDriveFileReader(sp.GetRequiredService<IDriveClients>(), host.TemporaryFolder, new SourceFileReader()));
        services.AddSingleton<ISourceFileReader>(sp => sp.GetRequiredService<OneDriveFileReader>());
        services.AddSingleton<DriveChangeFeed>();
        services.AddSingleton(host.Pdf);
        services.AddSingleton(host.Images);
        services.AddSingleton<IPasswordStore, NoPasswords>();
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<IFileIdentity, NoFileIdentity>();
        services.AddSingleton<ICloudFiles, NoCloudFiles>();

        // The stores and services indexing reaches, as the Windows app registers them.
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<EntryStore>();
        services.AddSingleton<VersionStore>();
        services.AddSingleton<CopiesService>();
        services.AddSingleton<PackStore>();
        services.AddSingleton<PartStore>();
        services.AddSingleton<PartsService>();
        services.AddSingleton<PartProposalStore>();
        services.AddSingleton<PartProposalService>();
        services.AddSingleton<PackService>();
        services.AddSingleton<ElsewhereStore>();
        services.AddSingleton<ChosenCovers>();
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
        services.AddSingleton<PinsService>();
        services.AddSingleton(sp => new SmartViewStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), host.Clock));
        services.AddSingleton<NoteStore>();
        services.AddSingleton<CatalogExport>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<CoverCache>();
        services.AddSingleton(sp => new CoverMirror(userId, sp.GetRequiredService<CoverCache>(), host.Covers, sp.GetRequiredService<ILogger<CoverMirror>>()));
        services.AddSingleton<FileHasher>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton(sp => new SourceFiles(paths, sp.GetRequiredService<ArchiveReader>(), sp.GetRequiredService<IDiskSpace>(),
            sp.GetRequiredService<IndexingOptions>(), FetchedBytes, sp.GetRequiredService<OneDriveFileReader>()));
        services.AddSingleton<OnlineOnlyReturns>();
        services.AddSingleton<StageServices>();
        services.AddSingleton<MarkStores>();
        services.AddSingleton<MetadataProjector>();
        services.AddSingleton<MetadataHints>();
        services.AddSingleton<FolderRules>();

        // What the details change: the Windows inspector's services, without AI's chapter scan, which the server doesn't run yet.
        services.AddSingleton<MetadataService>();
        services.AddSingleton<CorrectionService>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<NotesService>();
        services.AddSingleton(new HeldActions(host.Clock));

        // Indexing: the Windows app's stages that read a book, without OCR and AI, which the server doesn't run yet.
        services.AddSingleton<IStage, ProbeStage>();
        services.AddSingleton<IStage, TextStage>();
        services.AddSingleton<IStage, CoversStage>();
        services.AddSingleton<IStage, RuleHintsStage>();
        services.AddSingleton<IStageCatalog, StageCatalog>();
        services.AddSingleton(new IndexingOptions { OnlineOnlyReserveBytes = 0 });
        services.AddSingleton<IndexingFiles>();
        services.AddSingleton(sp => new IndexingService(
            sp.GetRequiredService<SourceRootStore>(), sp.GetRequiredService<LibraryStore>(), sp.GetRequiredService<JobBoard>(),
            sp.GetRequiredService<IStageCatalog>(), sp.GetRequiredService<IndexingFiles>(), sp.GetRequiredService<PackService>(),
            sp.GetRequiredService<PartsService>(), sp.GetRequiredService<FolderCollections>(), sp.GetRequiredService<IndexingOptions>(),
            sp.GetRequiredService<ILogger<IndexingService>>(), host.Clock, sp.GetRequiredService<DriveChangeFeed>()));

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

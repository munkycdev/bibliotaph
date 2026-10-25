using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Libraries;

/// <summary>What every library shares with the server: logging, the clock, and the way to reach a user's drives.</summary>
public sealed record LibraryHost(ILoggerFactory Logs, TimeProvider Clock, UserDriveFactory Drives, string TemporaryFolder);

/// <summary>
/// One library's services: the same stores, queries and indexing the Windows app composes in its own host, built once
/// per open library so nothing is shared between users. What the Windows app gets from Windows (PDFium's worker
/// processes, WPF's image codec, the password vault, the file system's watcher) is replaced by what the server has.
/// </summary>
public static class LibraryServices
{
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
        services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), host.Clock));
        services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), host.Clock));

        // The user's drives, read through Graph; a book is downloaded only while it's being read.
        services.AddSingleton(_ => host.Drives.For(userId));
        services.AddSingleton<IDriveClients>(sp => sp.GetRequiredService<UserDrives>());
        services.AddSingleton<ISourceLister, OneDriveSourceLister>();
        services.AddSingleton<ISourceFileReader>(sp => new OneDriveFileReader(sp.GetRequiredService<IDriveClients>(), host.TemporaryFolder, new SourceFileReader()));
        services.AddSingleton<DriveChangeFeed>();
        services.AddSingleton<IPdfEngine, NoPdfEngine>();
        services.AddSingleton<IImageCodec, NoImageCodec>();
        services.AddSingleton<IPasswordStore, NoPasswords>();
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<IFileIdentity, FileIdentity>();
        services.AddSingleton<ICloudFiles, CloudFiles>();

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
        services.AddSingleton<NoteStore>();
        services.AddSingleton<CoverCache>();
        services.AddSingleton<FileHasher>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton<SourceFiles>();
        services.AddSingleton<OnlineOnlyReturns>();
        services.AddSingleton<StageServices>();
        services.AddSingleton<MarkStores>();
        services.AddSingleton<MetadataProjector>();
        services.AddSingleton<MetadataHints>();
        services.AddSingleton<FolderRules>();

        // Indexing: scanning and hashing. No stage reads a book on the server yet, so the catalog of stages is empty and
        // each book waits in the queue with its first stage.
        services.AddSingleton<IStageCatalog>(_ => new StageCatalog([]));
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

/// <summary>The server has no PDF engine yet: a PDF waits in the queue rather than being read.</summary>
sealed class NoPdfEngine : IPdfEngine
{
    public Task<PdfResult<IPdfDocument>> OpenAsync(string path, string? password, CancellationToken ct) =>
        Task.FromResult(new PdfResult<IPdfDocument>(null, ErrorKind.Internal, "PDFs aren't read on the server."));
}

/// <summary>The server has no image codec yet; nothing that makes covers or thumbnails runs there.</summary>
sealed class NoImageCodec : IImageCodec
{
    public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride) => throw new NotSupportedException("Images aren't encoded on the server.");

    public (int Width, int Height)? ReadSize(Stream image) => null;

    public byte[]? Thumbnail(Stream image, int maxWidth) => null;
}

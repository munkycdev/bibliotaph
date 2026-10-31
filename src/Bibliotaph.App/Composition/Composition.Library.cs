using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>The library's own stores and services: books, packs, adventures, collections, binders, notes, pins, files, passwords and backups.</summary>
    public static IServiceCollection AddLibrary(this IServiceCollection services, bool smokeTest)
    {
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
        services.AddSingleton<ElsewhereService>();
        services.AddSingleton<LookupMatches>();
        services.AddSingleton<ChosenCovers>();
        services.AddSingleton<FavoriteStore>();
        services.AddSingleton<ReadingStore>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<CollectionStore>();
        services.AddSingleton<FolderCollectionStore>();
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
        services.AddSingleton<OnlineImports>();
        services.AddSingleton<OnlineBrowsers>();
        services.AddSingleton<EbookBrowser>();
        services.AddSingleton<OnlineReaders>();
        services.AddSingleton<OnlineDownloads>();
        services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CoverCache>();
        services.AddSingleton<PageThumbnailCache>();
        services.AddSingleton<WpfImageCodec>();
        services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<WpfImageCodec>());
        // The smoke test keeps remembered passwords in memory and opens no other app.
        if (smokeTest) services.AddSingleton<IPasswordVault, MemoryPasswordVault>();
        else services.AddSingleton<IPasswordVault, PasswordVault>();
        services.AddSingleton<UnlockedPasswords>();
        services.AddSingleton<IPasswordStore, IndexPasswords>();
        services.AddSingleton<ApiKeyVault>();
        services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<ApiKeyVault>());
        services.AddSingleton<StartOver>();
        services.AddSingleton<CatalogExport>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<FileHasher>();
        services.AddSingleton<ArchiveReader>();
        services.AddSingleton<SourceFiles>();
        services.AddSingleton<IDiskSpace, DiskSpace>();
        services.AddSingleton<IFileIdentity, FileIdentity>();
        services.AddSingleton<ForgetText>();
        return services;
    }
}

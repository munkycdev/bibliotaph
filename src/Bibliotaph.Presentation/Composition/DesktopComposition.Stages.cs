using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Presentation.Composition;

public static partial class DesktopComposition
{
    /// <summary>The job queue's stages, and the services that read and change what they found.</summary>
    public static IServiceCollection AddStages(this IServiceCollection services)
    {
        services.AddSingleton<StageServices>();
        // Every stage registered as an IStage is in it: lanes, startup and the screens that name stages all read it.
        services.AddSingleton<IStageCatalog, StageCatalog>();
        services.AddSingleton<IStage, ProbeStage>();
        services.AddSingleton<IStage, TextStage>();
        services.AddSingleton<IStage, CoversStage>();
        services.AddSingleton<IStage, RuleHintsStage>();
        services.AddSingleton<IStage, OcrStage>();
        services.AddSingleton<IStage, ClassifyStage>();
        services.AddSingleton<IStage, MatchStage>();
        services.AddSingleton<IStage, IdentifyStage>();
        // Through the web library while connected, which holds the server's keys; on its own, every catalogue it can
        // ask, with the user's own keys for those that need one.
        services.AddSingleton(sp => new OwnCatalogues(sp.GetRequiredService<IApiKeyStore>()));
        services.AddSingleton<IBookCatalogue>(sp => new WebLibraryCatalogue(sp.GetRequiredService<WebLibrary>(),
            sp.GetRequiredService<OwnCatalogues>(), sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton<BookLookups>();
        services.AddSingleton<IStage, LookUpStage>();
        services.AddSingleton<ChapterScanStore>();
        services.AddSingleton<ChapterQueries>();
        services.AddSingleton<ChapterProjection>();
        services.AddSingleton<IStage, ChapterScanStage>();
        services.AddSingleton<ChapterScanService>();
        services.AddSingleton<MarkStores>();
        services.AddSingleton<MetadataProjector>();
        services.AddSingleton<MetadataHints>();
        services.AddSingleton<FolderRules>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<CorrectionService>();
        services.AddSingleton<TermOverlapFinder>();
        services.AddSingleton<VocabularyService>();
        services.AddSingleton<UserTagService>();
        services.AddSingleton<ReviewCards>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<RunAgainService>();
        return services;
    }
}

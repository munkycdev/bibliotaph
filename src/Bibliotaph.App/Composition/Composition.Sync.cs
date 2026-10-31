using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>Library sync, and what this PC shares with the user's other devices through the web library.</summary>
    public static IServiceCollection AddSync(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SyncFollowUp(sp.GetRequiredService<UserTagService>(), sp.GetRequiredService<MetadataHints>(),
            sp.GetRequiredService<ForgetText>(), sp.GetRequiredService<CoverPages>(), sp.GetRequiredService<CoverCache>(), sp.GetRequiredService<ILogger<SyncFollowUp>>()));
        services.AddSingleton(sp => new LibrarySyncStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(),
            sp.GetRequiredService<MetadataStore>(), sp.GetRequiredService<VocabularyStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LibrarySyncStore>>(), sp.GetRequiredService<LookupRecordStore>(), sp.GetRequiredService<SyncFollowUp>().ReadChosenCovers));
        services.AddSingleton(sp => new LibrarySync(sp.GetRequiredService<LibrarySyncStore>(), sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<CatalogDatabase>(), sp.GetRequiredService<MetadataProjector>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LibrarySync>>(), channels: [sp.GetRequiredService<WebLibrary>()], followUp: sp.GetRequiredService<SyncFollowUp>()));
        services.AddHostedService(sp => sp.GetRequiredService<LibrarySync>());
        // What the AI worked out about this PC's files, shared by content with the user's other devices through the web library.
        services.AddSingleton<SharedResultCache>();
        services.AddSingleton<IResultChannel>(sp => sp.GetRequiredService<WebLibrary>());
        services.AddSingleton<SharedPageText>();
        services.AddSingleton<SharedResults>();
        services.AddHostedService(sp => sp.GetRequiredService<SharedResults>());
        services.AddSingleton<WebShelfSync>();
        services.AddHostedService(sp => sp.GetRequiredService<WebShelfSync>());
        // What this PC's AI is doing, for Settings › Processing on the web.
        services.AddSingleton<IPcActivityChannel>(sp => sp.GetRequiredService<WebLibrary>());
        services.AddSingleton<PcActivityReport>();
        services.AddHostedService(sp => sp.GetRequiredService<PcActivityReport>());
        return services;
    }
}

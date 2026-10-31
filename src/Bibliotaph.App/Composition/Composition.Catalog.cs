using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>catalog.db: the user's work.</summary>
    public static IServiceCollection AddCatalog(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(sp => new CatalogDatabase(paths.CatalogDatabase, paths.Backups,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CatalogDatabase>>()));
        services.AddDbContextFactory<CatalogDbContext>((sp, options) => sp.GetRequiredService<CatalogDatabase>().Configure(options));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<SourceRootStore>();
        services.AddSingleton(sp => new VocabularyStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MetadataStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ClassificationStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new CorrectionStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new UserTagStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}

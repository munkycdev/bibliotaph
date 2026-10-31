using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>index.db: derived, rebuildable. One writer; reads on their own connections.</summary>
    public static IServiceCollection AddIndex(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(sp => IndexDatabase.ForFile(paths.IndexDatabase, sp.GetRequiredService<ILogger<IndexDatabase>>()));
        services.AddSingleton<IndexWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexWriter>());
        services.AddSingleton<IndexQueries>();
        services.AddSingleton<UserTagQueries>();
        services.AddSingleton<LibraryQueries>();
        services.AddSingleton<SummaryQueries>();
        return services;
    }
}

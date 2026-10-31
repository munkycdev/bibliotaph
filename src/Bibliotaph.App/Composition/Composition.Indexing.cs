using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>Indexing, which runs the stages, and reading the library for meaning.</summary>
    public static IServiceCollection AddIndexing(this IServiceCollection services)
    {
        services.AddSingleton(new IndexingOptions());
        services.AddSingleton<IndexingFiles>();
        services.AddSingleton<IndexingService>();
        // Both forward to the one service the host runs; registered with their own implementation type, each would get a
        // second IndexingService that never starts.
        services.AddSingleton<IJobControl>(sp => sp.GetRequiredService<IndexingService>());
        services.AddSingleton<IIndexingStatus>(sp => sp.GetRequiredService<IndexingService>());
        services.AddHostedService(sp => sp.GetRequiredService<IndexingService>());
        // Reading the whole library for meaning, slowly and last of all.
        services.AddSingleton<LibraryVectors>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryVectors>());
        return services;
    }
}

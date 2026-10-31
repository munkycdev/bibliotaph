using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>The AI: the built-in engine and its downloads, the model settings, Ask, cataloguing's inputs and results, and the model pilot.</summary>
    public static IServiceCollection AddAi(this IServiceCollection services, AppPaths paths, bool pilot, bool smokeTest)
    {
        // The built-in AI's engine (llama.cpp's server, in aiworker\); nothing starts until there is a book to read.
        services.AddSingleton(new BuiltInEngineOptions { ModelsFolder = paths.Models, DownloadFolder = paths.Engine });
        services.AddSingleton(sp => new BuiltInEngine(sp.GetRequiredService<BuiltInEngineOptions>(),
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInEngine>>()));
        // The engine itself, downloaded at startup when this copy has none beside it.
        services.AddSingleton(sp => new BuiltInEngineDownload(sp.GetRequiredService<BuiltInEngine>(),
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) }, null, sp.GetRequiredService<ILogger<BuiltInEngineDownload>>()));
        // Its models, downloaded only when Download is pressed. Pause and Cancel stop a download; nothing else times it out.
        services.AddSingleton(sp => new BuiltInModelDownloads(sp.GetRequiredService<BuiltInEngineOptions>(),
            new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInModelDownloads>>()));
        services.AddSingleton<GraphicsMemory>();
        // A second, small engine in embedding mode, for finding pages by meaning in Ask this book.
        services.AddKeyedSingleton(BuiltInEngine.EmbeddingKey, (sp, _) => new BuiltInEngine(
            sp.GetRequiredService<BuiltInEngineOptions>() with { Embedding = true, MinContextTokens = 2_048 },
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) }, null,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BuiltInEngine>>()));
        // The smoke test has a stand-in model server of its own, for Ask this book (SmokeModel).
        services.AddSingleton(sp => new ModelActivity(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AiSettings(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IApiKeyStore>(),
            smokeTest ? SmokeModel.ClientsFor(sp.GetRequiredService<IApiKeyStore>()) : null,
            sp.GetRequiredService<BuiltInEngine>(), sp.GetRequiredKeyedService<BuiltInEngine>(BuiltInEngine.EmbeddingKey),
            sp.GetRequiredService<ModelActivity>()));
        services.AddSingleton<AskQueries>();
        services.AddSingleton(sp => new AiUse(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<AskService>();
        services.AddSingleton<LibraryAskQueries>();
        services.AddSingleton<LibraryAskService>();
        services.AddSingleton<MeaningSearchService>();
        services.AddSingleton<ExampleFinder>();
        services.AddSingleton<ClassifierInputs>();
        services.AddSingleton<ClassificationResults>();
        services.AddSingleton<AiReadingFeed>();
        services.AddSingleton<AiService>();
        services.AddSingleton<PlainSearchService>();
        // The model pilot: its own pilot.db, shown only with --pilot.
        services.AddSingleton(sp => new PilotStore(paths.Pilot, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PilotService>();
        services.AddSingleton(new PilotMode(pilot));
        services.AddSingleton<PilotSession>();
        return services;
    }
}

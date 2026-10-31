using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Web.Services;

public static class ApiServices
{
    /// <summary>The server's API, one client per area over a shared <see cref="ApiHttp"/>.</summary>
    public static IServiceCollection AddBibliotaphApi(this IServiceCollection services)
    {
        services.AddScoped<ApiHttp>();
        services.AddScoped<AccountClient>();
        services.AddScoped<AiClient>();
        services.AddScoped<AskClient>();
        services.AddScoped<BackupClient>();
        services.AddScoped<BindersClient>();
        services.AddScoped<BooksClient>();
        services.AddScoped<CollectionsClient>();
        services.AddScoped<LibraryClient>();
        services.AddScoped<OnlineClient>();
        services.AddScoped<ReaderClient>();
        services.AddScoped<ReviewClient>();
        services.AddScoped<SearchesClient>();
        services.AddScoped<SelectionClient>();
        services.AddScoped<SettingsClient>();
        services.AddScoped<StorageClient>();
        services.AddScoped<VocabularyClient>();
        return services;
    }
}

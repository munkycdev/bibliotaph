using System.IO;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

static partial class Composition
{
    /// <summary>
    /// Storage connected on this PC (OneDrive, Dropbox, Google Drive, Box, WebDAV), read through each provider's API as
    /// the web library reads it. Its folders are library folders like any other: listed from the provider, each book
    /// downloaded to be read, and changes asked for every few minutes.
    /// </summary>
    public static IServiceCollection AddStorage(this IServiceCollection services, bool smokeTest)
    {
        if (smokeTest) services.AddSingleton<IConnectionSecrets, MemoryConnectionSecrets>();
        else services.AddSingleton<IConnectionSecrets, StorageSecretVault>();
        services.AddSingleton(_ => new DesktopStorageApps());
        services.AddSingleton<IDesktopClients>(sp => sp.GetRequiredService<DesktopStorageApps>());
        // The smoke test connects a stand-in WebDAV server; the OAuth providers need a browser and a real account.
        services.AddSingleton(_ => smokeTest ? new StorageHttp(new SmokeWebDav()) : new StorageHttp());
        services.AddSingleton(sp => new PcConnections(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IConnectionSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PcTokens(sp.GetRequiredService<StorageHttp>().Tokens, sp.GetRequiredService<PcConnections>(),
            sp.GetRequiredService<IDesktopClients>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PcTokens>>()));
        services.AddSingleton(sp => new PcDrives(sp.GetRequiredService<StorageHttp>().For, sp.GetRequiredService<PcConnections>(),
            sp.GetRequiredService<PcTokens>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IStorageDrives>(sp => sp.GetRequiredService<PcDrives>());
        services.AddSingleton<IStoragePathNames>(sp => sp.GetRequiredService<PcDrives>());
        services.AddSingleton<StorageChangeFeed>();
        services.AddSingleton<IFolderWatcher>(sp => new StorageAwareFolderWatcher(sp.GetRequiredService<StorageChangeFeed>(), new FileSystemFolderWatcher()));
        // A book in storage is downloaded whole to be read: to a temporary file deleted when it's closed, or into the
        // extract cache while its stages read it.
        services.AddSingleton(sp => new StorageFileReader(sp.GetRequiredService<IStorageDrives>(), Path.Combine(sp.GetRequiredService<AppPaths>().Cache, "storage"),
            new SourceFileReader()));
        services.AddSingleton<ISourceFileReader>(sp => sp.GetRequiredService<StorageFileReader>());
        services.AddSingleton<IRemoteFiles>(sp => sp.GetRequiredService<StorageFileReader>());
        services.AddSingleton(sp => new PcStorageChanges(sp.GetRequiredService<PcDrives>(), sp.GetRequiredService<StorageChangeFeed>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PcStorageChanges>>()));
        services.AddHostedService(sp => sp.GetRequiredService<PcStorageChanges>());
        services.AddSingleton<StorageConnecting>();
        return services;
    }
}

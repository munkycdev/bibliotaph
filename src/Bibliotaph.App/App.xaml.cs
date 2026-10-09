using System.Windows;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Bibliotaph.App;

/// <summary>
/// The composition root. Builds the Generic Host, prepares both databases, applies the theme and opens
/// the shell. Closing the window stops the host, which stops all processing (product principle 7).
/// </summary>
public partial class App : Application
{
    IHost? _host;
    bool _smokeTest;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smokeTest = e.Args.Contains("--smoke-test");
        var measureSearch = e.Args.Contains("--measure-search");
        var measureViewer = e.Args.Contains("--measure-viewer");
        var paths = DataRootArgument(e.Args) is { } root ? new AppPaths(root) : AppPaths.ForCurrentUser();
        foreach (var directory in paths.Directories) System.IO.Directory.CreateDirectory(directory);

        // Page text and passwords are never logged; file paths are.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(System.IO.Path.Combine(paths.Logs, "bibliotaph-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(args.Exception, "Unobserved task exception");
        Log.Information("Bibliotaph {Version} starting; data in {Root}", typeof(App).Assembly.GetName().Version, paths.Root);

        try
        {
            StartOver.FinishIfRequested(paths, e.Args);
            foreach (var directory in paths.Directories) System.IO.Directory.CreateDirectory(directory);
            _host = BuildHost(paths);
            await PrepareDatabasesAsync(_host.Services);
            await _host.StartAsync();

            var services = _host.Services;
            ProjectMetadataInBackground(services);
            if (measureSearch)
            {
                Shutdown(await SearchMeasurement.RunAsync(services));
                return;
            }
            if (measureViewer)
            {
                Shutdown(await ViewerMeasurement.RunAsync(services));
                return;
            }

            var theme = services.GetRequiredService<ThemeService>();
            await theme.InitializeAsync();

            var window = services.GetRequiredService<MainWindow>();
            theme.Track(window);
            MainWindow = window;
            window.Show();
            await services.GetRequiredService<ShellViewModel>().StartAsync();

            if (_smokeTest) Shutdown(await SmokeTest.RunAsync(services, window, Argument(e.Args, "--smoke-files")));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            if (_smokeTest)
            {
                Shutdown(1);
                return;
            }
            MessageBox.Show(
                $"Bibliotaph could not start.\n\n{ex.Message}\n\nDetails are in the log folder:\n{paths.Logs}",
                "Bibliotaph", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary><c>--data-root &lt;folder&gt;</c> keeps Bibliotaph's databases and logs somewhere other than %LOCALAPPDATA%.</summary>
    static string? DataRootArgument(string[] args) => Argument(args, "--data-root");

    /// <summary>The full path given after <paramref name="name"/>, if any.</summary>
    static string? Argument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? System.IO.Path.GetFullPath(args[at + 1]) : null;
    }

    static IHost BuildHost(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        // A missing registration fails at startup, where the smoke test sees it, not when a page first opens.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true }));
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);

        // catalog.db: the user's work.
        builder.Services.AddSingleton(sp => new CatalogDatabase(paths.CatalogDatabase, paths.Backups,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CatalogDatabase>>()));
        builder.Services.AddDbContextFactory<CatalogDbContext>((sp, options) => sp.GetRequiredService<CatalogDatabase>().Configure(options));
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<SourceRootStore>();
        builder.Services.AddSingleton(sp => new VocabularyStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new MetadataStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));

        // index.db: derived, rebuildable. One writer; reads on their own connections.
        builder.Services.AddSingleton(sp => IndexDatabase.ForFile(paths.IndexDatabase, sp.GetRequiredService<ILogger<IndexDatabase>>()));
        builder.Services.AddSingleton<IndexWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexWriter>());
        builder.Services.AddSingleton<IndexQueries>();
        builder.Services.AddSingleton<LibraryQueries>();

        // PDFium runs only in worker processes; none starts until something asks for a page.
        builder.Services.AddSingleton(sp => new PdfWorkerPool(new PdfWorkerPoolOptions(), sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<ISourceFileReader, SourceFileReader>();

        // Processing: scan, hash, and the job queue's stages. Registered after the index writer, so it stops first
        // (hosted services stop in reverse order) and can still record the job it was on.
        builder.Services.AddSingleton<LibraryStore>();
        builder.Services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<CoverCache>();
        builder.Services.AddSingleton<WpfImageCodec>();
        builder.Services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<WpfImageCodec>());
        builder.Services.AddSingleton<PasswordVault>();
        builder.Services.AddSingleton<IPasswordStore>(sp => sp.GetRequiredService<PasswordVault>());
        builder.Services.AddSingleton<StartOver>();
        builder.Services.AddSingleton<FileHasher>();
        builder.Services.AddSingleton<IDiskSpace, DiskSpace>();
        builder.Services.AddSingleton<StageServices>();
        builder.Services.AddSingleton<IStage, ProbeStage>();
        builder.Services.AddSingleton<IStage, TextStage>();
        builder.Services.AddSingleton<IStage, CoversStage>();
        builder.Services.AddSingleton<IStage, RuleHintsStage>();
        builder.Services.AddSingleton<IStage, OcrStage>();
        builder.Services.AddSingleton<MetadataProjector>();
        builder.Services.AddSingleton<MetadataHints>();
        builder.Services.AddSingleton<MetadataService>();
        builder.Services.AddSingleton(new IndexingOptions());
        builder.Services.AddSingleton<IndexingService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingService>());

        // Shell
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<LibraryFolders>();
        builder.Services.AddSingleton<LibraryActivity>(); // creates its timer on the UI thread, where the shell resolves it
        builder.Services.AddSingleton<SearchState>();
        builder.Services.AddSingleton<CoverImages>();
        builder.Services.AddSingleton<ViewerRequests>();
        builder.Services.AddSingleton<IPasswordPrompt, PasswordPrompt>();
        builder.Services.AddSingleton<AboutBox>();
        builder.Services.AddSingleton<INavigationService>(sp => new NavigationService(route => CreatePage(sp, route)));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<LibraryViewModel>();
        builder.Services.AddTransient<CollectionsViewModel>();
        builder.Services.AddTransient<SessionsViewModel>();
        builder.Services.AddTransient<NeedsReviewViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<LibraryFoldersViewModel>();
        builder.Services.AddTransient<ViewerViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }

    static PageViewModel CreatePage(IServiceProvider services, Route route) => route switch
    {
        Route.Home => services.GetRequiredService<HomeViewModel>(),
        Route.Library => services.GetRequiredService<LibraryViewModel>(),
        Route.Collections => services.GetRequiredService<CollectionsViewModel>(),
        Route.Sessions => services.GetRequiredService<SessionsViewModel>(),
        Route.NeedsReview => services.GetRequiredService<NeedsReviewViewModel>(),
        Route.Settings => services.GetRequiredService<SettingsViewModel>(),
        Route.LibraryFolders => services.GetRequiredService<LibraryFoldersViewModel>(),
        Route.Viewer => services.GetRequiredService<ViewerViewModel>(),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    static async Task PrepareDatabasesAsync(IServiceProvider services)
    {
        var catalog = await services.GetRequiredService<CatalogDatabase>().MigrateAsync();
        if (catalog.BackupPath is not null) Log.Information("catalog.db was backed up to {Backup}", catalog.BackupPath);

        var added = await services.GetRequiredService<VocabularyStore>().SeedAsync();
        if (added > 0) Log.Information("Added {Count} starter vocabulary terms", added);

        var index = await services.GetRequiredService<IndexDatabase>().InitializeAsync();
        Log.Information("index.db: {Result}", index);
    }

    /// <summary>
    /// Copies catalog.db's metadata into index.db once the index writer is running: after an upgrade or a rebuild of
    /// index.db, and to pick up any change a previous run didn't finish projecting. In the background; the library
    /// refreshes when it is done.
    /// </summary>
    static void ProjectMetadataInBackground(IServiceProvider services) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<MetadataProjector>().ProjectAllAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Projecting metadata into index.db failed");
            }
        });

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        if (_smokeTest)
        {
            Shutdown(1);
            return;
        }
        MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nThe details are in the log.", "Bibliotaph",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stop on the thread pool: hosted services must not need the UI thread to finish.
        if (_host is { } host)
        {
            Task.Run(async () =>
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
                if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
                else host.Dispose();
            }).GetAwaiter().GetResult();
        }
        Log.Information("Bibliotaph stopped");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

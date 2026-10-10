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
        var pilot = e.Args.Contains("--pilot");
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
            // A restore chosen before the restart (slice 4j) swaps its catalog in now, before anything opens it.
            var restored = await PendingRestore.ApplyIfRequestedAsync(paths);
            if (restored is not null) Log.Information("Restore of {Backup}: {Result}", restored.Request?.Backup, restored.Problem ?? "restored");
            foreach (var directory in paths.Directories) System.IO.Directory.CreateDirectory(directory);
            _host = BuildHost(paths, pilot);
            await PrepareDatabasesAsync(_host.Services);
            await _host.StartAsync();

            var services = _host.Services;
            ProjectMetadataInBackground(services);
            if (!_smokeTest && !measureSearch && !measureViewer) BackUpWeeklyInBackground(services);
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
            if (restored is not null && !_smokeTest) AppRestart.Tell(restored);

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

    static IHost BuildHost(AppPaths paths, bool pilot)
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
        builder.Services.AddSingleton(sp => new ClassificationStore(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>(), sp.GetRequiredService<TimeProvider>()));

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
        builder.Services.AddSingleton<EntryStore>();
        builder.Services.AddSingleton<VersionStore>();
        builder.Services.AddSingleton<CopiesService>();
        builder.Services.AddSingleton<PackStore>();
        builder.Services.AddSingleton<PackService>();
        builder.Services.AddSingleton<ElsewhereStore>();
        builder.Services.AddSingleton<ElsewhereService>();
        builder.Services.AddSingleton<FavoriteStore>();
        builder.Services.AddSingleton<ReadingStore>();
        builder.Services.AddSingleton<FavoritesService>();
        builder.Services.AddSingleton<CollectionStore>();
        builder.Services.AddSingleton<CollectionsService>();
        builder.Services.AddSingleton<PageRefStore>();
        builder.Services.AddSingleton<PagePlaces>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<SessionsService>();
        builder.Services.AddSingleton<SmartViewStore>();
        builder.Services.AddSingleton<NoteStore>();
        builder.Services.AddSingleton<NotesService>();
        builder.Services.AddSingleton<ReadingService>();
        builder.Services.AddSingleton<DownloadCheck>();
        builder.Services.AddSingleton(sp => new JobBoard(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<IndexDatabase>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new IndexStore(sp.GetRequiredService<IndexWriter>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<CoverCache>();
        builder.Services.AddSingleton<WpfImageCodec>();
        builder.Services.AddSingleton<IImageCodec>(sp => sp.GetRequiredService<WpfImageCodec>());
        builder.Services.AddSingleton<PasswordVault>();
        builder.Services.AddSingleton<IPasswordStore>(sp => sp.GetRequiredService<PasswordVault>());
        builder.Services.AddSingleton<ApiKeyVault>();
        builder.Services.AddSingleton<IApiKeyStore>(sp => sp.GetRequiredService<ApiKeyVault>());
        builder.Services.AddSingleton(sp => new AiSettings(sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<IApiKeyStore>()));
        builder.Services.AddSingleton<ClassifierInputs>();
        builder.Services.AddSingleton<ClassificationResults>();
        builder.Services.AddSingleton<StartOver>();
        builder.Services.AddSingleton<CatalogExport>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<FileHasher>();
        builder.Services.AddSingleton<ArchiveReader>();
        builder.Services.AddSingleton<SourceFiles>();
        builder.Services.AddSingleton<IDiskSpace, DiskSpace>();
        builder.Services.AddSingleton<IFileIdentity, FileIdentity>();
        builder.Services.AddSingleton<StageServices>();
        builder.Services.AddSingleton<IStage, ProbeStage>();
        builder.Services.AddSingleton<IStage, TextStage>();
        builder.Services.AddSingleton<IStage, CoversStage>();
        builder.Services.AddSingleton<IStage, RuleHintsStage>();
        builder.Services.AddSingleton<IStage, OcrStage>();
        builder.Services.AddSingleton<IStage, ClassifyStage>();
        builder.Services.AddSingleton<IStage, MatchStage>();
        builder.Services.AddSingleton<MetadataProjector>();
        builder.Services.AddSingleton<MetadataHints>();
        builder.Services.AddSingleton<MetadataService>();
        builder.Services.AddSingleton<VocabularyService>();
        builder.Services.AddSingleton<ReviewService>();
        builder.Services.AddSingleton<AiService>();
        // The model pilot (slice 2d): its own pilot.db, shown only with --pilot.
        builder.Services.AddSingleton(sp => new PilotStore(paths.Pilot, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PilotService>();
        builder.Services.AddSingleton(new PilotMode(pilot));
        builder.Services.AddSingleton<PilotSession>();
        builder.Services.AddSingleton(new IndexingOptions());
        builder.Services.AddSingleton<IndexingService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingService>());

        // Shell
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<SettingsLinks>();
        builder.Services.AddSingleton<LibraryFolders>();
        builder.Services.AddSingleton<LibraryActivity>(); // creates its timer on the UI thread, where the shell resolves it
        builder.Services.AddSingleton<SearchState>();
        builder.Services.AddSingleton<CoverImages>();
        builder.Services.AddSingleton<IPasswordPrompt, PasswordPrompt>();
        builder.Services.AddSingleton<AboutBox>();
        builder.Services.AddSingleton<AiTestBox>();
        builder.Services.AddSingleton<AppRestart>();
        builder.Services.AddSingleton<INavigationService>(sp => new NavigationService(route => CreatePage(sp, route)));
        builder.Services.AddSingleton<Func<LibraryViewModel>>(sp => () => sp.GetRequiredService<LibraryViewModel>());
        builder.Services.AddSingleton<LibraryPages>();
        builder.Services.AddSingleton<CollectionDirectory>();
        builder.Services.AddSingleton<SmartViewDirectory>();
        builder.Services.AddTransient<CollectionActions>();
        builder.Services.AddSingleton<SessionDirectory>();
        builder.Services.AddTransient<SessionActions>();
        builder.Services.AddSingleton<Func<SessionActions>>(sp => () => sp.GetRequiredService<SessionActions>());
        builder.Services.AddSingleton<Func<SessionPackViewModel>>(sp => () => sp.GetRequiredService<SessionPackViewModel>());
        builder.Services.AddSingleton<Func<RunSessionViewModel>>(sp => () => sp.GetRequiredService<RunSessionViewModel>());
        builder.Services.AddSingleton<SessionPages>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<LibraryViewModel>();
        builder.Services.AddTransient<CollectionsViewModel>();
        builder.Services.AddTransient<SessionsViewModel>();
        builder.Services.AddTransient<SessionPackViewModel>();
        builder.Services.AddTransient<RunSessionViewModel>();
        builder.Services.AddTransient<NeedsReviewViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<LibrarySectionViewModel>();
        builder.Services.AddTransient<ProcessingSectionViewModel>();
        builder.Services.AddTransient<AppearanceSectionViewModel>();
        builder.Services.AddTransient<ReviewSectionViewModel>();
        builder.Services.AddTransient<VocabularyViewModel>();
        builder.Services.AddTransient<StartOverSectionViewModel>();
        builder.Services.AddTransient<BackupSectionViewModel>();
        builder.Services.AddTransient<AiSettingsViewModel>();
        builder.Services.AddTransient<PilotPanelViewModel>();
        builder.Services.AddTransient<PilotReviewViewModel>();
        // One check a download page, so a check keeps running while the library is shown.
        builder.Services.AddSingleton<DownloadCheckViewModel>();
        builder.Services.AddSingleton<SearchGuideViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        // Readers, in the main window or a pop-out, are made by ReaderWindows with the book they open.
        builder.Services.AddSingleton<UnlockedPasswords>();
        builder.Services.AddSingleton<ViewerServices>();
        builder.Services.AddSingleton<ReaderWindows>();
        return builder.Build();
    }

    static PageViewModel CreatePage(IServiceProvider services, Route route) => route switch
    {
        Route.Home => services.GetRequiredService<HomeViewModel>(),
        Route.Library => services.GetRequiredService<LibraryViewModel>(),
        Route.Collections => services.GetRequiredService<CollectionsViewModel>(),
        // A pack's page and run mode are opened with their pack (SessionPages); by route alone, the list of them.
        Route.Sessions or Route.SessionPack or Route.RunSession => services.GetRequiredService<SessionsViewModel>(),
        Route.NeedsReview => services.GetRequiredService<NeedsReviewViewModel>(),
        Route.Settings => services.GetRequiredService<SettingsViewModel>(),
        Route.PilotReview => services.GetRequiredService<PilotReviewViewModel>(),
        Route.DownloadCheck => services.GetRequiredService<DownloadCheckViewModel>(),
        Route.Viewer => services.GetRequiredService<ReaderWindows>().Create(null),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    static async Task PrepareDatabasesAsync(IServiceProvider services)
    {
        var catalog = await services.GetRequiredService<CatalogDatabase>().MigrateAsync();
        if (catalog.BackupPath is not null) Log.Information("catalog.db was backed up to {Backup}", catalog.BackupPath);

        var added = await services.GetRequiredService<VocabularyStore>().SeedAsync();
        if (added > 0) Log.Information("Added {Count} starter vocabulary terms", added);

        // Before the Classify lane looks at whether it may send anything.
        var ai = services.GetRequiredService<AiSettings>();
        await ai.LoadAsync();
        Log.Information("AI: {State}", ai.Setup.IsReady ? $"on, {ai.Setup.Provider} model {ai.Setup.Model}" : "off");

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

    /// <summary>
    /// The weekly automatic backup (slice 4j plan, choice 2), at the first start of the week: in the background, as
    /// copying the catalog can take a moment and nothing waits on it.
    /// </summary>
    static void BackUpWeeklyInBackground(IServiceProvider services) =>
        _ = Task.Run(async () =>
        {
            try
            {
                if (await services.GetRequiredService<BackupService>().BackUpWeeklyIfDueAsync() is { } backup)
                    Log.Information("Weekly backup saved to {Backup}", backup.Path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "The weekly backup failed");
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

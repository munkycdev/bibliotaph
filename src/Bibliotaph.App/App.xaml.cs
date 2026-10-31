using System.Windows;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Bibliotaph.App;

/// <summary>
/// The composition root. Builds the Generic Host, prepares both databases, applies the theme and opens
/// the shell. Closing the window stops the host, which stops all processing.
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
        var dev = e.Args.Contains("--dev");
        var screenshots = Argument(e.Args, "--screenshots");
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
            // A restore chosen before the restart swaps its catalog in now, before anything opens it.
            var restored = await PendingRestore.ApplyIfRequestedAsync(paths);
            if (restored is not null) Log.Information("Restore of {Backup}: {Result}", restored.Request?.Backup, restored.Problem ?? "restored");
            foreach (var directory in paths.Directories) System.IO.Directory.CreateDirectory(directory);
            _host = BuildHost(paths, pilot, dev, _smokeTest);
            await PrepareDatabasesAsync(_host.Services);
            await _host.StartAsync();

            var services = _host.Services;
            ProjectMetadataInBackground(services);
            var everyday = !_smokeTest && !measureSearch && !measureViewer && screenshots is null;
            if (everyday) BackUpWeeklyInBackground(services);
            if (everyday) DownloadEngineInBackground(services);
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
            await services.GetRequiredService<LibrarySorts>().InitializeAsync();
            await services.GetRequiredService<LibraryPackView>().InitializeAsync();

            var window = services.GetRequiredService<MainWindow>();
            theme.Track(window);
            MainWindow = window;
            window.Show();
            await services.GetRequiredService<ShellViewModel>().StartAsync();
            // Updates: checked once the window shows, in the background.
            services.GetRequiredService<IUpdateService>().Start();
            if (restored is not null && !_smokeTest) AppRestart.Tell(restored);

            if (_smokeTest) Shutdown(await SmokeTest.RunAsync(services, window, Argument(e.Args, "--smoke-files")));
            else if (screenshots is not null)
                Shutdown(await Screenshots.RunAsync(services, window, screenshots, Argument(e.Args, "--screenshot-files"), RawArgument(e.Args, "--screenshot-open")));
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

    /// <summary>The text given after <paramref name="name"/>, as it is, if any.</summary>
    static string? RawArgument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    static IHost BuildHost(AppPaths paths, bool pilot, bool dev, bool smokeTest)
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

        builder.Services
            .AddCatalog(paths)
            .AddIndex(paths)
            .AddPdfWorkers()
            .AddLibrary(smokeTest)
            .AddWebLibrary(smokeTest)
            .AddAi(paths, pilot, smokeTest)
            .AddStages()
            // Hosted services stop in reverse order: indexing after the index writer, so it stops first and can still
            // record the job it was on; sync after indexing, so it writes this PC's file one last time.
            .AddIndexing()
            .AddSync()
            .AddShell(dev, smokeTest)
            .AddPages();
        return builder.Build();
    }

    static async Task PrepareDatabasesAsync(IServiceProvider services)
    {
        var catalog = await services.GetRequiredService<CatalogDatabase>().MigrateAsync();
        if (catalog.BackupPath is not null) Log.Information("catalog.db was backed up to {Backup}", catalog.BackupPath);

        var added = await services.GetRequiredService<VocabularyStore>().SeedAsync();
        if (added > 0) Log.Information("Added {Count} starter vocabulary terms", added);

        // Before the Classify lane looks at whether it may send anything.
        var ai = services.GetRequiredService<AiSettings>();
        await ai.LoadAsync();
        Log.Information("AI: {State}", ai.Setup.IsReady ? $"on, {ai.Setup.RunProvider} model {ai.Setup.ModelName}" : "off");
        services.GetRequiredService<BuiltInEngine>().StopWhenNotNeeded(ai, services.GetRequiredService<IIndexingStatus>());
        services.GetRequiredKeyedService<BuiltInEngine>(BuiltInEngine.EmbeddingKey).StopWhenNotNeededForAsk(ai);
        services.GetRequiredService<BuiltInModelDownloads>().WakeAiWhenDownloaded(ai);
        services.GetRequiredService<BuiltInEngineDownload>().WakeAiWhenDownloaded(ai);

        var index = await services.GetRequiredService<IndexDatabase>().InitializeAsync();
        Log.Information("index.db: {Result}", index);
        // Before the first projection, which checks every card: books owned elsewhere meet the files already in the library.
        services.GetRequiredService<ElsewhereService>().CheckFilesWhenProjected();
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
                // What the AI found in each chapter, kept in catalog.db, back into a rebuilt index.db.
                await services.GetRequiredService<ChapterProjection>().RestoreAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Projecting metadata into index.db failed");
            }
        });

    /// <summary>
    /// The weekly automatic backup, at the first start of the week: in the background, as copying the catalog can take a
    /// moment and nothing waits on it.
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

    /// <summary>
    /// The built-in AI's engine, when this copy has none beside it (a build made without build/fetch-engine.ps1): at
    /// every start until it's here, whether or not the built-in AI is used.
    /// </summary>
    static void DownloadEngineInBackground(IServiceProvider services) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<BuiltInEngineDownload>().EnsureAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Downloading the built-in AI engine failed");
            }
        });

    /// <summary>
    /// A failure of something outside the app, such as a file that went away, is shown and the app carries on. Anything
    /// else may have stopped an action half way, so the app says so and closes cleanly, letting the databases close.
    /// </summary>
    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var recoverable = UiFailures.IsRecoverable(e.Exception);
        Log.Error(e.Exception, recoverable ? "Unhandled UI exception; carrying on" : "Unhandled UI exception; closing");
        e.Handled = true;
        if (_smokeTest)
        {
            Shutdown(1);
            return;
        }
        if (recoverable)
        {
            MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nThe details are in the log.", "Bibliotaph",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show($"Bibliotaph ran into a problem it can't safely carry on from, so it will close. Your library is "
            + $"saved.\n\n{e.Exception.Message}\n\nThe details are in the log.", "Bibliotaph", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
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

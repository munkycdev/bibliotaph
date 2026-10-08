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
            _host = BuildHost(paths);
            await PrepareDatabasesAsync(_host.Services);
            await _host.StartAsync();

            var services = _host.Services;
            var theme = services.GetRequiredService<ThemeService>();
            await theme.InitializeAsync();

            var window = services.GetRequiredService<MainWindow>();
            theme.Track(window);
            MainWindow = window;
            window.Show();
            await services.GetRequiredService<ShellViewModel>().StartAsync();

            if (_smokeTest) Shutdown(await SmokeTest.RunAsync(services, window));
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
    static string? DataRootArgument(string[] args)
    {
        var at = Array.IndexOf(args, "--data-root");
        return at >= 0 && at + 1 < args.Length ? System.IO.Path.GetFullPath(args[at + 1]) : null;
    }

    static IHost BuildHost(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);

        // catalog.db: the user's work.
        builder.Services.AddSingleton(sp => new CatalogDatabase(paths.CatalogDatabase, paths.Backups,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<CatalogDatabase>>()));
        builder.Services.AddDbContextFactory<CatalogDbContext>((sp, options) => sp.GetRequiredService<CatalogDatabase>().Configure(options));
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<SourceRootStore>();

        // index.db: derived, rebuildable. One writer; reads on their own connections.
        builder.Services.AddSingleton(sp => IndexDatabase.ForFile(paths.IndexDatabase, sp.GetRequiredService<ILogger<IndexDatabase>>()));
        builder.Services.AddSingleton<IndexWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexWriter>());
        builder.Services.AddSingleton<IndexQueries>();

        // PDFium runs only in worker processes; none starts until something asks for a page.
        builder.Services.AddSingleton(sp => new PdfWorkerPool(new PdfWorkerPoolOptions(), sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<ISourceFileReader, SourceFileReader>();

        // Shell
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<LibraryFolders>();
        builder.Services.AddSingleton<INavigationService>(sp => new NavigationService(route => CreatePage(sp, route)));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<LibraryViewModel>();
        builder.Services.AddTransient<CollectionsViewModel>();
        builder.Services.AddTransient<SessionsViewModel>();
        builder.Services.AddTransient<NeedsReviewViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<LibraryFoldersViewModel>();
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
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    static async Task PrepareDatabasesAsync(IServiceProvider services)
    {
        var catalog = await services.GetRequiredService<CatalogDatabase>().MigrateAsync();
        if (catalog.BackupPath is not null) Log.Information("catalog.db was backed up to {Backup}", catalog.BackupPath);

        var index = await services.GetRequiredService<IndexDatabase>().InitializeAsync();
        Log.Information("index.db: {Result}", index);
    }

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

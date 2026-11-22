using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Mac.Composition;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Mac.Views;
using Bibliotaph.Presentation.Composition;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Bibliotaph.Mac;

static class Program
{
    static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new AppDelegate(args);
        NSApplication.Main(args);
    }
}

/// <summary>
/// The composition root, as the Windows app's App: builds the Generic Host, prepares both databases and opens the
/// window. Quitting stops the host, which stops all processing.
/// </summary>
[Register(nameof(AppDelegate))]
sealed class AppDelegate(string[] args) : NSApplicationDelegate
{
    IHost? _host;
    MainWindowController? _main;
    bool _stopped;
    System.Runtime.InteropServices.PosixSignalRegistration? _terminate;

    public override async void DidFinishLaunching(NSNotification notification)
    {
        var paths = DataRoot(args) is { } root ? new AppPaths(root) : AppPaths.ForCurrentUser();
        foreach (var directory in paths.Directories) Directory.CreateDirectory(directory);

        // Page text and passwords are never logged; file paths are.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.Logs, "bibliotaph-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        // A quit from outside (kill, a logout) quits as the menu does, so the host stops and the databases close.
        _terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(this));
        });
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");
        Log.Information("Bibliotaph for Mac {Version} starting; data in {Root}", typeof(AppDelegate).Assembly.GetName().Version, paths.Root);

        try
        {
            _host = BuildHost(paths, everyday: DataRoot(args) is null);
            await PrepareDatabasesAsync(_host.Services);
            await _host.StartAsync();
            var services = _host.Services;
            ProjectMetadataInBackground(services);

            await services.GetRequiredService<MacTheme>().InitializeAsync();
            await services.GetRequiredService<LibrarySorts>().InitializeAsync();
            await services.GetRequiredService<LibraryPackView>().InitializeAsync();

            _main = new MainWindowController(services);
            NSApplication.SharedApplication.MainMenu = Menus.Build(_main);
            _main.ShowWindow(this);
            // A copy checking a build stays behind what the person at the Mac is doing, so it can't take their typing.
            if (!args.Contains("--screenshot")) NSApplication.SharedApplication.Activate();
            // Opens Home, or the Library when Settings says to start there, as on Windows.
            await services.GetRequiredService<ShellViewModel>().StartAsync();
            await DevArguments.ApplyAsync(args, services, _main);
            _main.CheckConnectionAsync(offer: DataRoot(args) is null).FireAndForget();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            using var alert = new NSAlert
            {
                AlertStyle = NSAlertStyle.Critical,
                MessageText = "Bibliotaph could not start",
                InformativeText = $"{ex.Message}\n\nDetails are in the log folder:\n{paths.Logs}",
            };
            alert.RunModal();
            NSApplication.SharedApplication.Terminate(this);
        }
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_host is null || _stopped) return NSApplicationTerminateReply.Now;
        // Stop on the thread pool, as hosted services must not need the main thread to finish, then quit for real.
        _ = Task.Run(async () =>
        {
            try
            {
                _main?.Unload();
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                if (_host is IAsyncDisposable disposable) await disposable.DisposeAsync();
                else _host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Stopping Bibliotaph failed");
            }
            Log.Information("Bibliotaph stopped");
            await Log.CloseAndFlushAsync();
            _stopped = true;
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => sender.ReplyToApplicationShouldTerminate(true));
        });
        return NSApplicationTerminateReply.Later;
    }

    /// <summary><c>--data-root &lt;folder&gt;</c> keeps the databases and logs somewhere other than Application Support.</summary>
    static string? DataRoot(string[] args)
    {
        var at = Array.IndexOf(args, "--data-root");
        return at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : null;
    }

    static IHost BuildHost(AppPaths paths, bool everyday)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        // A missing registration fails at startup, not when a page first opens.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true }));
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services
            .AddMacHost()
            .AddSecrets(everyday)
            .AddDesktop(paths)
            .AddMacPdfWorker();
        return builder.Build();
    }

    static async Task PrepareDatabasesAsync(IServiceProvider services)
    {
        var catalog = await services.GetRequiredService<CatalogDatabase>().MigrateAsync();
        if (catalog.BackupPath is not null) Log.Information("catalog.db was backed up to {Backup}", catalog.BackupPath);

        var added = await services.GetRequiredService<VocabularyStore>().SeedAsync();
        if (added > 0) Log.Information("Added {Count} starter vocabulary terms", added);

        var ai = services.GetRequiredService<AiSettings>();
        await ai.LoadAsync();
        Log.Information("AI: {State}", ai.Setup.IsReady ? $"on, {ai.Setup.RunProvider} model {ai.Setup.ModelName}" : "off");

        var index = await services.GetRequiredService<IndexDatabase>().InitializeAsync();
        Log.Information("index.db: {Result}", index);

        // Before the first scan, so folders in connected storage aren't taken for unreachable.
        await services.GetRequiredService<Bibliotaph.Storage.Desktop.PcDrives>().ReloadAsync();
        services.GetRequiredService<ElsewhereService>().CheckFilesWhenProjected();
    }

    /// <summary>Copies catalog.db's metadata into index.db once the index writer runs, as the Windows app does at each start.</summary>
    static void ProjectMetadataInBackground(IServiceProvider services) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await services.GetRequiredService<MetadataProjector>().ProjectAllAsync();
                services.GetRequiredService<LostCovers>().Start();
                await services.GetRequiredService<ChapterProjection>().RestoreAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Projecting metadata into index.db failed");
            }
        });
}

using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Arguments for checking a build with nobody at the Mac, as the Windows app's --screenshots: <c>--add-folder</c>
/// adds a library folder without the picker, <c>--open</c> opens a document by its id (at <c>--at-page</c>'s page), <c>--devices</c> and <c>--settings</c> open those windows (Settings at a section named after it), <c>--page</c> shows a page by its route, <c>--quit-after</c> quits after that many seconds, <c>--details-first</c> shows the first book's details (or the one at the index after it; with <c>--notes</c>, at its Notes), <c>--zoom</c> zooms the book opened, <c>--select-all</c> selects its page's text, <c>--check-download</c> checks a folder or ZIP against the library, <c>--online-reader</c> opens a page in the online reader, <c>--add-elsewhere</c> opens Add a Book I Own Elsewhere, <c>--list</c> shows the Library as a list, <c>--online</c> opens a service's browser by its key (at a saved page after it, if given), <c>--organize</c> ticks that many books in Organize mode (and <c>--bulk-edit</c> opens the editor for them), and <c>--screenshot</c> saves the
/// key window as a PNG every few seconds. The
/// app draws the picture itself, so it needs no Screen Recording permission; AppKit leaves the sidebar's glass and what's on
/// it out of such a picture.
/// </summary>
static class DevArguments
{
    public static async Task ApplyAsync(string[] args, IServiceProvider services, Views.MainWindowController main)
    {
        var window = main.Window!;
        if (args.Contains("--devices"))
        {
            main.ShowDevices();
            window = main.SettingsWindow ?? window;
        }
        if (args.Contains("--settings"))
        {
            main.ShowSettings(Enum.TryParse<SettingsSection>(Value(args, "--settings"), out var section) ? section : null);
            window = main.SettingsWindow ?? window;
        }
        if (Enum.TryParse<Presentation.Services.Route>(Value(args, "--page"), out var route)) services.GetRequiredService<INavigationService>().NavigateTo(route);
        if (int.TryParse(Value(args, "--quit-after"), out var quitAfter))
            _ = Task.Delay(TimeSpan.FromSeconds(quitAfter)).ContinueWith(_ => Bindings.OnMain(() =>
            {
                // AppKit won't quit under a sheet.
                if (main.Window?.AttachedSheet is { } sheet) main.Window.EndSheet(sheet);
                NSApplication.SharedApplication.Terminate(null);
            }), TaskScheduler.Default);
        if (Value(args, "--add-folder") is { } folder)
        {
            var root = await services.GetRequiredService<SourceRootStore>().AddAsync(Path.GetFullPath(folder));
            services.GetRequiredService<IndexingService>().RequestScan(root.Id);
            Log.Information("Added library folder {Folder} from the command line", root.Path);
        }
        if (long.TryParse(Value(args, "--open"), out var documentId))
            services.GetRequiredService<Presentation.Services.IReaderWindows>().OpenInMainWindow(new Presentation.Services.ViewerRequest(documentId, "Book", int.TryParse(Value(args, "--at-page"), out var atPage) ? atPage - 1 : 0));
        if (double.TryParse(Value(args, "--zoom"), System.Globalization.CultureInfo.InvariantCulture, out var zoom)) ZoomAsync(services, zoom).FireAndForget();
        // Once the page has put its own view back, which would set the layout again.
        if (args.Contains("--list"))
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Bindings.OnMain(() =>
            {
                if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.LibraryViewModel library) library.Layout = Index.LibraryLayout.List;
            }), TaskScheduler.Default);
        if (int.TryParse(Value(args, "--organize"), out var tick)) OrganizeAsync(services, tick, args.Contains("--bulk-edit")).FireAndForget();
        if (Value(args, "--online") is { } key
            && services.GetRequiredService<MacOnlineLibraries>() is var online && online.Services.FirstOrDefault(s => s.Reader.Key == key) is { } service)
        {
            var at = Array.IndexOf(args, "--online") + 2;
            online.OpenBrowser(service.Reader, at < args.Length && !args[at].StartsWith("--", StringComparison.Ordinal) ? new Uri(Path.GetFullPath(args[at])) : null);
        }
        if (args.Contains("--details-first")) ShowFirstDetailsAsync(services, int.TryParse(Value(args, "--details-first"), out var at) ? at : 0, args.Contains("--notes")).FireAndForget();
        if (Value(args, "--check-download") is { } download)
        {
            services.GetRequiredService<INavigationService>().NavigateTo(Presentation.Services.Route.DownloadCheck);
            if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.DownloadCheckViewModel check)
                check.StartAsync(Path.GetFullPath(download)).FireAndForget();
        }
        if (Value(args, "--online-reader") is { } page)
        {
            var readers = services.GetRequiredService<OnlineReaders>();
            services.GetRequiredService<INavigationService>().Show(
                readers.Create(new OnlineReaderRequest(new Core.EntryId(1), "Test page", new Uri(Path.GetFullPath(page)).AbsoluteUri)));
        }
        if (args.Contains("--add-elsewhere"))
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Bindings.OnMain(() => main.AddElsewhereAsync().FireAndForget()), TaskScheduler.Default);
        // Selects the text on the page being read, as Select All does, once the book has opened.
        if (args.Contains("--select-all"))
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Bindings.OnMain(() => NSApplication.SharedApplication.SendAction(new ObjCRuntime.Selector("selectAll:"), main.Window?.FirstResponder, null)),
                TaskScheduler.Default);
        if (Value(args, "--screenshot") is { } file)
        {
            var every = int.TryParse(Value(args, "--screenshot-every"), out var seconds) ? seconds : 5;
            _ = KeepTakingAsync(window, Path.GetFullPath(file), TimeSpan.FromSeconds(every));
        }
    }

    /// <summary>The open book zoomed, once its pages show, as the zoom buttons would.</summary>
    static async Task ZoomAsync(IServiceProvider services, double zoom)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            await Task.Delay(250);
            if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.ViewerViewModel { IsPdf: true } reader)
            {
                await Task.Delay(3000);
                reader.Zoom = zoom;
                return;
            }
        }
    }

    /// <summary>Organize mode with the first books ticked, once the Library has listed them.</summary>
    static async Task OrganizeAsync(IServiceProvider services, int count, bool edit)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            await Task.Delay(250);
            if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.LibraryViewModel { Items.Count: > 0 } library)
            {
                await Task.Delay(2000);
                library.Selection.StartSelectingCommand.Execute(null);
                foreach (var item in library.Items.Take(count)) library.ActivateCommand.Execute(item);
                if (edit) library.Selection.EditSelectedCommand.Execute(null);
                return;
            }
        }
    }

    /// <summary>The first book's details, once the Library has listed it, at Notes if asked.</summary>
    static async Task ShowFirstDetailsAsync(IServiceProvider services, int at, bool notes)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            await Task.Delay(250);
            if (services.GetRequiredService<Presentation.Services.INavigationService>().Current is Presentation.ViewModels.LibraryViewModel library && library.Items.Count > at)
            {
                library.ActivateCommand.Execute(library.Items[at]);
                for (var waits = 0; notes && waits < 40 && library.Inspector is null; waits++) await Task.Delay(100);
                if (notes && library.Inspector is { } inspector) inspector.IsNotesTab = true;
                return;
            }
        }
    }

    static async Task KeepTakingAsync(NSWindow window, string file, TimeSpan every)
    {
        while (window.IsVisible)
        {
            await Task.Delay(every);
            var shown = NSApplication.SharedApplication.KeyWindow ?? window;
            Take(shown.AttachedSheet ?? shown, file);
        }
    }

    static void Take(NSWindow window, string file)
    {
        if (window.ContentView?.Superview is not { } frame) return;
        using var rep = frame.BitmapImageRepForCachingDisplayInRect(frame.Bounds);
        if (rep is null) return;
        frame.CacheDisplay(frame.Bounds, rep);
        using var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
        if (png?.Save(NSUrl.CreateFileUrl(file, false, null), atomically: true) != true) Log.Warning("Saving a screenshot to {File} failed", file);
    }

    static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}

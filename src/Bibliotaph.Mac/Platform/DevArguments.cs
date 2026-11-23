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
/// adds a library folder without the picker, <c>--open</c> opens a document by its id (at <c>--at-page</c>'s page), <c>--devices</c> and <c>--settings</c> open those windows (Settings at a section named after it), <c>--page</c> shows a page by its route, <c>--quit-after</c> quits after that many seconds, <c>--details-first</c> shows the first book's details (or the one at the index after it; with <c>--notes</c>, at its Notes), <c>--zoom</c> zooms the book opened, <c>--select-all</c> selects its page's text, <c>--check-download</c> checks a folder or ZIP against the library, <c>--online-reader</c> opens a page in the online reader, <c>--add-elsewhere</c> opens Add a Book I Own Elsewhere, <c>--shortcuts</c> the Keyboard Shortcuts window, <c>--tour-shot</c> saves the tour's popover, <c>--search</c> searches (with <c>--pages</c>, at Inside documents), <c>--list</c> shows the Library as a list, <c>--online</c> opens a service's browser by its key (at a saved page after it, if given), <c>--organize</c> ticks that many books in Organize mode (and <c>--bulk-edit</c> opens the editor for them), and <c>--screenshot</c> saves the
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
        if (args.Contains("--guide"))
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Bindings.OnMain(() =>
            {
                // The search guide, open at the empty field or after what's given: "--guide system:".
                main.FocusSearch();
                var guide = services.GetRequiredService<Presentation.ViewModels.SearchGuideViewModel>();
                if (Value(args, "--guide") is { } typed && !typed.StartsWith("--", StringComparison.Ordinal))
                {
                    if (main.SearchBox is { } field) field.StringValue = typed;
                    guide.Request(typed, typed.Length);
                }
                else guide.Focused("", 0);
            }), TaskScheduler.Default);
        if (long.TryParse(Value(args, "--binder"), out var binder)) services.GetRequiredService<Presentation.Services.SessionPages>().Open(binder);
        if (Value(args, "--run") is { } command) RunAsync(services, command).FireAndForget();
        if (args.Contains("--review-samples")) ReviewSamplesAsync(services, main, Value(args, "--review-samples")).FireAndForget();
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
        if (args.Contains("--shortcuts"))
        {
            Views.ShortcutsWindow.Show();
            window = Views.ShortcutsWindow.Current ?? window;
        }
        if (Value(args, "--search") is { } words) SearchAsync(services, words, args.Contains("--pages")).FireAndForget();
        // The tour's popover, which the window's own screenshot leaves out.
        if (Value(args, "--tour-shot") is { } tourShot)
            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => Bindings.OnMain(() =>
            {
                if (Views.TourPopover.Showing is { } popover) Take(popover, Path.GetFullPath(tourShot));
            }), TaskScheduler.Default);
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
    /// <summary>
    /// A command of the page showing, by its name, once the page has settled: "--run AddPagesCommand" in the reader opens
    /// Add pages, so a dialog can be looked at without clicking to it. A dotted name reaches into the page: "Selection.X".
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The app isn't trimmed (LinkMode None).")]
    static async Task RunAsync(IServiceProvider services, string name)
    {
        await Task.Delay(3000);
        Bindings.OnMain(() =>
        {
            object? target = services.GetRequiredService<INavigationService>().Current;
            foreach (var part in name.Split('.'))
                target = target?.GetType().GetProperty(part)?.GetValue(target);
            if (target is System.Windows.Input.ICommand command && command.CanExecute(null)) command.Execute(null);
            else Log.Warning("--run {Command}: no such command on the page, or it can't run now", name);
        });
    }

    /// <summary>One made-up card of each kind on Needs review, once its own list has loaded.</summary>
    static async Task ReviewSamplesAsync(IServiceProvider services, Views.MainWindowController main, string? picture)
    {
        services.GetRequiredService<INavigationService>().NavigateTo(Presentation.Services.Route.NeedsReview);
        for (var tries = 0; tries < 40; tries++)
        {
            await Task.Delay(250);
            if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.NeedsReviewViewModel { IsLoading: false } review)
            {
                Bindings.OnMain(() =>
                {
                    foreach (var card in DevReviewSamples.Make(review)) review.Cards.Add(card);
                });
                // The whole list, past the window's foot, as one picture.
                if (picture is null || picture.StartsWith("--", StringComparison.Ordinal)) return;
                await Task.Delay(2500);
                Bindings.OnMain(() =>
                {
                    if (Tallest(main.Window!.ContentView!) is not { } list) return;
                    var rep = list.BitmapImageRepForCachingDisplayInRect(list.Bounds)!;
                    list.CacheDisplay(list.Bounds, rep);
                    rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png)!.Save(NSUrl.FromFilename(picture), true);
                });
                return;
            }
        }
    }

    static NSView? Tallest(NSView view)
    {
        NSView? best = view is NSScrollView { DocumentView: { } document } ? document : null;
        foreach (var child in view.Subviews)
            if (Tallest(child) is { } found && (best is null || found.Bounds.Height > best.Bounds.Height)) best = found;
        return best;
    }

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

    /// <summary>A search from the toolbar, at its Inside documents tab if asked, once the Library shows it.</summary>
    static async Task SearchAsync(IServiceProvider services, string words, bool pages)
    {
        await Task.Delay(2000);
        var shell = services.GetRequiredService<Presentation.ViewModels.ShellViewModel>();
        shell.SearchText = words;
        shell.SubmitSearchCommand.Execute(null);
        if (!pages) return;
        for (var tries = 0; tries < 40; tries++)
        {
            await Task.Delay(250);
            if (services.GetRequiredService<INavigationService>().Current is Presentation.ViewModels.LibraryViewModel { IsSearching: true } library)
            {
                library.IsPagesTab = true;
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
                // The first field Bibliotaph only suggests, or else the first with a value, with what it rests on open.
                if (Environment.GetCommandLineArgs().Contains("--evidence"))
                {
                    for (var waits = 0; waits < 40 && library.Inspector is not { Fields.Count: > 0 }; waits++) await Task.Delay(100);
                    await Task.Delay(1500);
                    if ((library.Inspector?.GlanceFields.FirstOrDefault(f => f.IsSuggested) ?? library.Inspector?.GlanceFields.FirstOrDefault(f => f.IsKnown)) is { } field) Bindings.OnMain(() => field.ShowEvidence = true);
                }
                return;
            }
        }
    }

    static async Task KeepTakingAsync(NSWindow window, string file, TimeSpan every)
    {
        while (window.IsVisible)
        {
            await Task.Delay(every);
            // A popover or panel that has the keyboard isn't the window to show; the window under it is.
            var shown = NSApplication.SharedApplication.KeyWindow is { ContentView.Superview: not null, IsSheet: false } key && key is not NSPanel ? key : window;
            Take(shown.AttachedSheet ?? shown, file);
            // Windows that ride on it, as the search guide does, each in a picture beside it.
            var children = window.ChildWindows ?? [];
            for (var i = 0; i < children.Length; i++)
                if (children[i].IsVisible) Take(children[i], Path.ChangeExtension(file, null) + $"-child{i}.png");
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

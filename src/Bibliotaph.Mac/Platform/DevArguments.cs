using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Arguments for checking a build with nobody at the Mac, as the Windows app's --screenshots: <c>--add-folder</c>
/// adds a library folder without the picker, and <c>--screenshot</c> saves the window as a PNG every few seconds. The
/// app draws the picture itself, so it needs no Screen Recording permission; AppKit leaves the sidebar's glass and what's on
/// it out of such a picture.
/// </summary>
static class DevArguments
{
    public static async Task ApplyAsync(string[] args, IServiceProvider services, NSWindow window)
    {
        if (Value(args, "--add-folder") is { } folder)
        {
            var root = await services.GetRequiredService<SourceRootStore>().AddAsync(Path.GetFullPath(folder));
            services.GetRequiredService<IndexingService>().RequestScan(root.Id);
            Log.Information("Added library folder {Folder} from the command line", root.Path);
        }
        if (Value(args, "--screenshot") is { } file)
        {
            var every = int.TryParse(Value(args, "--screenshot-every"), out var seconds) ? seconds : 5;
            _ = KeepTakingAsync(window, Path.GetFullPath(file), TimeSpan.FromSeconds(every));
        }
    }

    static async Task KeepTakingAsync(NSWindow window, string file, TimeSpan every)
    {
        while (window.IsVisible)
        {
            await Task.Delay(every);
            Take(window, file);
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

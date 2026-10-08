using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Bibliotaph.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --smoke-test --data-root &lt;folder&gt;</c>: opens the real window, visits every route in
/// light and dark, then exits 0, or 1 on any exception or binding error. CI runs it on Windows so a broken
/// resource or template fails the build instead of the first launch. It is not a substitute for looking.
/// </summary>
static class SmokeTest
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window)
    {
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            if (window.Icon is null) throw new InvalidOperationException("The main window has no icon.");
            // Covers are encoded on indexing threads, so check the codec off the UI thread.
            await Task.Run(CheckImageCodec);
            var navigation = services.GetRequiredService<INavigationService>();
            var theme = services.GetRequiredService<ThemeService>();
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                foreach (var route in Enum.GetValues<Route>())
                {
                    navigation.NavigateTo(route);
                    await Settle(window);
                    Log.Information("Smoke test: {Route} rendered in {Theme}", route, preference);
                }
                while (navigation.GoBack()) await Settle(window);
            }
            await theme.SetPreferenceAsync(ThemePreference.System);
            await Settle(window);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Smoke test failed");
            return 1;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
        }

        if (bindingErrors.Errors.Count > 0)
        {
            foreach (var error in bindingErrors.Errors) Log.Error("Smoke test binding error: {Error}", error);
            return 1;
        }
        Log.Information("Smoke test passed");
        return 0;
    }

    /// <summary>WIC is only on Windows, so the cover codec is checked here rather than in the unit tests.</summary>
    static void CheckImageCodec()
    {
        var codec = new WpfImageCodec();
        var pixels = new byte[16 * 8 * 4];
        Array.Fill(pixels, (byte)200);
        using var jpeg = new System.IO.MemoryStream(codec.EncodeJpeg(pixels, 16, 8, 16 * 4));
        if (codec.ReadSize(jpeg) != (16, 8)) throw new InvalidOperationException("A JPEG cover did not read back at 16x8.");
        jpeg.Position = 0;
        using var thumbnail = new System.IO.MemoryStream(codec.Thumbnail(jpeg, 4) ?? throw new InvalidOperationException("No thumbnail was made."));
        if (codec.ReadSize(thumbnail)?.Width != 4) throw new InvalidOperationException("The thumbnail is not 4 px wide.");
        if (codec.ReadSize(new System.IO.MemoryStream([1, 2, 3])) is not null) throw new InvalidOperationException("Garbage read as an image.");
        Log.Information("Smoke test: image codec works off the UI thread");
    }

    static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }
}

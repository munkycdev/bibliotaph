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

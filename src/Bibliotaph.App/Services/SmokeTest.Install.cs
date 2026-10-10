using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>Slice 4l: the sidebar's ready update, Start over only with --dev, and every shipped package's licence in About.</summary>
static partial class SmokeTest
{
    /// <summary>
    /// An update made ready by the smoke test's own update service shows in the sidebar as "Update ready: restart to
    /// update", clickable but never clicked, and goes again; the real service, in this build that wasn't installed,
    /// doesn't check and offers nothing.
    /// </summary>
    static async Task ShowUpdateReadyAsync(IServiceProvider services, Window window)
    {
        var updates = services.GetRequiredService<IUpdateService>() as SmokeUpdateService
            ?? throw new InvalidOperationException("The smoke test isn't using its own update service, so an update could restart it.");
        if (updates.IsReady || Shown(window, "UpdateReady")) throw new InvalidOperationException("The sidebar offers an update before one is ready.");

        updates.ShowReady("0.99.1");
        await WaitUntilAsync(window, () => Shown(window, "UpdateReady"), () => "A ready update doesn't show in the sidebar.");
        var button = Descendants<Button>(window).Single(b => b.Name == "UpdateReady");
        if (AutomationProperties.GetName(button) != "Update ready: restart to update"
            || !Descendants<TextBlock>(button).Any(t => t.Text == "Update ready: restart to update")
            || !Descendants<TextBlock>(button).Any(t => t.Text == "Version 0.99.1"))
            throw new InvalidOperationException("The sidebar's update doesn't say \"Update ready: restart to update\" and the version.");
        // Only checked as clickable: a click would close the app to update it.
        _ = Clickable(button, "Update ready");
        if (updates.RestartRequests != 0) throw new InvalidOperationException("Something asked to restart for the update.");
        updates.Clear();
        await WaitUntilAsync(window, () => !Shown(window, "UpdateReady"), () => "The sidebar still offers an update that is gone.");

        using var real = new UpdateService(TimeProvider.System, services.GetRequiredService<ILogger<UpdateService>>());
        real.Start();
        if (real.IsInstalled || real.IsReady) throw new InvalidOperationException("The update service takes this build for an installed one.");
    }

    /// <summary>
    /// Settings > Start over (slice 4l plan, choice 7): without --dev, as the smoke test starts, Settings doesn't list it
    /// and a link to it does nothing; with --dev it is last in the list and shows its button, in light and dark. The
    /// button is never clicked: it deletes the library and restarts.
    /// </summary>
    static async Task ShowStartOverOnlyWithDevAsync(IServiceProvider services, Window window)
    {
        var dev = services.GetRequiredService<DevMode>();
        var navigation = services.GetRequiredService<INavigationService>();
        var theme = services.GetRequiredService<ThemeService>();
        if (dev.IsOn) throw new InvalidOperationException("The smoke test started with --dev, so Start over can't be seen hidden.");

        navigation.NavigateTo(Route.Settings);
        await Settle(window);
        var settings = navigation.Current as SettingsViewModel ?? throw new InvalidOperationException("Settings didn't open.");
        if (settings.Sections.Any(s => s.Section == SettingsSection.StartOver)
            || Descendants<RadioButton>(window).Any(r => r.GroupName == "SettingsSection" && Equals(r.Content, "Start over")))
            throw new InvalidOperationException("Settings lists Start over without --dev.");
        settings.Show(SettingsSection.StartOver);
        await Settle(window);
        if (settings.Selected.Section == SettingsSection.StartOver) throw new InvalidOperationException("A link opened Start over without --dev.");
        navigation.GoBack();
        await Settle(window);

        dev.IsOn = true;
        try
        {
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                navigation.NavigateTo(Route.Settings);
                await Settle(window);
                settings = navigation.Current as SettingsViewModel ?? throw new InvalidOperationException("Settings didn't open.");
                if (settings.Sections[^1] is not StartOverSectionViewModel startOver)
                    throw new InvalidOperationException("Settings doesn't list Start over last with --dev.");
                startOver.IsSelected = true;
                await WaitUntilAsync(window, () => Descendants<Button>(window).Any(b => b.Command == startOver.StartOverCommand && b.IsVisible),
                    () => $"Settings > Start over doesn't show its button with --dev in {preference}.");
                navigation.GoBack();
                await Settle(window);
            }
        }
        finally
        {
            dev.IsOn = false;
        }
    }

    /// <summary>
    /// The About popup's licence reader lists packages/&lt;id&gt;.txt for every package in the app's and the worker's
    /// .deps.json, and PDFium's notices, as build/verify.ps1's Licenses step wrote them; and it reads one.
    /// </summary>
    static async Task ShowPackageLicencesAsync(Window window)
    {
        var shipped = Directory.GetFiles(AppContext.BaseDirectory, "*.deps.json", SearchOption.AllDirectories)
            .SelectMany(ShippedPackages).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!shipped.Contains("Velopack", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The app's .deps.json doesn't list Velopack.");

        var about = new Views.AboutDialog(AboutInfo.ForThisApp()) { Owner = window };
        try
        {
            about.Show();
            await Settle(about);
            about.ShowLicence(Path.Combine("packages", "Velopack.txt"));
            await Settle(about);
            var listed = about.LicenceFiles.Items.OfType<Choice<string>>().Select(c => c.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = shipped.Append("pdfium").Where(id => !listed.Contains($"packages/{id}.txt")).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"About lists no licence for {string.Join(", ", missing)} (build/verify.ps1 -Step Licenses writes them).");
            if (!about.LicenceTextShown.StartsWith("Velopack ", StringComparison.Ordinal) || !about.LicenceTextShown.Contains("MIT License", StringComparison.Ordinal))
                throw new InvalidOperationException("The licence reader didn't show Velopack's MIT licence.");
        }
        finally
        {
            about.Close();
        }
        await WaitUntilAsync(window, () => !Application.Current.Windows.OfType<Views.AboutDialog>().Any(), () => "The About popup didn't close.");
    }

    /// <summary>The NuGet packages a .deps.json lists, runtime packs included, as tools/LicenseNotices reads them.</summary>
    static IEnumerable<string> ShippedPackages(string depsFile)
    {
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var ids = new List<string>();
        foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var type = library.Value.GetProperty("type").GetString();
            var id = library.Name[..library.Name.LastIndexOf('/')];
            if (type == "package") ids.Add(id);
            else if (type == "runtimepack") ids.Add(id["runtimepack.".Length..]);
        }
        return ids;
    }
}

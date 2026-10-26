using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Settings > Web library, which the smoke test only opens: connecting needs bibliotaph.online and a browser.</summary>
static partial class SmokeTest
{
    static async Task ShowWebLibraryAsync(IServiceProvider services, Window window)
    {
        var page = (WebLibrarySectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.WebLibrary)).Selected;
        await WaitUntilAsync(window, () => page.StatusLine == "Not connected."
            && Descendants<Button>(window).Any(b => b.Command == page.ConnectCommand && b.IsVisible)
            && !Descendants<Button>(window).Any(b => b.Command == page.DisconnectCommand && b.IsVisible),
            () => $"Settings > Web library says {page.StatusLine ?? "nothing"}.");
        await AuditAsync(window, "Settings > Web library, not connected");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }
}

using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Settings > Devices, which the smoke test only opens: connecting needs bibliotaph.online and a browser.</summary>
static partial class SmokeTest
{
    static async Task ShowDevicesAsync(IServiceProvider services, Window window)
    {
        var page = (DevicesSectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Devices)).Selected;
        await WaitUntilAsync(window, () => page.StatusLine == "Not connected."
            && Descendants<Button>(window).Any(b => b.Command == page.ConnectCommand && b.IsVisible)
            && !Descendants<Button>(window).Any(b => b.Command == page.DisconnectCommand && b.IsVisible),
            () => $"Settings > Devices says {page.StatusLine ?? "nothing"}.");
        await AuditAsync(window, "Settings > Devices, not connected");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }
}

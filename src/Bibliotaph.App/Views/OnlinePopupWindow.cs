using System.Runtime.InteropServices;
using System.Windows;
using Bibliotaph.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bibliotaph.App.Views;

/// <summary>
/// A window a page in Bibliotaph's browser opened: a sign-in popup, or a store's Download button opening a new tab.
/// Bibliotaph hosts it rather than leaving it to WebView2's own window, so a download started there goes to the
/// downloads folder like any other, and the page that opened it keeps talking to it as sign-ins need. A window that
/// only downloads is hidden, and closes when its download is done.
/// </summary>
public sealed class OnlinePopupWindow : Window
{
    readonly OnlineBrowsers _browsers;
    readonly string _serviceName;
    readonly ILogger _log;
    bool _showsPage;

    OnlinePopupWindow(OnlineBrowsers browsers, string serviceName, ILogger log)
    {
        (_browsers, _serviceName, _log) = (browsers, serviceName, log);
        Title = $"{serviceName} · Bibliotaph";
        (Width, Height, MinWidth, MinHeight) = (720, 760, 360, 300);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bt.Panel");
        var web = new WebView2();
        System.Windows.Automation.AutomationProperties.SetName(web, $"{serviceName} page");
        Content = web;
        Closed += (_, _) => web.Dispose();
    }

    /// <summary>
    /// Hosts the window <paramref name="e"/> asks for, or leaves it to WebView2 when Bibliotaph's can't start. Must be
    /// called before the event handler returns, so it takes the deferral itself.
    /// </summary>
    public static async Task HostAsync(CoreWebView2NewWindowRequestedEventArgs e, Window owner, OnlineBrowsers browsers, string serviceName, ILogger log)
    {
        var deferral = e.GetDeferral();
        try
        {
            var popup = new OnlinePopupWindow(browsers, serviceName, log) { Owner = owner };
            if (e.WindowFeatures is { HasSize: true } features)
                (popup.Width, popup.Height) = (Math.Max(popup.MinWidth, features.Width), Math.Max(popup.MinHeight, features.Height));
            popup.Show();
            await popup.Web.EnsureCoreWebView2Async(await browsers.EnvironmentAsync());
            popup.Hook(popup.Web.CoreWebView2);
            e.NewWindow = popup.Web.CoreWebView2;
            e.Handled = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Bibliotaph's browser couldn't host a window the page opened, so WebView2 opens it");
        }
        finally
        {
            deferral.Complete();
        }
    }

    WebView2 Web => (WebView2)Content;

    void Hook(CoreWebView2 core)
    {
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? $"{_serviceName} · Bibliotaph" : $"{core.DocumentTitle} · Bibliotaph";
        core.WindowCloseRequested += (_, _) => Close();
        core.NewWindowRequested += async (_, e) => await HostAsync(e, this, _browsers, _serviceName, _log);
        // A blank tab a page writes into shows nothing either; only a page loaded from the web counts.
        core.NavigationCompleted += (_, e) => _showsPage |= e.IsSuccess && Uri.TryCreate(core.Source, UriKind.Absolute, out var shown) && shown.Scheme is "http" or "https" or "file";
        core.DownloadStarting += async (_, e) =>
        {
            var download = await _browsers.Downloads.TakeAsync(e, _serviceName);
            if (_showsPage) return;
            // A tab opened only to download shows nothing: it goes away now, and closes once the file is saved.
            Hide();
            Owner?.Activate();
            if (download.InProgress) download.Finished += (_, _) => Dispatcher.BeginInvoke(Close);
            else Close();
        };
    }
}

using System.Windows;
using System.Windows.Interop;
using Bibliotaph.App.Views;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Bibliotaph.App.Services;

/// <summary>
/// The in-app browser (online libraries plan, choices 1 to 3): one window per service, showing the service's own pages
/// in WebView2 with a profile of its own under <see cref="AppPaths.Browser"/>, so its sign-ins last between imports and
/// stay apart from the user's everyday browser. It never navigates on its own: it opens the library page once, and
/// from there the user browses. Read this page hands the page in view to the service's reader.
/// </summary>
public sealed class OnlineBrowsers(AppPaths paths, SiteIcons icons, SiteIconImages iconImages, OnlineDownloads downloads, ILoggerFactory logs)
{
    readonly Dictionary<string, OnlineBrowserWindow> _open = [];
    Task<CoreWebView2Environment>? _environment;

    /// <summary>Store downloads from every window (O5).</summary>
    public OnlineDownloads Downloads => downloads;

    /// <summary>The smoke test's switch: browsers open only files, so no real site is read.</summary>
    public bool OnlyFiles { get; set; }

    /// <summary>The windows open now, one per service.</summary>
    public IReadOnlyCollection<OnlineBrowserWindow> Windows => _open.Values;

    /// <summary>The Edge WebView2 Runtime's version, or null when it isn't installed (Windows 11 has it).</summary>
    public static string? RuntimeVersion
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>One browser for every window, so they share the profile's sign-ins.</summary>
    public async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        _environment ??= CoreWebView2Environment.CreateAsync(null, paths.Browser);
        try
        {
            return await _environment;
        }
        catch
        {
            _environment = null;
            throw;
        }
    }

    /// <summary>
    /// Opens the service's browser window at its library page, or <paramref name="start"/> (the smoke test's made-up
    /// page), or brings it forward when it is open. <paramref name="read"/> takes what Read this page read and says
    /// what happened.
    /// </summary>
    public OnlineBrowserWindow Open(IOnlineLibraryReader reader, Func<OnlinePage, Func<string, CancellationToken, Task<byte[]?>>, Task<string>> read,
        Uri? start = null)
    {
        if (_open.TryGetValue(reader.Key, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var window = new OnlineBrowserWindow(this, reader, start ?? reader.LibraryPage, read, logs.CreateLogger<OnlineBrowserWindow>());
        _open[reader.Key] = window;
        window.Closed += (_, _) => _open.Remove(reader.Key);
        window.Show();
        return window;
    }

    /// <summary>
    /// Keeps the icon the browser shows for a site, for the badge on that site's books (choice 12), unless site icons are
    /// off in Settings.
    /// </summary>
    internal async Task KeepIconAsync(string url, byte[] png)
    {
        if (!await iconImages.IsOnAsync() || SiteIcons.HostOf(url) is not { } host) return;
        if (await Task.Run(() => icons.Keep(host, png)) is not null) iconImages.Kept(host);
    }

    /// <summary>
    /// Sign out of all services (choice 2): the browser's windows close and its profile forgets every sign-in, cookie
    /// and cached page. The browser clears them itself; Bibliotaph deletes no file.
    /// </summary>
    public async Task SignOutAsync()
    {
        foreach (var window in _open.Values.ToList()) window.Close();
        var environment = await EnvironmentAsync();
        var owner = new WindowInteropHelper(Application.Current.MainWindow).Handle;
        var controller = await environment.CreateCoreWebView2ControllerAsync(owner);
        try
        {
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        finally
        {
            controller.Close();
        }
    }
}

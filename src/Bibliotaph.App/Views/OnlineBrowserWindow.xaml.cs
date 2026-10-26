using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Bibliotaph.App.Services;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Bibliotaph.App.Views;

/// <summary>
/// The in-app browser's window for one service. It opens the service's library page once and never navigates, clicks
/// or scrolls by itself. As each page shows, the service's reader looks at it, and Read this page lights up when it
/// lists the user's books; pressing it hands those books, and the cover images the page loaded, to Settings &gt; Online
/// libraries. Nothing is fetched for them. The site's icon is kept for the badge on its books.
/// </summary>
public partial class OnlineBrowserWindow
{
    /// <summary>Images the pages showed, kept for covers; past this much, no more are kept.</summary>
    const long MaxImageBytes = 64 * 1024 * 1024;

    const int MaxImage = 8 * 1024 * 1024;

    readonly OnlineBrowsers _browsers;
    readonly IOnlineLibraryReader _reader;
    readonly Uri _start;
    readonly Func<OnlinePage, Func<string, CancellationToken, Task<byte[]?>>, Task<string>> _read;
    readonly ILogger _log;
    readonly ConcurrentDictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
    long _imageBytes;
    int _checks;
    OnlinePage? _page;

    public OnlineBrowserWindow(OnlineBrowsers browsers, IOnlineLibraryReader reader, Uri start,
        Func<OnlinePage, Func<string, CancellationToken, Task<byte[]?>>, Task<string>> read, ILogger log)
    {
        InitializeComponent();
        (_browsers, _reader, _start, _read, _log) = (browsers, reader, start, read, log);
        State = new OnlineBrowserState(reader.Name) { Downloads = browsers.Downloads.Items };
        DataContext = State;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Web.Dispose();
    }

    public OnlineBrowserState State { get; }

    async Task StartAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async(await _browsers.EnvironmentAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            State.Status = "Bibliotaph's browser needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes. Install it from Microsoft.";
            return;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The in-app browser couldn't start");
            State.Status = $"Bibliotaph's browser couldn't start: {ex.Message}";
            return;
        }

        var core = Web.CoreWebView2;
        core.NavigationStarting += (_, e) =>
        {
            _checks++;
            _page = null;
            State.CanRead = false;
            State.Address = e.Uri;
        };
        core.SourceChanged += (_, _) => State.Address = core.Source;
        core.HistoryChanged += (_, _) =>
        {
            (State.CanGoBack, State.CanGoForward) = (core.CanGoBack, core.CanGoForward);
            CheckSoon();
        };
        core.DOMContentLoaded += (_, _) => CheckSoon();
        core.NavigationCompleted += (_, _) => CheckSoon();
        core.FaviconChanged += async (_, _) => await KeepIconAsync(core);
        core.WebResourceResponseReceived += async (_, e) => await KeepImageAsync(e);
        core.DownloadStarting += async (_, e) => await _browsers.Downloads.TakeAsync(e, _reader.Name);
        core.NewWindowRequested += async (_, e) => await OnlinePopupWindow.HostAsync(e, this, _browsers, _reader.Name, _log);
        // A library that changes without a new page (a filter chosen, more books drawn) is looked at again too.
        core.WebMessageReceived += (_, _) => CheckSoon();
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WatchChanges);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.LogInformation("The in-app browser can't watch pages change: {Message}", ex.Message);
        }
        State.Status = $"Sign in on {_reader.Name}'s own page if it asks, and open your library. Read this page lights up when the page lists your books.";
        if (_reader.LinkField != MetadataFields.AlsoOwn)
            State.Status += await _browsers.Downloads.FolderAsync() is null
                ? " Its Download buttons save to your Downloads folder; choose a downloads folder in Settings > Online libraries to have Bibliotaph read what you download."
                : " Its Download buttons save into your downloads folder, and Bibliotaph reads each file.";
        core.Navigate(_start.AbsoluteUri);
    }

    /// <summary>Looks at the page once it has settled: a page still drawing is looked at again when it is done.</summary>
    async void CheckSoon()
    {
        var mine = ++_checks;
        await Task.Delay(400);
        if (mine != _checks || Web.CoreWebView2 is not { } core) return;
        try
        {
            var html = await MarkupAsync(core);
            var (page, hint) = await Task.Run(() => _reader.Read(html) is { } read ? (read, null) : ((OnlinePage?)null, _reader.Hint(html)));
            if (mine != _checks) return;
            _page = page;
            State.CanRead = page is not null;
            State.Status = page switch
            {
                null => hint ?? $"Open your library on {_reader.Name}, signed in. Read this page lights up when the page lists your books; if it doesn't, reload the page.",
                { Books.Count: 1 } => "This page lists 1 book you own.",
                _ => $"This page lists {page.Books.Count} books you own.",
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException)
        {
            // The page went away while it was being read; the next one is looked at in turn.
            _log.LogDebug(ex, "The in-app browser couldn't look at the page");
        }
    }

    async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_page is not { } page) return;
        State.CanRead = false;
        try
        {
            State.Status = await _read(page, CoverAsync);
        }
        finally
        {
            State.CanRead = _page is not null;
        }
    }

    /// <summary>
    /// The page's markup as it shows now. What the user chose or typed in a list or a search box (Humble Bundle's
    /// Platform) lives in the page, not its markup, so it is written into the markup first. Passwords, and what is typed
    /// in a form that asks for one, never are.
    /// </summary>
    static async Task<string> MarkupAsync(CoreWebView2 core) => JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Snapshot)) ?? "";

    const string Snapshot = """
        (() => {
          for (const list of document.querySelectorAll('select'))
            for (const option of list.options) option.toggleAttribute('selected', option.selected);
          for (const input of document.querySelectorAll('input')) {
            const type = (input.getAttribute('type') || 'text').toLowerCase();
            if (type === 'checkbox' || type === 'radio') input.toggleAttribute('checked', input.checked);
            else if ((type === 'text' || type === 'search') && !input.form?.querySelector('input[type=password]')) input.setAttribute('value', input.value);
          }
          return document.documentElement.outerHTML;
        })()
        """;

    /// <summary>Tells Bibliotaph, at most every second and a half, that the page has changed without a new page.</summary>
    const string WatchChanges = """
        (() => {
          let waiting = false;
          const changed = () => {
            if (waiting) return;
            waiting = true;
            setTimeout(() => { waiting = false; window.chrome?.webview?.postMessage('changed'); }, 1500);
          };
          document.addEventListener('change', changed, true);
          new MutationObserver(changed).observe(document, { childList: true, subtree: true });
        })();
        """;

    /// <summary>
    /// A cover the pages showed: by its address; else one at the same address with another query, as a site can resize
    /// it (Next.js) or sign it; else the one image with its file name.
    /// </summary>
    Task<byte[]?> CoverAsync(string url, CancellationToken ct)
    {
        if (_images.TryGetValue(url, out var image)) return Task.FromResult<byte[]?>(image);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Task.FromResult<byte[]?>(null);
        var kept = _images.Select(i => (Uri: Uri.TryCreate(i.Key, UriKind.Absolute, out var at) ? at : null, Bytes: i.Value)).Where(i => i.Uri is not null).ToList();
        var path = uri.GetLeftPart(UriPartial.Path);
        if (kept.FirstOrDefault(i => i.Uri!.GetLeftPart(UriPartial.Path).Equals(path, StringComparison.OrdinalIgnoreCase)).Bytes is { } same)
            return Task.FromResult<byte[]?>(same);
        var name = uri.Segments[^1];
        var named = name.Length > 1 ? kept.Where(i => i.Uri!.Segments[^1].Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList() : [];
        return Task.FromResult(named is [var only] ? only.Bytes : null);
    }

    async Task KeepImageAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            if (e.Response.StatusCode != 200 || Interlocked.Read(ref _imageBytes) > MaxImageBytes) return;
            var headers = e.Response.Headers;
            if (!headers.Contains("Content-Type") || !headers.GetHeader("Content-Type").StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;
            using var content = await e.Response.GetContentAsync();
            if (content is null) return;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            if (copy.Length is 0 or > MaxImage) return;
            var bytes = copy.ToArray();
            if (_images.TryAdd(ImageKey(e.Request.Uri), bytes)) Interlocked.Add(ref _imageBytes, bytes.Length);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException)
        {
            // An image the browser no longer has: the cover stays a placeholder.
        }
    }

    /// <summary>The address an image is for: Next.js serves resized images at /_next/image?url=…, keyed by the original.</summary>
    internal static string ImageKey(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/_next/image") return address;
        var original = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
        return original is not null && Uri.TryCreate(uri, original, out var resolved) ? resolved.AbsoluteUri : address;
    }

    async Task KeepIconAsync(CoreWebView2 core)
    {
        try
        {
            if (string.IsNullOrEmpty(core.FaviconUri)) return;
            using var icon = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (icon is null) return;
            using var copy = new MemoryStream();
            await icon.CopyToAsync(copy);
            if (copy.Length > 0) await _browsers.KeepIconAsync(core.Source, copy.ToArray());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException)
        {
            _log.LogInformation("The site icon of {Address} couldn't be kept: {Message}", core.Source, ex.Message);
        }
    }

    void Back_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoBack();

    void Forward_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoForward();

    void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Web.CoreWebView2?.Reload();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _checks++;
        base.OnClosing(e);
    }
}

/// <summary>What the in-app browser's toolbar shows.</summary>
public sealed partial class OnlineBrowserState(string serviceName) : ObservableObject
{
    public string ServiceName { get; } = serviceName;

    public string WindowTitle => $"{ServiceName} · Bibliotaph";

    [ObservableProperty]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "Starting the browser…";

    [ObservableProperty]
    public partial bool CanRead { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    /// <summary>This session's store downloads, listed under the page.</summary>
    public ObservableCollection<OnlineDownload> Downloads
    {
        get;
        init
        {
            field = value;
            value.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDownloads));
        }
    } = [];

    public bool HasDownloads => Downloads.Count > 0;
}

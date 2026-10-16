using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.Ebooks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bibliotaph.App.Views;

/// <summary>
/// The e-book reader's page surface: the book's reader page in WebView2, answered from the book alone, and its
/// script driven for the reader. The page says which page is in view, the reader goes to pages and finds text
/// through it, and links and keys come back as events. Nothing on the page reaches the network, opens a window or
/// downloads: the browser refuses every address but the book's own.
/// </summary>
public partial class EbookView
{
    const string FrameCsp = $"default-src 'none'; style-src https://{EbookBrowser.Host} 'unsafe-inline'; img-src https://{EbookBrowser.Host}; "
        + $"font-src https://{EbookBrowser.Host}; base-uri 'none'; form-action 'none'";
    const string FileCsp = $"default-src 'none'; style-src 'unsafe-inline'; img-src https://{EbookBrowser.Host}";

    Task<CoreWebView2?>? _starting;
    EbookContent? _content;
    string _page = "";
    string _pageCsp = "";
    int _version;
    TaskCompletionSource<bool> _ready = NewReady();
    int _start;
    int _current;
    bool _fitPage;
    bool _gone;

    public EbookView()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            // The browser is let go as soon as the reader isn't shown, and a new one is made if it shows again.
            _gone = true;
            _version++;
            NotShowing();
            Web.Dispose();
        };
    }

    void Revive()
    {
        if (!_gone) return;
        var web = new WebView2CompositionControl();
        AutomationProperties.SetName(web, AutomationProperties.GetName(Web));
        Surface.Children.Remove(Web);
        Surface.Children.Insert(0, web);
        Web = web;
        _starting = null;
        _gone = false;
    }

    static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.None);

    /// <summary>Lets anything waiting for the page go, and says from now on that no page is showing.</summary>
    void NotShowing()
    {
        _ready.TrySetResult(false);
        _ready = NewReady();
        _ready.TrySetResult(false);
    }

    /// <summary>Set by the reader view: the browser the page is shown in.</summary>
    public EbookBrowser? Browser { get; set; }

    /// <summary>The page in view changed, as the reader scrolled or went to a page.</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>A link to another place in the book was followed, from the page given, so there's a way back.</summary>
    public event EventHandler<int>? LinkFollowed;

    /// <summary>A web or mail link in the book was clicked.</summary>
    public event EventHandler<string>? WebLinkClicked;

    /// <summary>Ctrl+F, F3 or Shift+F3 while the page had the keyboard: "find", "next" or "previous".</summary>
    public event EventHandler<string>? KeyPressed;

    /// <summary>Ctrl+wheel or Ctrl+plus, minus or 0 on the page: 1 bigger, -1 smaller, 0 back to the usual size.</summary>
    public event EventHandler<int>? ZoomRequested;

    /// <summary>The browser couldn't start, or stopped, and says why.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>The page with selected text on it, or null when nothing is selected.</summary>
    public int? SelectionPageIndex { get; private set; }

    /// <summary>The book's page is loaded and shown.</summary>
    public bool IsShowing => _ready.Task is { IsCompletedSuccessfully: true, Result: true };

    /// <summary>Shows <paramref name="content"/>'s page at <paramref name="page"/>.</summary>
    public async void Show(EbookContent content, int page)
    {
        var version = ++_version;
        _ready.TrySetResult(false);
        _ready = NewReady();
        _content = content;
        _start = page;
        _current = page;
        SelectionPageIndex = null;
        Message.Visibility = Visibility.Collapsed;
        Revive();
        var core = await StartAsync();
        if (core is null || version != _version || _gone) return;
        _page = content.Page(ThemeCss());
        _pageCsp = $"default-src 'none'; script-src 'nonce-{content.Nonce}'; style-src 'nonce-{content.Nonce}'; img-src https://{EbookBrowser.Host}; "
            + $"font-src https://{EbookBrowser.Host}; frame-src https://{EbookBrowser.Host}; media-src 'none'; base-uri 'none'; form-action 'none'";
        try
        {
            core.Navigate($"https://{EbookBrowser.Host}{EbookBrowser.PagePath}?v={version.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Fail($"The e-book reader couldn't show the book: {ex.Message}");
        }
    }

    /// <summary>Closes the book: the page goes blank, and the book's files are no longer asked for.</summary>
    public void Unload()
    {
        _version++;
        NotShowing();
        _content = null;
        _page = "";
        SelectionPageIndex = null;
        if (_gone || Web.CoreWebView2 is not { } core) return;
        try
        {
            core.Navigate("about:blank");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The browser went meanwhile.
        }
    }

    /// <summary>Goes to a page; before the book shows, it opens there.</summary>
    public void GoTo(int page)
    {
        if (!IsShowing)
        {
            _start = page;
            return;
        }
        _ = RunAsync($"bt.goTo({page.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>Every match of <paramref name="text"/> in the book, up to <paramref name="most"/>, as the page each is on.</summary>
    public async Task<IReadOnlyList<int>> FindAsync(string text, int most) =>
        Pages(await RunAsync($"bt.find({JsonSerializer.Serialize(text)},{most.ToString(CultureInfo.InvariantCulture)})"));

    /// <summary>The words of a search marked on the page it found, as the page each is on.</summary>
    public async Task<IReadOnlyList<int>> MarkTermsAsync(IReadOnlyList<string> terms, int page) =>
        Pages(await RunAsync($"bt.terms({JsonSerializer.Serialize(terms)},{page.ToString(CultureInfo.InvariantCulture)})"));

    /// <summary>Marks the matches of the last find (<c>find</c>) or the search's words (<c>terms</c>), or none.</summary>
    public void ShowMatches(string? list, int current) =>
        _ = RunAsync($"bt.show({(list is null ? "null" : JsonSerializer.Serialize(list))},{current.ToString(CultureInfo.InvariantCulture)})");

    /// <summary>The page's size: 1 is the usual size.</summary>
    public void SetZoom(double zoom, bool fitPage)
    {
        try
        {
            Web.ZoomFactor = zoom;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Not started yet: the factor is set when it is.
        }
        if (_fitPage == fitPage) return;
        _fitPage = fitPage;
        if (IsShowing) _ = RunAsync($"bt.setFit('{(fitPage ? "page" : "width")}')");
    }

    /// <summary>Runs a line of the page's script once the book shows, and gives its result as JSON; null when it couldn't.</summary>
    public async Task<string?> RunAsync(string script)
    {
        var version = _version;
        if (!await _ready.Task || version != _version || _gone || Web.CoreWebView2 is not { } core) return null;
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    static int[] Pages(string? json)
    {
        if (json is null or "null") return [];
        try
        {
            return JsonSerializer.Deserialize<int[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    Task<CoreWebView2?> StartAsync() => _starting ??= StartCoreAsync();

    async Task<CoreWebView2?> StartCoreAsync()
    {
        if (Browser is not { } browser) return null;
        try
        {
            if (TryFindResource("Bt.SideColor") is Color side) Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(side.R, side.G, side.B);
            await Web.EnsureCoreWebView2Async(await browser.EnvironmentAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("Bibliotaph's e-book reader needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes.");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Fail($"Bibliotaph's e-book reader couldn't start: {ex.Message}");
            return null;
        }
        if (_gone) return null;

        var core = Web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsWebMessageEnabled = true;

        // Every request is answered here: the book's page and files, or a refusal.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) => e.Cancel = !IsPage(e.Uri) && e.Uri != "about:blank";
        core.FrameNavigationStarting += (_, e) => e.Cancel = !IsBookFile(e.Uri);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.ContextMenuRequested += (_, e) =>
        {
            // Copy and Select all are what a book's page needs; the browser's Back, Save as and Print aren't.
            var kept = e.MenuItems.Where(i => i.Name is "copy" or "selectAll").ToList();
            e.MenuItems.Clear();
            foreach (var item in kept) e.MenuItems.Add(item);
            if (kept.Count == 0) e.Handled = true;
        };
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _starting = null;
                Fail("Bibliotaph's e-book reader stopped. Open the book again to go on reading.");
            }
            else if (_content is { } content) Show(content, _current);
        };
        return core;
    }

    void Fail(string message)
    {
        Message.Text = message;
        Message.Visibility = Visibility.Visible;
        Failed?.Invoke(this, message);
    }

    static bool IsPage(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == EbookBrowser.Host
        && uri.AbsolutePath == EbookBrowser.PagePath;

    static bool IsBookFile(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == EbookBrowser.Host
        && EbookReaderDocument.ResourcePath(uri.AbsolutePath) is not null;

    async void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != EbookBrowser.Host)
        {
            e.Response = Refuse(core);
            return;
        }
        if (uri.AbsolutePath == EbookBrowser.PagePath)
        {
            e.Response = _page.Length == 0 ? Refuse(core) : Respond(core, Encoding.UTF8.GetBytes(_page), "text/html; charset=utf-8", _pageCsp);
            return;
        }
        if (EbookReaderDocument.ResourcePath(uri.AbsolutePath) is not { } path || _content is not { } content)
        {
            e.Response = Refuse(core);
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var found = await Task.Run(() => content.Read(path));
            e.Response = found is { } file
                ? Respond(core, file.Bytes, file.MediaType == "text/html" ? "text/html; charset=utf-8" : file.MediaType, file.MediaType == "text/html" ? FrameCsp : FileCsp)
                : Refuse(core);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The book or the browser closed meanwhile.
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Nothing is kept in the browser's cache: two books can have a file at the same path.
    static CoreWebView2WebResourceResponse Respond(CoreWebView2 core, byte[] bytes, string type, string csp) =>
        core.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
            $"Content-Type: {type}\r\nContent-Security-Policy: {csp}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");

    static CoreWebView2WebResourceResponse Refuse(CoreWebView2 core) =>
        core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the book's page talks; its frames have no script.
        if (!IsPage(e.Source) || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || source.Query != $"?v={_version.ToString(CultureInfo.InvariantCulture)}") return;
        JsonElement message;
        try
        {
            message = JsonElement.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("t", out var kind)) return;
        int Number(string name) => message.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : -1;
        switch (kind.GetString())
        {
            case "ready":
                _ = Web.CoreWebView2?.ExecuteScriptAsync(
                    $"bt.setFit('{(_fitPage ? "page" : "width")}');bt.start({_start.ToString(CultureInfo.InvariantCulture)})");
                _ready.TrySetResult(true);
                break;
            case "page" when Number("page") is >= 0 and var page:
                _current = page;
                CurrentPageChanged?.Invoke(this, page);
                break;
            case "selection":
                SelectionPageIndex = Number("page") is >= 0 and var selected ? selected : null;
                break;
            case "linked" when Number("from") is >= 0 and var from:
                LinkFollowed?.Invoke(this, from);
                break;
            case "web" when message.TryGetProperty("url", out var url) && url.GetString() is { } address:
                WebLinkClicked?.Invoke(this, address);
                break;
            case "find" or "next" or "previous":
                KeyPressed?.Invoke(this, kind.GetString()!);
                break;
            case "zoom" when message.TryGetProperty("step", out var step) && step.TryGetInt32(out var by):
                ZoomRequested?.Invoke(this, Math.Sign(by));
                break;
        }
    }

    /// <summary>The theme's colours for the page, so the book reads like the rest of the app.</summary>
    string ThemeCss()
    {
        Color Of(string key, Color fallback) => TryFindResource(key) is Color color ? color : fallback;
        static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        var bg = Of("Bt.BgColor", Colors.White);
        var accent = Of("Bt.AccentColor", Color.FromRgb(0x3c, 0x6e, 0x47));
        var dark = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B < 128;
        return ":root{" + $"color-scheme:{(dark ? "dark" : "light")};--bt-bg:{Hex(bg)};--bt-side:{Hex(Of("Bt.SideColor", Colors.WhiteSmoke))};"
            + $"--bt-text:{Hex(Of("Bt.TextColor", Colors.Black))};--bt-muted:{Hex(Of("Bt.MutedColor", Colors.DimGray))};"
            + $"--bt-line:{Hex(Of("Bt.LineColor", Colors.LightGray))};--bt-accent:{Hex(accent)};"
            + $"--bt-accent-text:{Hex(Of("Bt.AccentTextColor", Colors.White))};"
            + $"--bt-hit:rgba({accent.R},{accent.G},{accent.B},0.4)" + "}";
    }
}

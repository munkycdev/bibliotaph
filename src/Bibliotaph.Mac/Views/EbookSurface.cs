using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppKit;
using Bibliotaph.Ebooks;
using Bibliotaph.Presentation.Services;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// An e-book's page in a WebKit view, as the Windows reader shows it in WebView2: the book's page and its files are
/// served from memory under a scheme of its own, its script talks to the reader through a message handler, and nothing
/// on the page reaches the network, opens a window or downloads.
/// </summary>
public sealed class EbookSurface : NSView
{
    const string Scheme = "btbook";
    const string Host = "book";
    const string PagePath = "/book.html";
    const string FrameCsp = "default-src 'none'; style-src btbook: 'unsafe-inline'; img-src btbook:; font-src btbook:; base-uri 'none'; form-action 'none'";
    const string FileCsp = "default-src 'none'; style-src 'unsafe-inline'; img-src btbook:";

    // The page's script speaks to WebView2; here the same call goes to WebKit's handler.
    const string Bridge = "window.chrome={webview:{postMessage:m=>window.webkit.messageHandlers.bt.postMessage(m)}};";

    readonly WKWebView _web;
    readonly SchemeHandler _files;
    EbookContent? _content;
    string _page = "";
    string _pageCsp = "";
    int _version;
    TaskCompletionSource<bool> _ready = Ready();
    int _start;
    bool _fitPage;

    public EbookSurface()
    {
        _files = new SchemeHandler(this);
        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
        configuration.SetUrlSchemeHandler(_files, Scheme);
        configuration.Preferences.JavaScriptCanOpenWindowsAutomatically = false;
        configuration.UserContentController.AddUserScript(new WKUserScript(new NSString(Bridge), WKUserScriptInjectionTime.AtDocumentStart, true));
        configuration.UserContentController.AddScriptMessageHandler(new Messages(this), "bt");
        _web = new WKWebView(CGRect.Empty, configuration) { NavigationDelegate = new Navigation(), TranslatesAutoresizingMaskIntoConstraints = false };
        AddSubview(_web);
        NSLayoutConstraint.ActivateConstraints(
        [
            _web.TopAnchor.ConstraintEqualTo(TopAnchor),
            _web.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _web.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
        ]);
    }

    public EbookSurface(NativeHandle handle) : base(handle)
    {
        _web = null!;
        _files = null!;
    }

    /// <summary>The page in view changed, as the reader scrolled or went to a page.</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>A web or mail link in the book was clicked.</summary>
    public event EventHandler<string>? WebLinkClicked;

    /// <summary>Command-plus, minus or 0 on the page: 1 bigger, -1 smaller, 0 back to the usual size.</summary>
    public event EventHandler<int>? ZoomRequested;

    bool IsShowing => _ready.Task is { IsCompletedSuccessfully: true, Result: true };

    static TaskCompletionSource<bool> Ready() => new(TaskCreationOptions.None);

    public void Show(EbookContent content, int page)
    {
        var version = ++_version;
        _ready.TrySetResult(false);
        _ready = Ready();
        _content = content;
        _start = page;
        _page = content.Page(ThemeCss());
        _pageCsp = $"default-src 'none'; script-src 'nonce-{content.Nonce}'; style-src 'nonce-{content.Nonce}'; img-src btbook:; font-src btbook:; "
            + "frame-src btbook:; media-src 'none'; base-uri 'none'; form-action 'none'";
        _web.LoadRequest(new NSUrlRequest(new NSUrl($"{Scheme}://{Host}{PagePath}?v={version.ToString(CultureInfo.InvariantCulture)}")));
    }

    public void Unload()
    {
        _version++;
        _ready.TrySetResult(false);
        _ready = Ready();
        _content = null;
        _page = "";
        _web.LoadHtmlString("", null!);
    }

    public void GoTo(int page)
    {
        if (!IsShowing)
        {
            _start = page;
            return;
        }
        _ = RunAsync($"bt.goTo({page.ToString(CultureInfo.InvariantCulture)})");
    }

    public async Task<IReadOnlyList<int>> FindAsync(string text, int most) =>
        Pages(await RunAsync($"bt.find({JsonSerializer.Serialize(text, EbookJson.Default.String)},{most.ToString(CultureInfo.InvariantCulture)})"));

    public async Task<IReadOnlyList<int>> MarkTermsAsync(IReadOnlyList<string> terms, int page) =>
        Pages(await RunAsync($"bt.terms({JsonSerializer.Serialize(terms.ToArray(), EbookJson.Default.StringArray)},{page.ToString(CultureInfo.InvariantCulture)})"));

    public void ShowMatches(string? list, int current) =>
        _ = RunAsync($"bt.show({(list is null ? "null" : JsonSerializer.Serialize(list, EbookJson.Default.String))},{current.ToString(CultureInfo.InvariantCulture)})");

    public void SetZoom(double zoom, bool fitPage)
    {
        _web.PageZoom = (nfloat)zoom;
        if (_fitPage == fitPage) return;
        _fitPage = fitPage;
        if (IsShowing) _ = RunAsync($"bt.setFit('{(fitPage ? "page" : "width")}')");
    }

    /// <summary>Runs a line of the page's script once the book shows, and gives its result as JSON; null when it couldn't.</summary>
    async Task<string?> RunAsync(string script)
    {
        var version = _version;
        if (!await _ready.Task || version != _version) return null;
        var done = new TaskCompletionSource<string?>();
        _web.EvaluateJavaScript(script, (result, error) => done.TrySetResult(error is null ? Json(result) : null));
        return await done.Task;
    }

    static string? Json(NSObject? value)
    {
        if (value is null) return null;
        if (value is NSNumber number) return number.ToString();
        if (value is NSString text) return JsonSerializer.Serialize(text.ToString(), EbookJson.Default.String);
        var data = NSJsonSerialization.Serialize(value, 0, out var error);
        return error is null ? data.ToString(NSStringEncoding.UTF8) : null;
    }

    static int[] Pages(string? json)
    {
        if (json is null or "null") return [];
        try
        {
            return JsonSerializer.Deserialize(json, EbookJson.Default.Int32Array) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void OnMessage(NSObject body)
    {
        if (Json(body) is not { } json) return;
        JsonElement message;
        try
        {
            message = JsonElement.Parse(json);
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
                _web.EvaluateJavaScript($"bt.setFit('{(_fitPage ? "page" : "width")}');bt.start({_start.ToString(CultureInfo.InvariantCulture)})", null!);
                _ready.TrySetResult(true);
                break;
            case "page" when Number("page") is >= 0 and var page:
                CurrentPageChanged?.Invoke(this, page);
                break;
            case "web" when message.TryGetProperty("url", out var url) && url.GetString() is { } address:
                WebLinkClicked?.Invoke(this, address);
                break;
            case "zoom" when message.TryGetProperty("step", out var step) && step.TryGetInt32(out var by):
                ZoomRequested?.Invoke(this, Math.Sign(by));
                break;
        }
    }

    /// <summary>The app's colours for the page, so the book reads like the rest of the app, light or dark.</summary>
    string ThemeCss()
    {
        var dark = EffectiveAppearance.Name.Contains("Dark", StringComparison.Ordinal);
        static string Hex(NSColor color)
        {
            var rgb = color.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? color;
            return $"#{(int)(rgb.RedComponent * 255):x2}{(int)(rgb.GreenComponent * 255):x2}{(int)(rgb.BlueComponent * 255):x2}";
        }
        string Resolved(NSColor color)
        {
            var hex = "";
            EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => hex = Hex(color));
            return hex;
        }
        var accent = NSColor.ControlAccent;
        return ":root{" + $"color-scheme:{(dark ? "dark" : "light")};--bt-bg:{Resolved(NSColor.TextBackground)};--bt-side:{Resolved(NSColor.UnderPageBackground)};"
            + $"--bt-text:{Resolved(NSColor.Text)};--bt-muted:{Resolved(NSColor.SecondaryLabel)};--bt-line:{Resolved(NSColor.Separator)};"
            + $"--bt-accent:{Resolved(accent)};--bt-accent-text:#ffffff;--bt-hit:{Resolved(NSColor.FindHighlight)}" + "}";
    }

    (byte[] Bytes, string Type, string Csp)? Serve(NSUrl url, string query)
    {
        var path = url.Path ?? "";
        if (path == PagePath)
            return _page.Length == 0 || query != $"v={_version.ToString(CultureInfo.InvariantCulture)}"
                ? null
                : (System.Text.Encoding.UTF8.GetBytes(_page), "text/html; charset=utf-8", _pageCsp);
        if (EbookReaderDocument.ResourcePath(path) is not { } file || _content?.Read(file) is not { } found) return null;
        var html = found.MediaType == "text/html";
        return (found.Bytes, html ? "text/html; charset=utf-8" : found.MediaType, html ? FrameCsp : FileCsp);
    }

    sealed class SchemeHandler(EbookSurface owner) : NSObject, IWKUrlSchemeHandler
    {
        [Export("webView:startURLSchemeTask:")]
        public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
        {
            var url = urlSchemeTask.Request.Url;
            var served = url.Host == Host ? owner.Serve(url, url.Query ?? "") : null;
            // Nothing is kept in a cache: two books can have a file at the same path.
            var headers = new NSMutableDictionary
            {
                ["Cache-Control"] = new NSString("no-store"),
                ["X-Content-Type-Options"] = new NSString("nosniff"),
            };
            if (served is { } file)
            {
                headers["Content-Type"] = new NSString(file.Type);
                headers["Content-Security-Policy"] = new NSString(file.Csp);
            }
            using var response = new NSHttpUrlResponse(url, served is null ? 404 : 200, "HTTP/1.1", headers);
            urlSchemeTask.DidReceiveResponse(response);
            if (served is { } body) urlSchemeTask.DidReceiveData(NSData.FromArray(body.Bytes));
            urlSchemeTask.DidFinish();
        }

        [Export("webView:stopURLSchemeTask:")]
        public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
        {
        }
    }

    sealed class Messages(EbookSurface owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            // Only the book's page talks; its frames have no script.
            if (message.FrameInfo.MainFrame) owner.OnMessage(message.Body);
        }
    }

    /// <summary>The page and the book's own files load; every other address is refused, links included.</summary>
    sealed class Navigation : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url;
            var ours = url.Scheme == Scheme && url.Host == Host || url.AbsoluteString == "about:blank";
            decisionHandler(ours && navigationAction.NavigationType != WKNavigationType.LinkActivated ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }
    }
}

/// <summary>The few shapes the page's script is handed or answers with, written out at build time.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
sealed partial class EbookJson : JsonSerializerContext;

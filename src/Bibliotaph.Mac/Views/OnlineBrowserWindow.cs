using AppKit;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using WebKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Bibliotaph's browser for one service, as on Windows: it opens the service's library page once and never navigates,
/// clicks or scrolls by itself. As each page shows, the service's reader looks at it, and Read this page lights up
/// when it lists the user's books; pressing it hands those books to Settings › Online libraries. Sign-ins are kept
/// in the app's own website data, apart from Safari's; a store's Download buttons save into the downloads folder.
/// </summary>
sealed class OnlineBrowserWindow : NSWindowController
{
    // Covers are fetched again only from the service's own sites, and only this big.
    const int MaxCover = 8 * 1024 * 1024;
    static readonly HttpClient Covers = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly IOnlineLibraryReader _reader;
    readonly OnlineBrowserRequest _request;
    readonly MacOnlineDownloads _downloads;
    readonly ILogger _log;
    readonly WKWebView _web;
    readonly NSTextField _status;
    readonly NSTextField _address;
    readonly NSButton _read;
    readonly NSButton _back;
    readonly NSButton _forward;
    readonly NSTextField _downloaded;
    readonly Bindings _bindings = new();
    readonly List<NSWindow> _popups = [];
    int _checks;
    OnlinePage? _page;

    public OnlineBrowserWindow(OnlineBrowserRequest request, MacOnlineDownloads downloads, ILogger log)
        : base(new NSWindow(new CGRect(0, 0, 1100, 800), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false))
    {
        (_request, _reader, _downloads, _log) = (request, request.Reader, downloads, log);
        var window = Window!;
        window.Title = $"{_reader.Name} · Bibliotaph";
        window.ReleaseWhenClosed(false);

        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.DefaultDataStore };
        // A library that changes without a new page (a filter chosen, more books drawn) is looked at again too.
        configuration.UserContentController.AddScriptMessageHandler(new Changes(this), "changed");
        configuration.UserContentController.AddUserScript(new WKUserScript(new NSString(WatchChanges), WKUserScriptInjectionTime.AtDocumentEnd, false));
        _web = new WKWebView(CGRect.Empty, configuration) { NavigationDelegate = new Navigation(this), UIDelegate = new Popups(this) };

        _back = NSButton.CreateButton(NSImage.GetSystemSymbol("chevron.left", "Back")!, () => _web.GoBack());
        _forward = NSButton.CreateButton(NSImage.GetSystemSymbol("chevron.right", "Forward")!, () => _web.GoForward());
        var reload = NSButton.CreateButton(NSImage.GetSystemSymbol("arrow.clockwise", "Reload")!, () => _web.Reload());
        _address = NSTextField.CreateLabel("");
        _address.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        _address.TextColor = NSColor.SecondaryLabel;
        _address.SetContentCompressionResistancePriority(100, NSLayoutConstraintOrientation.Horizontal);
        _read = NSButton.CreateButton("Read this page", () => ReadAsync().FireAndForget());
        _read.BezelColor = NSColor.ControlAccent;
        _read.Enabled = false;
        var bar = NSStackView.FromViews([_back, _forward, reload, _address, _read]);
        bar.Spacing = 8;
        bar.EdgeInsets = new NSEdgeInsets(8, 12, 8, 12);
        _status = NSTextField.CreateWrappingLabel("");
        _status.Font = NSFont.SystemFontOfSize(12)!;
        _downloaded = NSTextField.CreateWrappingLabel("");
        _downloaded.Font = NSFont.SystemFontOfSize(11)!;
        _downloaded.TextColor = NSColor.SecondaryLabel;
        _downloaded.Hidden = true;

        var root = new NSView();
        foreach (var view in new NSView[] { bar, _status, _web, _downloaded })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            bar.TopAnchor.ConstraintEqualTo(root.TopAnchor),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _status.TopAnchor.ConstraintEqualTo(bar.BottomAnchor),
            _status.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 14),
            _status.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -14),
            _web.TopAnchor.ConstraintEqualTo(_status.BottomAnchor, 8),
            _web.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _downloaded.TopAnchor.ConstraintEqualTo(_web.BottomAnchor, 6),
            _downloaded.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 14),
            _downloaded.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -14),
            _downloaded.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -8),
        ]);
        window.ContentView = root;
        window.WillClose += (_, _) =>
        {
            _checks++;
            _bindings.Dispose();
            foreach (var popup in _popups.ToList()) popup.Close();
            _web.StopLoading();
        };
        _bindings.Collection(_downloads.Items, _ => ShowDownloads());
        StartAsync().FireAndForget();
    }

    /// <summary>Forgets every sign-in, cookie and page the browser kept.</summary>
    public static Task SignOutAsync()
    {
        var done = new TaskCompletionSource();
        var store = WKWebsiteDataStore.DefaultDataStore;
        store.RemoveDataOfTypes(WKWebsiteDataStore.AllWebsiteDataTypes, NSDate.DistantPast, () => done.TrySetResult());
        return done.Task;
    }

    public void Show()
    {
        Window!.Center();
        ShowWindow(null);
        Window.MakeKeyAndOrderFront(null);
    }

    async Task StartAsync()
    {
        _status.StringValue = $"Sign in on {_reader.Name}'s own page if it asks, and open your library. Read this page lights up when the page lists your books.";
        if (_reader.LinkField != MetadataFields.AlsoOwn)
            _status.StringValue += await _downloads.FolderAsync() is null
                ? " Its Download buttons save to your Downloads folder; choose a downloads folder in Settings › Online libraries to have Bibliotaph read what you download."
                : " Its Download buttons save into your downloads folder, and Bibliotaph reads each file.";
        if (_request.Start is { IsFile: true } saved)
            _web.LoadFileUrl(NSUrl.FromFilename(saved.LocalPath), NSUrl.FromFilename(Path.GetDirectoryName(saved.LocalPath)!));
        else _web.LoadRequest(new NSUrlRequest(new NSUrl((_request.Start ?? _reader.LibraryPage).AbsoluteUri)));
    }

    void Moved()
    {
        _back.Enabled = _web.CanGoBack;
        _forward.Enabled = _web.CanGoForward;
        _address.StringValue = _web.Url?.AbsoluteString ?? "";
    }

    /// <summary>Looks at the page once it has settled: a page still drawing is looked at again when it is done.</summary>
    async Task CheckSoonAsync()
    {
        var mine = ++_checks;
        await Task.Delay(400);
        if (mine != _checks) return;
        try
        {
            var html = (await _web.EvaluateJavaScriptAsync(Snapshot)) is NSString text ? text.ToString() : "";
            var (page, hint) = await Task.Run(() => _reader.Read(html) is { } read ? (read, null) : ((OnlinePage?)null, _reader.Hint(html)));
            if (mine != _checks) return;
            _page = page;
            _read.Enabled = page is not null;
            _status.StringValue = page switch
            {
                null => hint ?? $"Open your library on {_reader.Name}, signed in. Read this page lights up when the page lists your books; if it doesn't, reload the page.",
                { Books.Count: 1 } => "This page lists 1 book you own.",
                _ => $"This page lists {page.Books.Count} books you own.",
            };
        }
        catch (NSErrorException ex)
        {
            // The page went away while it was being read; the next one is looked at in turn.
            _log.LogDebug(ex, "The browser couldn't look at the page");
        }
    }

    async Task ReadAsync()
    {
        if (_page is not { } page) return;
        _read.Enabled = false;
        try
        {
            _status.StringValue = await _request.Read(page, CoverAsync);
        }
        finally
        {
            _read.Enabled = _page is not null;
        }
    }

    /// <summary>A cover the page showed, fetched again from the service's own sites; nothing else is ever fetched.</summary>
    async Task<byte[]?> CoverAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!_reader.CoverSites.Any(site => uri.Host.Equals(site, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            using var response = await Covers.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaxCover) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is > 0 and <= MaxCover ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogInformation("A cover from {Host} couldn't be fetched: {Message}", uri.Host, ex.Message);
            return null;
        }
    }

    void ShowDownloads()
    {
        var latest = _downloads.Items.Take(3).ToList();
        _downloaded.Hidden = latest.Count == 0;
        _downloaded.StringValue = string.Join("\n", latest.Select(d => $"{d.Name}: {d.Status}"));
    }

    /// <summary>
    /// The page's markup as it shows now. What the user chose or typed in a list or a search box lives in the page, not
    /// its markup, so it is written into the markup first. Passwords, and what is typed in a form that asks for one,
    /// never are.
    /// </summary>
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
            setTimeout(() => { waiting = false; window.webkit?.messageHandlers?.changed?.postMessage('changed'); }, 1500);
          };
          document.addEventListener('change', changed, true);
          new MutationObserver(changed).observe(document, { childList: true, subtree: true });
        })();
        """;

    sealed class Changes(OnlineBrowserWindow owner) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) =>
            owner.CheckSoonAsync().FireAndForget();
    }

    sealed class Navigation(OnlineBrowserWindow owner) : WKNavigationDelegate
    {
        public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation)
        {
            owner._checks++;
            owner._page = null;
            owner._read.Enabled = false;
            owner.Moved();
        }

        public override void DidCommitNavigation(WKWebView webView, WKNavigation navigation) => owner.Moved();

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            owner.Moved();
            owner.CheckSoonAsync().FireAndForget();
        }

        /// <summary>What the browser can't show is a download, as a store's Download button gives.</summary>
        public override void DecidePolicy(WKWebView webView, WKNavigationResponse navigationResponse, Action<WKNavigationResponsePolicy> decisionHandler) =>
            decisionHandler(navigationResponse.CanShowMimeType ? WKNavigationResponsePolicy.Allow : WKNavigationResponsePolicy.Download);

        public override void NavigationResponseDidBecomeDownload(WKWebView webView, WKNavigationResponse navigationResponse, WKDownload download) =>
            download.Delegate = new Saving(owner);

        public override void NavigationActionDidBecomeDownload(WKWebView webView, WKNavigationAction navigationAction, WKDownload download) =>
            download.Delegate = new Saving(owner);
    }

    /// <summary>A download, saved where the downloads folder says, and read once it is there.</summary>
    sealed class Saving(OnlineBrowserWindow owner) : WKDownloadDelegate
    {
        MacDownload? _item;

        public override async void DecideDestination(WKDownload download, NSUrlResponse response, string suggestedFilename, Action<NSUrl> completionHandler)
        {
            try
            {
                _item = await owner._downloads.StartAsync(suggestedFilename, owner._reader.Name);
                completionHandler(NSUrl.FromFilename(_item.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner._log.LogWarning(ex, "A download from {Service} couldn't be placed", owner._reader.Name);
                completionHandler(null!);
            }
        }

        public override void DidFinish(WKDownload download)
        {
            if (_item is null) return;
            _item.Done();
            owner._downloads.Finished(_item);
            owner.ShowDownloads();
        }

        public override void DidFail(WKDownload download, NSError error, NSData? resumeData)
        {
            _item?.Failed(error.LocalizedDescription);
            owner.ShowDownloads();
        }
    }

    /// <summary>A sign-in that opens a window of its own (Sign in with Google, a store's own) opens in one here.</summary>
    sealed class Popups(OnlineBrowserWindow owner) : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            var popup = new WKWebView(CGRect.Empty, configuration) { UIDelegate = this };
            var window = new NSWindow(new CGRect(0, 0, 520, 680), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
            {
                Title = owner._reader.Name,
                ContentView = popup,
            };
            window.ReleaseWhenClosed(false);
            owner._popups.Add(window);
            window.WillClose += (_, _) => owner._popups.Remove(window);
            window.Center();
            window.MakeKeyAndOrderFront(null);
            return popup;
        }

        public override void DidClose(WKWebView webView)
        {
            if (owner._popups.FirstOrDefault(w => w.ContentView == webView) is { } window) window.Close();
        }
    }
}

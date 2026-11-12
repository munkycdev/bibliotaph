using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Processing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bibliotaph.App.Views;

/// <summary>
/// A video in the reader, such as an animated map: a page in the e-book reader's browser with the video on it, playing
/// on a loop without sound and fitted to the window. WPF's own player can't read WebM; the browser can. The page and the
/// video's bytes are answered here, the video a part at a time as the player asks, and every other address is refused,
/// so nothing reaches the network.
/// </summary>
public partial class VideoView
{
    /// <summary>The made-up address the page is at; <c>.invalid</c> names no site anywhere.</summary>
    const string Host = "video.bibliotaph.invalid";
    const string PagePath = "/video.html";
    const string VideoPath = "/video";

    public static readonly DependencyProperty VideoProperty = DependencyProperty.Register(nameof(Video), typeof(OpenVideo), typeof(VideoView),
        new PropertyMetadata(null, (d, e) => ((VideoView)d).Show(e.NewValue as OpenVideo)));

    Task<CoreWebView2?>? _starting;
    OpenVideo? _video;
    string _page = "";
    string _pageCsp = "";
    int _version;
    bool _gone;

    public VideoView()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            // The browser is let go as soon as the video isn't shown, and a new one is made if it shows again.
            _gone = true;
            _version++;
            Web.Dispose();
        };
        // A video set before the view was in the window, or shown again after it left, starts playing now.
        Loaded += (_, _) =>
        {
            if (_video is { } video) Show(video);
        };
    }

    /// <summary>The video to play, or null for none.</summary>
    public OpenVideo? Video
    {
        get => (OpenVideo?)GetValue(VideoProperty);
        set => SetValue(VideoProperty, value);
    }

    /// <summary>Set by the reader view: the browser the e-book reader uses, which plays the video too.</summary>
    public EbookBrowser? Browser { get; set; }

    /// <summary>The browser couldn't start, or couldn't play the video, and says why.</summary>
    public event EventHandler<string>? Failed;

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

    async void Show(OpenVideo? video)
    {
        var version = ++_version;
        _video = video;
        Message.Visibility = Visibility.Collapsed;
        if (video is null)
        {
            _page = "";
            if (!_gone && Web.CoreWebView2 is { } shown)
            {
                try
                {
                    shown.Navigate("about:blank");
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // The browser went meanwhile.
                }
            }
            return;
        }
        if (!IsLoaded) return;
        Revive();
        var core = await StartAsync();
        if (core is null || version != _version || _gone) return;
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        _page = Page(video.Title, nonce, version);
        _pageCsp = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; media-src https://{Host}; base-uri 'none'; form-action 'none'";
        try
        {
            core.Navigate($"https://{Host}{PagePath}?v={version.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Fail($"The video couldn't be shown: {ex.Message}");
        }
    }

    /// <summary>The page: the video, fitted to the window on the reader's background, and a line that says when it won't play.</summary>
    string Page(string title, string nonce, int version)
    {
        var background = TryFindResource("Bt.SideColor") is Color side ? $"#{side.R:x2}{side.G:x2}{side.B:x2}" : "#202020";
        var source = $"{VideoPath}?v={version.ToString(CultureInfo.InvariantCulture)}";
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
            <style nonce="{{nonce}}">html,body{margin:0;height:100%;background:{{background}};overflow:hidden}video{display:block;width:100%;height:100%;object-fit:contain}</style>
            </head><body>
            <video src="{{source}}" aria-label="{{WebUtility.HtmlEncode(title)}}" autoplay loop muted playsinline controls disablepictureinpicture></video>
            <script nonce="{{nonce}}">
            const v = document.querySelector('video');
            v.addEventListener('error', () => chrome.webview.postMessage({ t: 'error', code: v.error ? v.error.code : 0 }));
            </script>
            </body></html>
            """;
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
            Fail("Playing a video needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes.");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Fail($"The video player couldn't start: {ex.Message}");
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

        // Every request is answered here: the page and the video, or a refusal.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) => e.Cancel = !IsPage(e.Uri) && e.Uri != "about:blank";
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        // The player's own controls are enough; the browser's Save video as and Copy video address aren't for a library.
        core.ContextMenuRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _starting = null;
                Fail("The video player stopped. Open the video again to play it.");
            }
            else if (_video is { } video) Show(video);
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
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == Host && uri.AbsolutePath == PagePath;

    async void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != Host)
        {
            e.Response = Refuse(core);
            return;
        }
        if (uri.AbsolutePath == PagePath)
        {
            e.Response = _page.Length == 0
                ? Refuse(core)
                : core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(_page)), 200, "OK",
                    $"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {_pageCsp}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            return;
        }
        if (uri.AbsolutePath != VideoPath || _video is not { } video)
        {
            e.Response = Refuse(core);
            return;
        }
        var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
        var deferral = e.GetDeferral();
        try
        {
            e.Response = await Task.Run(() => Answer(core, video, range));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The video or the browser closed meanwhile.
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// The video's bytes for one request: the part asked for (206), or all of it (200), always saying parts can be
    /// asked for, as the player needs to seek and loop. The file is opened for each request and closed as the browser
    /// finishes reading it.
    /// </summary>
    static CoreWebView2WebResourceResponse Answer(CoreWebView2 core, OpenVideo video, string? rangeHeader)
    {
        var file = video.Open();
        try
        {
            var total = file.Length;
            const string common = "Content-Type: video/webm\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
            if (MediaRange.Parse(rangeHeader, total) is not { } range)
                return core.Environment.CreateWebResourceResponse(file, 200, "OK",
                    $"{common}\r\nContent-Length: {total.ToString(CultureInfo.InvariantCulture)}");
            if (range.Length == 0)
            {
                file.Dispose();
                return core.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                    $"Content-Range: bytes */{total.ToString(CultureInfo.InvariantCulture)}\r\nCache-Control: no-store");
            }
            return core.Environment.CreateWebResourceResponse(range.Open(file), 206, "Partial Content",
                $"{common}\r\nContent-Range: {range.ContentRange}\r\nContent-Length: {range.Length.ToString(CultureInfo.InvariantCulture)}");
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    static CoreWebView2WebResourceResponse Refuse(CoreWebView2 core) =>
        core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsPage(e.Source) || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || source.Query != $"?v={_version.ToString(CultureInfo.InvariantCulture)}") return;
        try
        {
            var message = JsonElement.Parse(e.WebMessageAsJson);
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("t", out var kind) && kind.GetString() == "error")
                Failed?.Invoke(this, "The file may be damaged, or not a video this PC can play.");
        }
        catch (JsonException)
        {
            // Not a message the page sends.
        }
    }
}

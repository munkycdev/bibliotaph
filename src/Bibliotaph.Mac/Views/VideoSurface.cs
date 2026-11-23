using System.Globalization;
using System.Text;
using AppKit;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A video in the library (an animated map, most often a WebM) played by WebKit, which plays WebM where AVFoundation
/// doesn't. The file is served from its stream under a scheme of its own, a range at a time as the player asks.
/// </summary>
public sealed class VideoSurface : NSView
{
    const string Scheme = "btvideo";
    readonly WKWebView _web;
    OpenVideo? _video;
    int _version;

    public VideoSurface()
    {
        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
        configuration.SetUrlSchemeHandler(new Files(this), Scheme);
        configuration.MediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypes.None;
        _web = new WKWebView(CGRect.Empty, configuration) { TranslatesAutoresizingMaskIntoConstraints = false };
        _web.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground"));
        AddSubview(_web);
        NSLayoutConstraint.ActivateConstraints(
        [
            _web.TopAnchor.ConstraintEqualTo(TopAnchor),
            _web.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _web.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
        ]);
    }

    public VideoSurface(NativeHandle handle) : base(handle) => _web = null!;

    public void Show(OpenVideo? video)
    {
        _video = video;
        _version++;
        if (video is null)
        {
            _web.LoadHtmlString("", null!);
            return;
        }
        _web.LoadRequest(new NSUrlRequest(new NSUrl($"{Scheme}://video/index.html?v={_version.ToString(CultureInfo.InvariantCulture)}")));
    }

    const string Page = """
        <!doctype html><meta charset="utf-8"><title>Video</title>
        <style>html,body{margin:0;height:100%;background:transparent}video{width:100%;height:100%;object-fit:contain}</style>
        <video src="/file" controls autoplay loop playsinline></video>
        """;

    (byte[] Bytes, string Type, int Status, string? Range, long Length)? Serve(NSUrl url, string? range)
    {
        if (url.Path == "/index.html") return (Encoding.UTF8.GetBytes(Page), "text/html; charset=utf-8", 200, null, 0);
        if (url.Path != "/file" || _video is not { } video) return null;
        using var stream = video.Open();
        var length = stream.Length;
        var (from, to) = (0L, length - 1);
        // The player asks for a range at a time: "bytes=from-to" or "bytes=from-".
        if (range is { } asked && asked.StartsWith("bytes=", StringComparison.Ordinal) && asked[6..].Split('-') is [var first, var last])
        {
            if (long.TryParse(first, CultureInfo.InvariantCulture, out var start)) from = Math.Clamp(start, 0, length - 1);
            if (long.TryParse(last, CultureInfo.InvariantCulture, out var end)) to = Math.Clamp(end, from, length - 1);
        }
        // A range is sent at most 8 MB at a time, which the player follows with the next; a request with none gets the whole file.
        if (range is not null) to = Math.Min(to, from + 8 * 1024 * 1024 - 1);
        var bytes = new byte[to - from + 1];
        stream.Position = from;
        stream.ReadExactly(bytes);
        return (bytes, "video/webm", range is null ? 200 : 206, range is null ? null : $"bytes {from}-{to}/{length}", length);
    }

    sealed class Files(VideoSurface owner) : NSObject, IWKUrlSchemeHandler
    {
        [Export("webView:startURLSchemeTask:")]
        public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
        {
            var request = urlSchemeTask.Request;
            var range = request.Headers?["Range"]?.ToString();
            (byte[] Bytes, string Type, int Status, string? Range, long Length)? served;
            try
            {
                served = owner.Serve(request.Url, range);
            }
            catch (IOException)
            {
                served = null;
            }
            var headers = new NSMutableDictionary { ["Cache-Control"] = new NSString("no-store") };
            if (served is { } file)
            {
                headers["Content-Type"] = new NSString(file.Type);
                headers["Content-Length"] = new NSString(file.Bytes.Length.ToString(CultureInfo.InvariantCulture));
                if (file.Range is { } contentRange)
                {
                    headers["Accept-Ranges"] = new NSString("bytes");
                    headers["Content-Range"] = new NSString(contentRange);
                }
            }
            using var response = new NSHttpUrlResponse(request.Url, served?.Status ?? 404, "HTTP/1.1", headers);
            urlSchemeTask.DidReceiveResponse(response);
            if (served is { } body) urlSchemeTask.DidReceiveData(NSData.FromArray(body.Bytes));
            urlSchemeTask.DidFinish();
        }

        [Export("webView:stopURLSchemeTask:")]
        public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
        {
        }
    }
}

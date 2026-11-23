using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebKit;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A book owned online, read in Bibliotaph's own browser as on Windows: the store's page with Back, Forward and Reload,
/// the heart, pin and the book's note beside it, Reading mode where the service has one, and Add this page to a binder.
/// It shares the import browser's sign-ins. A link off the book's site opens in the everyday browser; a download is
/// saved where the import browser saves them.
/// </summary>
public sealed class OnlineReaderController : NSViewController, IWindowTitle
{
    /// <summary>The book's title, with where it's read, "On D&amp;D Beyond", under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Property(_reader, nameof(OnlineReaderViewModel.PageTitle), () => show(_reader.Title, _reader.PlaceLine));

    readonly OnlineReaderViewModel _reader;
    readonly MacOnlineDownloads _downloads;
    readonly ILogger _log;
    readonly Bindings _bindings = new();
    WKWebView _web = null!;

    OnlineReaderController(OnlineReaderViewModel reader, MacOnlineDownloads downloads, ILogger log)
    {
        _reader = reader;
        _downloads = downloads;
        _log = log;
    }

    public static OnlineReaderController For(OnlineReaderViewModel model, IServiceProvider services) =>
        new(model, services.GetRequiredService<MacOnlineDownloads>(), services.GetRequiredService<ILogger<OnlineReaderController>>());

    NSView _notePane = null!;
    NSTextView _note = null!;
    bool _gone;

    public override void LoadView()
    {
        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.DefaultDataStore };
        _web = new WKWebView(CGRect.Empty, configuration)
        {
            NavigationDelegate = new Navigation(this),
            UIDelegate = new Windows(this),
            AllowsBackForwardNavigationGestures = true,
        };

        var back = Symbol("chevron.left", "Back a page on the site", () => _web.GoBack());
        var forward = Symbol("chevron.right", "Forward a page on the site", () => _web.GoForward());
        var reload = Symbol("arrow.clockwise", "Reload the page", () => _web.Reload());
        var title = NSTextField.CreateLabel(_reader.Title);
        title.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var place = NSTextField.CreateLabel("");
        place.Font = NSFont.SystemFontOfSize(11)!;
        place.TextColor = NSColor.SecondaryLabel;
        place.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var titles = NSStackView.FromViews([title, place]);
        titles.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        titles.Alignment = NSLayoutAttribute.Leading;
        titles.Spacing = 0;
        titles.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        titles.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        var heart = Symbol("heart", "", () => _reader.ToggleFavoriteCommand.Execute(null));
        var pin = Symbol("pin", "", () => _reader.TogglePinCommand.Execute(null));
        var note = Symbol("note.text", "The book's own note, beside the page", () => _reader.ToggleNoteCommand.Execute(null));
        var reading = Symbol("doc.plaintext", "", () => _reader.ToggleReadingModeCommand.Execute(null));
        reading.Hidden = !_reader.HasReadingMode;
        var addPage = NSButton.CreateButton("Add This Page", () => _reader.AddPageCommand.Execute(null));
        addPage.TintProminence = NSTintProminence.Primary;
        addPage.ToolTip = "Add the page in view to the current binder";
        var toBinder = Symbol("chevron.down", "Add to another binder", () => { });
        toBinder.Activated += (_, _) =>
        {
            var menu = new NSMenu();
            foreach (var choice in ((ISessionChoices)_reader).MenuChoices)
                menu.AddItem(new NSMenuItem(choice.Label, (_, _) => _reader.AddPageToSessionCommand.Execute(new SessionRequest(_reader, choice))));
            menu.PopUpMenu(null, new CGPoint(0, toBinder.Bounds.Height + 4), toBinder);
        };
        var inBrowser = NSButton.CreateButton("Open in Your Browser", () => _reader.OpenInBrowserCommand.Execute(null));
        inBrowser.Bordered = false;
        inBrowser.ContentTintColor = NSColor.ControlAccent;
        inBrowser.ToolTip = "Open this page in your everyday browser";
        var popOut = Symbol("macwindow.on.rectangle", "Pop out into a window of its own", () => _reader.PopOutCommand.Execute(null));
        popOut.Hidden = !_reader.IsInMainWindow;
        var returnTo = Symbol("arrow.down.right.and.arrow.up.left", "Return to the main window", () => _reader.ReturnToMainWindowCommand.Execute(null));
        returnTo.Hidden = !_reader.IsPoppedOut;

        // The book's title and where it's read are in the window's title; the bar keeps a space where they were.
        titles.Hidden = true;
        var spacer = new NSView();
        spacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var bar = NSStackView.FromViews([back, forward, reload, spacer, titles, heart, pin, note, reading, addPage, toBinder, inBrowser, popOut, returnTo]);
        bar.Spacing = 6;
        bar.SetCustomSpacing(12, reload);
        bar.SetCustomSpacing(14, titles);
        bar.SetCustomSpacing(14, reading);
        bar.EdgeInsets = new NSEdgeInsets(8, 12, 8, 12);
        bar.Distribution = NSStackViewDistribution.Fill;

        var notice = NSTextField.CreateWrappingLabel("");
        notice.Font = NSFont.SystemFontOfSize(12)!;
        notice.TextColor = NSColor.SystemOrange;
        var undoMessage = NSTextField.CreateWrappingLabel("");
        undoMessage.Font = NSFont.SystemFontOfSize(12)!;
        var undo = NSButton.CreateButton("Undo", () => { });
        undo.ControlSize = NSControlSize.Small;
        _bindings.Command(undo, _reader.Sessions.Undo.UndoCommand);
        var dismiss = NSButton.CreateButton(NSImage.GetSystemSymbol("xmark", "Hide")!, () => _reader.DismissSessionMessageCommand.Execute(null));
        dismiss.Bordered = false;
        var added = NSStackView.FromViews([undoMessage, undo, dismiss]);
        added.Spacing = 10;
        var lines = NSStackView.FromViews([notice, added]);
        lines.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        lines.Alignment = NSLayoutAttribute.Leading;
        lines.EdgeInsets = new NSEdgeInsets(6, 14, 6, 14);
        lines.Spacing = 4;

        _notePane = NotePane();
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        // The page and the note side by side under the bar, and the notice over them only while there's one.
        var page = new NSStackView { Spacing = 0, Alignment = NSLayoutAttribute.Height, Distribution = NSStackViewDistribution.Fill };
        page.AddArrangedSubview(_web);
        page.AddArrangedSubview(_notePane);
        _web.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var column = NSStackView.FromViews([bar, line, lines, page]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Width;
        column.Spacing = 0;
        page.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        var root = new NSView();
        column.TranslatesAutoresizingMaskIntoConstraints = false;
        root.AddSubview(column);
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
        ]);
        var noteWidth = _notePane.WidthAnchor.ConstraintEqualTo(0);
        noteWidth.Active = true;
        View = root;

        _bindings.Properties(_reader, [nameof(OnlineReaderViewModel.PageTitle), nameof(OnlineReaderViewModel.CanGoBack), nameof(OnlineReaderViewModel.CanGoForward),
            nameof(OnlineReaderViewModel.IsFavorite), nameof(OnlineReaderViewModel.IsPinned), nameof(OnlineReaderViewModel.ShowNote),
            nameof(OnlineReaderViewModel.ReadingMode), nameof(OnlineReaderViewModel.Notice), nameof(OnlineReaderViewModel.Address)], () =>
        {
            place.StringValue = _reader.PlaceLine;
            place.Hidden = _reader.PlaceLine.Length == 0;
            // In a binder, its header already names the page.
            titles.Hidden = _reader.IsInBinder;
            back.Enabled = _reader.CanGoBack;
            forward.Enabled = _reader.CanGoForward;
            heart.Image = NSImage.GetSystemSymbol(_reader.IsFavorite ? "heart.fill" : "heart", _reader.FavoriteAction)!;
            heart.ToolTip = _reader.FavoriteAction;
            pin.Image = NSImage.GetSystemSymbol(_reader.IsPinned ? "pin.fill" : "pin", _reader.PinAction)!;
            pin.ToolTip = _reader.PinAction;
            note.State = _reader.ShowNote ? NSCellStateValue.On : NSCellStateValue.Off;
            note.ContentTintColor = _reader.ShowNote ? NSColor.ControlAccent : null!;
            noteWidth.Constant = _reader.ShowNote ? 280 : 0;
            _notePane.Hidden = !_reader.ShowNote;
            reading.ContentTintColor = _reader.ReadingMode ? NSColor.ControlAccent : null!;
            reading.ToolTip = _reader.ReadingModeAction;
            notice.StringValue = _reader.Notice ?? "";
            notice.Hidden = _reader.Notice is null;
            lines.Hidden = notice.Hidden && added.Hidden;
        });
        _bindings.Properties(_reader.Sessions.Undo, [nameof(UndoSlot.Message), nameof(UndoSlot.HasUndo)], () =>
        {
            undoMessage.StringValue = _reader.Sessions.Undo.Message ?? "";
            added.Hidden = _reader.Sessions.Undo.Message is null;
            lines.Hidden = notice.Hidden && added.Hidden;
            undo.Hidden = !_reader.Sessions.Undo.HasUndo;
        });
        _bindings.Property(_reader, nameof(OnlineReaderViewModel.NoteText), () =>
        {
            if (_note.Window?.FirstResponder != _note) _note.Value = _reader.NoteText;
        });
        _bindings.Property(_reader, nameof(OnlineReaderViewModel.ReadingMode), () => ShowReadingMode());
        // The binder dialog opens as a sheet over the _reader.
        DialogSheet.Follow(_bindings, _reader.Sessions, () => View.Window);

        if (_reader.Start.IsFile) _web.LoadFileUrl(new NSUrl(_reader.Start.AbsoluteUri), new NSUrl(_reader.Start.AbsoluteUri));
        else _web.LoadRequest(new NSUrlRequest(new NSUrl(_reader.Start.AbsoluteUri)));
    }

    public override void ViewWillDisappear()
    {
        base.ViewWillDisappear();
        _reader.NoteText = _note.Value;
        _reader.SaveNoteCommand.Execute(null);
    }

    /// <summary>The book's own note, beside the page, kept when it loses focus or the _reader closes.</summary>
    NSStackView NotePane()
    {
        var heading = NSTextField.CreateLabel("Note");
        heading.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        _note = new NSTextView(new CGRect(0, 0, 250, 300))
        {
            RichText = false,
            AllowsUndo = true,
            Font = NSFont.SystemFontOfSize(13)!,
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            MaxSize = new CGSize(nfloat.MaxValue, nfloat.MaxValue),
            TextContainerInset = new CGSize(4, 6),
            Value = _reader.NoteText,
        };
        _note.TextContainer!.WidthTracksTextView = true;
        _note.TextDidChange += (_, _) => _reader.NoteText = _note.Value;
        _note.TextDidEndEditing += (_, _) => _reader.SaveNoteCommand.Execute(null);
        var scroll = new NSScrollView { DocumentView = _note, HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder };
        var pane = NSStackView.FromViews([heading, scroll]);
        pane.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        pane.Alignment = NSLayoutAttribute.Leading;
        pane.EdgeInsets = new NSEdgeInsets(12, 12, 12, 12);
        scroll.WidthAnchor.ConstraintEqualTo(pane.WidthAnchor, 1, -24).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        return pane;
    }

    /// <summary>
    /// Puts Reading mode's stylesheet on every page of the service's own site from now on, and on the page in view, or
    /// takes it off both, as on Windows.
    /// </summary>
    void ShowReadingMode()
    {
        var scripts = _web.Configuration.UserContentController;
        scripts.RemoveAllUserScripts();
        if (_reader.ReadingMode && _reader.ReadingStyle is { } style && OnlineReaders.SiteOf(_reader.Request.Link) is { } site)
        {
            var script = ReadingScript(style, site);
            scripts.AddUserScript(new WKUserScript(new NSString(script), WKUserScriptInjectionTime.AtDocumentStart, true));
            if (!_gone) _web.EvaluateJavaScript(script, null!);
        }
        else if (!_gone) _web.EvaluateJavaScript("document.getElementById('bibliotaph-reading')?.remove()", null!);
    }

    static string ReadingScript(string style, string site) => $$"""
        (() => {
          const site = {{Js(site)}};
          const host = location.hostname.replace(/^www\./, '');
          if (host !== site && !host.endsWith('.' + site)) return;
          const add = () => {
            if (document.getElementById('bibliotaph-reading')) return;
            const style = document.createElement('style');
            style.id = 'bibliotaph-reading';
            style.textContent = {{Js(style)}};
            (document.head ?? document.documentElement).appendChild(style);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add);
          else add();
        })();
        """;

    // A JavaScript string of the text, quotes included.
    static string Js(string text) => $"\"{System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text)}\"";

    void Moved()
    {
        if (_web.Url?.AbsoluteString is { } address) _reader.Address = address;
        _reader.PageTitle = _web.Title;
        _reader.CanGoBack = _web.CanGoBack;
        _reader.CanGoForward = _web.CanGoForward;
    }

    static NSButton Symbol(string symbol, string tip, Action action)
    {
        var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, action);
        button.BezelStyle = NSBezelStyle.Toolbar;
        button.ToolTip = tip;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gone = true;
            _bindings.Dispose();
            _web?.StopLoading();
        }
        base.Dispose(disposing);
    }

    /// <summary>A link off the book's site leaves for the everyday browser; what the browser can't show is a download.</summary>
    sealed class Navigation(OnlineReaderController owner) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var address = navigationAction.Request.Url?.AbsoluteString;
            if (navigationAction.NavigationType == WKNavigationType.LinkActivated && address is not null && !owner._reader.Follows(address))
            {
                owner._reader.OpenOutside(address);
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }
            decisionHandler(WKNavigationActionPolicy.Allow);
        }

        public override void DecidePolicy(WKWebView webView, WKNavigationResponse navigationResponse, Action<WKNavigationResponsePolicy> decisionHandler) =>
            decisionHandler(navigationResponse.CanShowMimeType ? WKNavigationResponsePolicy.Allow : WKNavigationResponsePolicy.Download);

        public override void DidCommitNavigation(WKWebView webView, WKNavigation navigation) => owner.Moved();

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => owner.Moved();

        public override void NavigationResponseDidBecomeDownload(WKWebView webView, WKNavigationResponse navigationResponse, WKDownload download) =>
            download.Delegate = new Saving(owner);

        public override void NavigationActionDidBecomeDownload(WKWebView webView, WKNavigationAction navigationAction, WKDownload download) =>
            download.Delegate = new Saving(owner);
    }

    /// <summary>A download, saved where the import browser saves them, and said in the reader's notice as it goes.</summary>
    sealed class Saving(OnlineReaderController owner) : WKDownloadDelegate
    {
        MacDownload? _item;

        public override async void DecideDestination(WKDownload download, NSUrlResponse response, string suggestedFilename, Action<NSUrl> completionHandler)
        {
            try
            {
                _item = await owner._downloads.StartAsync(suggestedFilename, owner._reader.Request.Place ?? owner._reader.Title);
                owner._reader.Notice = $"{_item.Name}: downloading…";
                completionHandler(NSUrl.FromFilename(_item.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                owner._log.LogWarning(ex, "A download from an online _reader couldn't be placed");
                owner._reader.Notice = $"{suggestedFilename} couldn't be saved.";
                completionHandler(null!);
            }
        }

        public override void DidFinish(WKDownload download)
        {
            if (_item is null) return;
            _item.Done();
            owner._downloads.Finished(_item);
            owner._reader.Notice = $"{_item.Name}: {_item.Status}";
        }

        public override void DidFail(WKDownload download, NSError error, NSData? resumeData)
        {
            _item?.Failed(error.LocalizedDescription);
            owner._reader.Notice = $"{_item?.Name ?? "The download"}: {error.LocalizedDescription}";
        }
    }

    /// <summary>A link that would open a window of its own opens here when it's on the book's site, outside otherwise.</summary>
    sealed class Windows(OnlineReaderController owner) : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            if (navigationAction.Request.Url?.AbsoluteString is { } address)
            {
                if (owner._reader.Follows(address)) webView.LoadRequest(navigationAction.Request);
                else owner._reader.OpenOutside(address);
            }
            return null;
        }
    }
}

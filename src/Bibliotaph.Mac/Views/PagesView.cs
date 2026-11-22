using AppKit;
using Bibliotaph.Core.Reading;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A PDF's pages one under another in a scroll view, drawn by the viewer worker as they come into view and let go
/// once far out of it. Sizes follow the reader's zoom the way the Windows page surface does: 100% is 96 points an
/// inch, as WPF's units are, and fit width fits the most common page width. Text is selected on one page at a time, as
/// on Windows: drag across it, double-click a word or triple-click a line, then Copy; Escape lets it go.
/// </summary>
public sealed class PagesView : NSScrollView
{
    const double Gap = 14;
    const double Margin = 24;
    const int KeepAround = 4;
    /// <summary>Beyond this many pixels a page is drawn smaller, as the Windows surface tiles it instead.</summary>
    const double MaxPixels = 12_000_000;

    readonly PagesDocument _document;
    readonly List<PageLayer> _pages = [];
    MacReaderDocument? _pdf;
    double _zoom = ZoomSteps.FitWidth;
    double _pointsPerPdfPoint = 1;
    double _commonWidth = 612;
    int _current = -1;
    int _version;
    bool _drawing;
    bool _drawAgain;
    Func<int, Task<PageTextLayer>>? _text;
    PageLayer? _selectionPage;
    TextRange _selection;
    int? _anchor;
    bool _dragging;
    readonly List<CALayer> _selectionLayers = [];

    public PagesView()
    {
        _document = new PagesDocument(this);
        DocumentView = _document;
        HasVerticalScroller = true;
        HasHorizontalScroller = true;
        AutohidesScrollers = true;
        DrawsBackground = true;
        BackgroundColor = NSColor.UnderPageBackground;
        ContentView.PostsBoundsChangedNotifications = true;
        NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => OnScrolled(), ContentView);
        PostsFrameChangedNotifications = true;
        NSNotificationCenter.DefaultCenter.AddObserver(NSView.FrameChangedNotification, _ => Layout(keepPlace: true), this);
    }

    public PagesView(NativeHandle handle) : base(handle)
    {
        _document = new PagesDocument(this);
    }

    /// <summary>The zoom the pages are shown at, as a fit works out to, for a step from fit width to the next zoom.</summary>
    public double ZoomInUse => _pointsPerPdfPoint * 72 / 96;

    /// <summary>The page mostly in view, which the reader's page box and its saved place follow.</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>Something to tell the reader, such as why Copy did nothing.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>False when the PDF forbids copying its text; Copy then says so instead of copying.</summary>
    public bool CanCopy { get; set; } = true;

    /// <summary>Whether some text is selected, for Copy.</summary>
    public bool HasSelection => _selectionPage is not null && !_selection.IsEmpty;

    public void Load(MacReaderDocument pdf, DocInfo doc, Func<int, Task<PageTextLayer>> text, int pageIndex, double? pdfTop)
    {
        Unload();
        _pdf = pdf;
        _text = text;
        for (var i = 0; i < doc.PageCount; i++)
        {
            var size = i < doc.PageSizes.Count ? doc.PageSizes[i] : new PageSize(612, 792);
            var page = new PageLayer(i, size.Width, size.Height);
            _pages.Add(page);
            _document.Layer!.AddSublayer(page.Layer);
        }
        // Fit width fits the width most pages have, so one wide map in a book doesn't shrink the rest.
        _commonWidth = _pages.Count == 0 ? 612 : _pages.GroupBy(p => Math.Round(p.WidthPts)).OrderByDescending(g => g.Count()).First().Key;
        Layout(keepPlace: false);
        GoToPage(pageIndex, pdfTop, centred: true);
    }

    public void Unload()
    {
        _version++;
        ClearSelection();
        _text = null;
        foreach (var page in _pages) page.Layer.RemoveFromSuperLayer();
        _pages.Clear();
        _pdf = null;
        _current = -1;
        _highlights.Clear();
        _marked = ([], -1);
        _document.SetFrameSize(CGSize.Empty);
    }

    public void SetZoom(double zoom)
    {
        if (zoom == _zoom) return;
        _zoom = zoom;
        Layout(keepPlace: true);
    }

    /// <summary>
    /// Shows a page from its top, or from a point on it. A book just opened, at a zoom wider than the window, shows
    /// the middle of its pages, as Preview does; a jump within it keeps the reader where they were across the page.
    /// </summary>
    public void GoToPage(int pageIndex, double? pdfTop, bool centred = false)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count) return;
        var page = _pages[pageIndex];
        var y = page.Frame.Y - Gap / 2;
        if (pdfTop is { } top) y = page.Frame.Y + Math.Max(0, page.HeightPts - top) * _pointsPerPdfPoint - 40;
        var x = centred ? Math.Max(0, (_document.Frame.Width - ContentView.Bounds.Width) / 2) : ContentView.Bounds.X;
        ContentView.ScrollToPoint(new CGPoint(x, Math.Max(0, y)));
        ReflectScrolledClipView(ContentView);
        OnScrolled();
    }

    readonly List<CALayer> _highlights = [];
    (IReadOnlyList<PageHighlight> Items, int Current) _marked = ([], -1);

    /// <summary>Marks the find's matches, the current one stronger, and brings it into view.</summary>
    public void ShowHighlights(IReadOnlyList<PageHighlight> items, int current) => ShowHighlights(items, current, bringIntoView: true);

    void ShowHighlights(IReadOnlyList<PageHighlight> items, int current, bool bringIntoView)
    {
        _marked = (items, current);
        foreach (var layer in _highlights) layer.RemoveFromSuperLayer();
        _highlights.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.PageIndex < 0 || item.PageIndex >= _pages.Count) continue;
            var page = _pages[item.PageIndex];
            foreach (var rect in item.Rects)
            {
                // PDF boxes have their origin at the bottom left; the page's layer has its at the top left.
                var layer = new CALayer
                {
                    BackgroundColor = (i == current ? NSColor.SystemOrange.ColorWithAlphaComponent(0.45f) : NSColor.SystemYellow.ColorWithAlphaComponent(0.35f)).CGColor,
                    CornerRadius = 2,
                    Frame = new CGRect(rect.Left * _pointsPerPdfPoint, (page.HeightPts - rect.Top) * _pointsPerPdfPoint,
                        Math.Abs(rect.Right - rect.Left) * _pointsPerPdfPoint, Math.Abs(rect.Top - rect.Bottom) * _pointsPerPdfPoint),
                };
                page.Layer.AddSublayer(layer);
                _highlights.Add(layer);
            }
        }
        if (bringIntoView && current >= 0 && current < items.Count && items[current] is { Rects.Count: > 0 } hit && hit.PageIndex < _pages.Count)
        {
            var page = _pages[hit.PageIndex];
            var top = page.Frame.Y + (page.HeightPts - hit.Rects[0].Top) * _pointsPerPdfPoint;
            if (top < ContentView.Bounds.Y || top > ContentView.Bounds.Bottom - 60)
                GoToPage(hit.PageIndex, hit.Rects[0].Top + 120 / Math.Max(_pointsPerPdfPoint, 0.01));
        }
    }

    void Layout(bool keepPlace)
    {
        if (_pages.Count == 0) return;
        // The point at the middle of the view stays there through a zoom or resize, as it does in Preview: which page
        // it's on, and where on that page.
        var before = ContentView.Bounds;
        var anchor = keepPlace ? Find(before.GetMidY()) : -1;
        var (acrossPage, downPage) = anchor >= 0
            ? ((before.GetMidX() - _pages[anchor].Frame.X) / Math.Max(1, _pages[anchor].Frame.Width),
               (before.GetMidY() - _pages[anchor].Frame.Y) / Math.Max(1, _pages[anchor].Frame.Height))
            : (0.0, 0.0);

        var view = ContentView.Bounds.Size;
        var fitWidth = Math.Max(0.05, (view.Width - 2 * Margin) / _commonWidth);
        _pointsPerPdfPoint = _zoom switch
        {
            ZoomSteps.FitWidth => fitWidth,
            ZoomSteps.FitPage => Math.Min(fitWidth, Math.Max(0.05, (view.Height - 2 * Gap) / Math.Max(1, _pages.Max(p => p.HeightPts)))),
            _ => _zoom * 96 / 72,
        };
        var widest = _pages.Max(p => p.WidthPts) * _pointsPerPdfPoint;
        var width = Math.Max(view.Width, widest + 2 * Margin);
        var y = Gap;
        foreach (var page in _pages)
        {
            var w = page.WidthPts * _pointsPerPdfPoint;
            var h = page.HeightPts * _pointsPerPdfPoint;
            page.Frame = new CGRect((width - w) / 2, y, w, h);
            y += h + Gap;
        }
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        foreach (var page in _pages) page.Layer.Frame = page.Frame;
        CATransaction.Commit();
        // The find's marks are sized to the pages, so they're drawn again at the new size.
        ShowHighlights(_marked.Items, _marked.Current, bringIntoView: false);
        RefreshSelection();
        _document.SetFrameSize(new CGSize(width, y));
        Window?.InvalidateCursorRectsForView(_document);
        if (anchor >= 0)
        {
            var frame = _pages[anchor].Frame;
            var middle = new CGPoint(frame.X + acrossPage * frame.Width, frame.Y + downPage * frame.Height);
            var size = ContentView.Bounds.Size;
            ContentView.ScrollToPoint(new CGPoint(
                Math.Clamp(middle.X - size.Width / 2, 0, Math.Max(0, width - size.Width)),
                Math.Clamp(middle.Y - size.Height / 2, 0, Math.Max(0, y - size.Height))));
        }
        ReflectScrolledClipView(ContentView);
        OnScrolled();
    }

    void OnScrolled()
    {
        if (_pages.Count == 0) return;
        var visible = ContentView.Bounds;
        // The page under the top third of the view is the one being read.
        var probe = visible.Y + visible.Height / 3;
        var current = Find(probe);
        if (current != _current)
        {
            _current = current;
            CurrentPageChanged?.Invoke(this, current);
        }
        DrawAsync().FireAndForget();
    }

    int Find(double y)
    {
        int lo = 0, hi = _pages.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_pages[mid].Frame.Y <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    // ---------- Selection ----------

    /// <summary>The page under a point in the document, and the point on it in PDF points (origin bottom left).</summary>
    (PageLayer Page, double X, double Y)? PageAt(CGPoint point)
    {
        if (_pages.Count == 0) return null;
        var page = _pages[Find(point.Y)];
        if (!page.Frame.Contains(point)) return null;
        return (page, (point.X - page.Frame.X) / _pointsPerPdfPoint, page.HeightPts - (point.Y - page.Frame.Y) / _pointsPerPdfPoint);
    }

    async void Press(NSEvent e)
    {
        var point = _document.ConvertPointFromView(e.LocationInWindow, null);
        if (PageAt(point) is not var (page, x, y) || _text is null)
        {
            ClearSelection();
            return;
        }
        var clicks = e.ClickCount;
        if (_selectionPage != page) ClearSelection();
        _selectionPage = page;
        _dragging = clicks == 1;
        _anchor = null;
        SetSelection(default);
        var layer = await TextOf(page);
        if (_selectionPage != page || layer.HitTest(x, y) is not { } index) return;
        _anchor = index;
        SetSelection(clicks switch
        {
            2 => layer.WordAt(index),
            >= 3 => layer.LineAt(index),
            _ => default,
        });
    }

    void Drag(NSEvent e)
    {
        if (!_dragging) return;
        // Past the edge of the view, the pages scroll to follow the drag, as text views do.
        _document.Autoscroll(e);
        if (_selectionPage is not { Text.IsCompletedSuccessfully: true } page || _anchor is not { } anchor) return;
        var point = _document.ConvertPointFromView(e.LocationInWindow, null);
        var x = (point.X - page.Frame.X) / _pointsPerPdfPoint;
        var y = page.HeightPts - (point.Y - page.Frame.Y) / _pointsPerPdfPoint;
        if (page.Text.Result.HitTest(x, y) is { } focus) SetSelection(PageTextLayer.Between(anchor, focus));
    }

    void Release() => _dragging = false;

    /// <summary>A page's text, read once, as the pointer reaches it, so a drag can start at once.</summary>
    Task<PageTextLayer> TextOf(PageLayer page) => page.Text ??= LoadTextAsync(page.Index);

    async Task<PageTextLayer> LoadTextAsync(int index)
    {
        try
        {
            return _text is { } text ? await text(index) : PageTextLayer.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Selection is a convenience; a page whose text can't be read just can't be selected.
            return PageTextLayer.Empty;
        }
    }

    void Hover(NSEvent e)
    {
        if (PageAt(_document.ConvertPointFromView(e.LocationInWindow, null)) is var (page, _, _) && _text is not null) _ = TextOf(page);
    }

    void SetSelection(TextRange range)
    {
        _selection = range;
        RefreshSelection();
    }

    void RefreshSelection()
    {
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        foreach (var layer in _selectionLayers) layer.RemoveFromSuperLayer();
        _selectionLayers.Clear();
        if (_selectionPage is { Text.IsCompletedSuccessfully: true } page && !_selection.IsEmpty)
        {
            var colour = NSColor.SelectedTextBackground.ColorWithAlphaComponent(0.45f).CGColor;
            foreach (var rect in page.Text.Result.RectsOf(_selection))
            {
                var layer = new CALayer
                {
                    BackgroundColor = colour,
                    Frame = new CGRect(rect.Left * _pointsPerPdfPoint, (page.HeightPts - rect.Top) * _pointsPerPdfPoint,
                        rect.Width * _pointsPerPdfPoint, rect.Height * _pointsPerPdfPoint),
                };
                page.Layer.AddSublayer(layer);
                _selectionLayers.Add(layer);
            }
        }
        CATransaction.Commit();
    }

    void ClearSelection()
    {
        _selectionPage = null;
        _anchor = null;
        _dragging = false;
        SetSelection(default);
    }

    void CopySelection()
    {
        if (_selectionPage is not { Text.IsCompletedSuccessfully: true } page || _selection.IsEmpty) return;
        if (!CanCopy)
        {
            Notice?.Invoke(this, "This PDF doesn't allow copying its text.");
            return;
        }
        // The layer gives Windows line breaks for its clipboard; the Mac's are single.
        var text = page.Text.Result.TextOf(_selection).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length == 0) return;
        NSPasteboard.GeneralPasteboard.ClearContents();
        NSPasteboard.GeneralPasteboard.SetStringForType(text, NSPasteboardType.String.GetConstant()!);
    }

    /// <summary>Select All: all the text on the page being read.</summary>
    async void SelectPage()
    {
        if (_current < 0 || _current >= _pages.Count || _text is null) return;
        var page = _pages[_current];
        ClearSelection();
        _selectionPage = page;
        var layer = await TextOf(page);
        if (_selectionPage == page) SetSelection(layer.All);
    }

    /// <summary>Draws what's in view at the screen's sharpness, nearest first, one page at a time; a scroll meanwhile starts it again.</summary>
    async Task DrawAsync()
    {
        if (_drawing)
        {
            _drawAgain = true;
            return;
        }
        _drawing = true;
        try
        {
            do
            {
                _drawAgain = false;
                if (_pdf is not { } pdf || _pages.Count == 0) return;
                var version = _version;
                var visible = ContentView.Bounds;
                var first = Find(visible.Y);
                var last = Find(visible.Bottom);
                var backing = Window?.BackingScaleFactor ?? 2;
                foreach (var page in _pages)
                {
                    if (page.Index >= first - KeepAround && page.Index <= last + KeepAround) continue;
                    page.Clear();
                }
                foreach (var index in Enumerable.Range(Math.Max(0, first - 1), Math.Min(_pages.Count, last + 2) - Math.Max(0, first - 1)).OrderBy(i => Math.Abs(i - _current)))
                {
                    var page = _pages[index];
                    var scale = _pointsPerPdfPoint * backing;
                    var pixels = page.WidthPts * page.HeightPts * scale * scale;
                    if (pixels > MaxPixels) scale *= Math.Sqrt(MaxPixels / pixels);
                    if (page.DrawnScale >= scale * 0.98) continue;
                    try
                    {
                        var image = await pdf.RenderAsync(index, scale);
                        if (version != _version) return;
                        page.Show(image, scale);
                    }
                    catch (Exception ex) when (ex is PdfRenderException or WorkerException or TimeoutException)
                    {
                        if (version != _version) return;
                        page.ShowProblem($"Page {index + 1} could not be drawn.");
                    }
                    if (_drawAgain) break;
                }
            }
            while (_drawAgain);
        }
        finally
        {
            _drawing = false;
        }
    }

    sealed class PageLayer(int index, double widthPts, double heightPts)
    {
        public int Index { get; } = index;
        public double WidthPts { get; } = widthPts;
        public double HeightPts { get; } = heightPts;
        public CGRect Frame { get; set; }
        public double DrawnScale { get; private set; }
        public Task<PageTextLayer>? Text { get; set; }

        public CALayer Layer { get; } = new()
        {
            BackgroundColor = NSColor.White.CGColor,
            ShadowOpacity = 0.25f,
            ShadowRadius = 3,
            ShadowOffset = new CGSize(0, 1),
            ContentsGravity = CALayer.GravityResize,
        };

        public void Show(CGImage image, double scale)
        {
            Layer.Contents = image;
            DrawnScale = scale;
        }

        /// <summary>A page the worker couldn't draw says so where it would be, and isn't asked for again.</summary>
        public void ShowProblem(string text)
        {
            Layer.Contents = null;
            DrawnScale = double.MaxValue;
            Layer.AddSublayer(new CATextLayer
            {
                String = text,
                FontSize = 13,
                ForegroundColor = NSColor.SecondaryLabel.CGColor,
                TextAlignmentMode = CATextLayerAlignmentMode.Center,
                ContentsScale = 2,
                Frame = new CGRect(0, Layer.Bounds.Height / 2 - 10, Layer.Bounds.Width, 20),
            });
        }

        public void Clear()
        {
            if (DrawnScale == 0) return;
            Layer.Contents = null;
            DrawnScale = 0;
        }
    }

    /// <summary>
    /// The scroll view's document: top-down like the book, able to take the keyboard for scrolling, and where text is
    /// selected and copied, with the text cursor over the pages.
    /// </summary>
    sealed class PagesDocument : NSView
    {
        readonly PagesView _owner;

        public PagesDocument(PagesView owner)
        {
            _owner = owner;
            WantsLayer = true;
            AddTrackingArea(new NSTrackingArea(CGRect.Empty,
                NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
        }

        public override bool IsFlipped => true;

        public override bool AcceptsFirstResponder() => true;

        public override void MouseDown(NSEvent theEvent)
        {
            Window?.MakeFirstResponder(this);
            _owner.Press(theEvent);
        }

        public override void MouseDragged(NSEvent theEvent) => _owner.Drag(theEvent);

        public override void MouseUp(NSEvent theEvent) => _owner.Release();

        public override void MouseMoved(NSEvent theEvent) => _owner.Hover(theEvent);

        public override void ResetCursorRects()
        {
            foreach (var page in _owner._pages) AddCursorRect(page.Frame, NSCursor.IBeamCursor);
        }

        public override void KeyDown(NSEvent theEvent)
        {
            // Escape lets a selection go; with none, it goes on to whatever else Escape does here.
            if (theEvent.KeyCode == 53 && _owner.HasSelection) _owner.ClearSelection();
            else base.KeyDown(theEvent);
        }

        [Export("copy:")]
        public void Copy(NSObject sender) => _owner.CopySelection();

        [Export("selectAll:")]
        public void SelectAll(NSObject sender) => _owner.SelectPage();

        [Export("validateMenuItem:")]
        public bool ValidateMenuItem(NSMenuItem item) => item.Action?.Name != "copy:" || _owner.HasSelection;
    }
}

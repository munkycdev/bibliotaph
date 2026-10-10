using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Viewer;

/// <summary>
/// The pages of one PDF in a virtualized scroll list. It works out which pages are in view and asks the renderer for
/// exactly those pixels: a quick preview first, then the full page, then the next pages in the scroll direction.
/// Above 200% a page is drawn as tiles over its preview. It shows search highlights and lets the reader select text
/// on a page and copy it.
/// </summary>
public partial class PdfPagesView
{
    const double PageMargin = 8;                     // matches Margin="0,8" in the item template
    const double FullPageMaxPixels = 12_000_000;     // above this a page is drawn as tiles even at 200% or less
    const double PreviewFraction = 0.25;             // a preview is a quarter of the target scale
    const double PreviewMaxPixels = 2_000_000;
    const double TileAboveZoom = 2.0;
    const int TileSize = 512;
    const int KeepAround = 6;                        // pages further than this from the view give their pixels back
    const int PrefetchPages = 2;
    const double HitTopMargin = 120;                 // a hit is shown this far below the top of the view

    public const double FitWidth = 0;
    public const double FitPage = -1;

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PdfPagesView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PdfPagesView)d).ApplyZoom(keepPosition: true)));

    static readonly DependencyPropertyKey CurrentPageIndexKey = DependencyProperty.RegisterReadOnly(nameof(CurrentPageIndex), typeof(int), typeof(PdfPagesView),
        new PropertyMetadata(0, (d, _) => ((PdfPagesView)d).CurrentPageChanged?.Invoke(d, EventArgs.Empty)));

    public static readonly DependencyProperty CurrentPageIndexProperty = CurrentPageIndexKey.DependencyProperty;

    static readonly DependencyPropertyKey PositionTextKey = DependencyProperty.RegisterReadOnly(nameof(PositionText), typeof(string), typeof(PdfPagesView),
        new PropertyMetadata(""));

    public static readonly DependencyProperty PositionTextProperty = PositionTextKey.DependencyProperty;

    public static readonly DependencyProperty CanCopyProperty = DependencyProperty.Register(nameof(CanCopy), typeof(bool), typeof(PdfPagesView),
        new PropertyMetadata(true));

    readonly ObservableCollection<ViewerPage> _pages = [];
    PdfRenderer? _renderer;
    Func<int, Task<PageTextLayer>>? _textSource;
    ScrollViewer? _scroll;
    VirtualizingStackPanel? _panel;
    double _dipPerPoint = 1;
    bool _renderQueued;
    bool _laidOut;
    int _direction = 1;
    GoTo? _pendingGoTo;
    IReadOnlyList<PageHighlight> _highlights = [];
    int _currentHighlight = -1;

    ViewerPage? _selectionPage;
    FrameworkElement? _selectionSurface;
    int? _anchor;
    TextRange _selection;
    bool _dragging;

    public PdfPagesView()
    {
        InitializeComponent();
        Pages.ItemsSource = _pages;
        Pages.PreviewMouseLeftButtonDown += Pages_PreviewMouseLeftButtonDown;
        Pages.PreviewMouseMove += Pages_PreviewMouseMove;
        Pages.PreviewMouseLeftButtonUp += Pages_PreviewMouseLeftButtonUp;
        Pages.PreviewMouseWheel += Pages_PreviewMouseWheel;
        Pages.PreviewKeyDown += Pages_PreviewKeyDown;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(), (_, e) => e.CanExecute = HasSelection));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, async (_, _) => await SelectAllOnCurrentPageAsync(),
            (_, e) => e.CanExecute = _pages.Count > 0));
    }

    /// <summary>
    /// Pixels per point over 96/72: 1 is 100%. <see cref="FitWidth"/> fits the most common page width to the view;
    /// <see cref="FitPage"/> fits a whole page.
    /// </summary>
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>The page under the top third of the view.</summary>
    public int CurrentPageIndex => (int)GetValue(CurrentPageIndexProperty);

    /// <summary>"Printed p. 42 · PDF page 46 of 180", or the PDF page alone when the book has no labels of its own.</summary>
    public string PositionText => (string)GetValue(PositionTextProperty);

    /// <summary>False when the PDF forbids copying its text; Copy then explains instead of copying.</summary>
    public bool CanCopy { get => (bool)GetValue(CanCopyProperty); set => SetValue(CanCopyProperty, value); }

    public IReadOnlyList<ViewerPage> PageModels => _pages;

    public bool HasSelection => _selectionPage is not null && !_selection.IsEmpty;

    /// <summary>The page the selected text is on, or null with nothing selected: Add pages… starts there.</summary>
    public int? SelectionPageIndex => HasSelection ? _selectionPage!.Index : null;

    /// <summary>Something to tell the reader, such as why Copy did nothing.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>The page in view changed, by scrolling or by <see cref="GoToPage"/>.</summary>
    public event EventHandler? CurrentPageChanged;

    /// <summary>Shows a newly opened document. <paramref name="text"/> gives a page's selectable text when it's needed.</summary>
    public void Load(PdfRenderer renderer, DocInfo doc, Func<int, Task<PageTextLayer>> text, int pageIndex = 0, double? pdfTop = null)
    {
        Unload();
        _renderer = renderer;
        _renderer.Rendered += OnRendered;
        _textSource = text;
        for (var i = 0; i < doc.PageCount; i++)
        {
            var size = i < doc.PageSizes.Count ? doc.PageSizes[i] : new PageSize(612, 792);
            var label = i < doc.PageLabels.Count ? doc.PageLabels[i] : null;
            _pages.Add(new ViewerPage(i, label, size.Width > 0 ? size.Width : 612, size.Height > 0 ? size.Height : 792));
        }
        _pendingGoTo = new GoTo(Math.Clamp(pageIndex, 0, Math.Max(0, _pages.Count - 1)), 0, pdfTop);
        ApplyZoom(keepPosition: false);
    }

    public void Unload()
    {
        if (_renderer is not null)
        {
            _renderer.Rendered -= OnRendered;
            _renderer.Submit([]);
        }
        _renderer = null;
        _textSource = null;
        ClearSelection();
        _highlights = [];
        _currentHighlight = -1;
        _pages.Clear();
        _panel = null;
        _laidOut = false;
    }

    /// <summary>
    /// Scrolls to a page; with <paramref name="pdfTop"/> (PDF points from the bottom of the page), to that spot on it,
    /// a little below the top of the view.
    /// </summary>
    public void GoToPage(int index, double? pdfTop = null)
    {
        if (index < 0 || index >= _pages.Count) return;
        _pendingGoTo = new GoTo(index, 0, pdfTop);
        ScrollToPending();
    }

    /// <summary>Marks search hits; the one at <paramref name="current"/> stands out.</summary>
    public void ShowHighlights(IReadOnlyList<PageHighlight> highlights, int current = -1)
    {
        _highlights = highlights;
        _currentHighlight = current;
        RefreshHighlights();
    }

    // ---------- Zoom and layout ----------

    void Pages_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        // A document loaded while the view was hidden is laid out when it first gets a size.
        var fits = Zoom == FitWidth ? e.WidthChanged : Zoom == FitPage && (e.WidthChanged || e.HeightChanged);
        if (!_laidOut || fits) ApplyZoom(keepPosition: _pendingGoTo is null);
        else QueueRender();
    }

    void ApplyZoom(bool keepPosition)
    {
        if (_pages.Count == 0 || Pages.ActualWidth <= 0) return;
        if (keepPosition && _laidOut && _pendingGoTo is null) _pendingGoTo = CurrentPosition();
        _laidOut = true;
        _dipPerPoint = Zoom > 0 ? Zoom * 96.0 / 72.0 : Zoom == FitPage ? FitPageDipPerPoint() : FitWidthDipPerPoint();
        foreach (var page in _pages)
        {
            page.Generation++;
            page.ClearPixels();
            page.Width = page.WidthPts * _dipPerPoint;
            page.Height = page.HeightPts * _dipPerPoint;
        }
        RefreshHighlights();
        RefreshSelection();
        ScrollToPending();
    }

    double FitWidthDipPerPoint()
    {
        // Fit the most common page size, so one wide fold-out doesn't shrink the whole book.
        var available = Math.Max(200, Pages.ActualWidth - SystemParameters.VerticalScrollBarWidth - 48);
        return Math.Min(available / CommonPage().WidthPts, 4 * 96.0 / 72.0);
    }

    double FitPageDipPerPoint()
    {
        var available = Math.Max(150, Pages.ActualHeight - 2 * PageMargin - 8);
        return Math.Min(FitWidthDipPerPoint(), available / CommonPage().HeightPts);
    }

    ViewerPage CommonPage() =>
        _pages.GroupBy(p => (Math.Round(p.WidthPts), Math.Round(p.HeightPts))).OrderByDescending(g => g.Count()).First().First();

    void Pages_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _pages.Count == 0) return;
        e.Handled = true;
        StepZoom(up: e.Delta > 0);
    }

    /// <summary>Zooms one step in or out from the size on screen, also when the pages are fitted.</summary>
    public void StepZoom(bool up)
    {
        if (_pages.Count > 0) Zoom = ZoomSteps.Next(_dipPerPoint * 72.0 / 96.0, up);
    }

    /// <summary>
    /// The window moved to a monitor with another scale. The pages keep their size in DIPs, so nothing else would
    /// notice, but their pixels were drawn for the old scale: draw them again, sharp, where the reader was.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_laidOut && oldDpi.PixelsPerDip != newDpi.PixelsPerDip) ApplyZoom(keepPosition: true);
    }

    // ---------- Rendering what is on screen ----------

    void Pages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) _direction = e.VerticalChange > 0 ? 1 : -1;
        QueueRender();
        UpdatePosition();
    }

    void OnRendered(ViewerPage page) => UpdatePosition();

    void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _renderQueued = false;
            ScheduleRender();
        });
    }

    /// <summary>Works out what is visible from the realized containers and asks for exactly those pixels.</summary>
    void ScheduleRender()
    {
        if (_renderer is null || _pages.Count == 0) return;
        _scroll ??= FindChild<ScrollViewer>(Pages);
        _panel ??= FindChild<VirtualizingStackPanel>(Pages);
        if (_scroll is null || _panel is null) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var scale = _dipPerPoint * pixelsPerDip;
        var previews = new List<RenderJob>();
        var visible = new List<RenderJob>();
        var prefetch = new List<RenderJob>();
        int firstVisible = int.MaxValue, lastVisible = -1;

        foreach (var child in _panel.Children.OfType<ListBoxItem>())
        {
            if (child.DataContext is not ViewerPage page) continue;
            var item = child.TransformToAncestor(_scroll).TransformBounds(new Rect(child.RenderSize));
            var pageTop = item.Top + PageMargin;
            var pageLeft = item.Left + ((item.Width - page.Width) / 2);
            // The part of the page inside the view, in DIPs from the page's top-left corner.
            var left = Math.Max(0, -pageLeft);
            var top = Math.Max(0, -pageTop);
            var right = Math.Min(page.Width, _scroll.ViewportWidth - pageLeft);
            var bottom = Math.Min(page.Height, _scroll.ViewportHeight - pageTop);
            if (right <= left || bottom <= top) continue;

            firstVisible = Math.Min(firstVisible, page.Index);
            lastVisible = Math.Max(lastVisible, page.Index);
            AddJobs(page, new Rect(left, top, right - left, bottom - top), scale, pixelsPerDip, previews, visible);
        }
        if (lastVisible < 0) return;

        // The next pages in the direction of reading, so turning the page finds them drawn.
        for (var i = 1; i <= PrefetchPages; i++)
        {
            var index = _direction >= 0 ? lastVisible + i : firstVisible - i;
            if (index < 0 || index >= _pages.Count) continue;
            var page = _pages[index];
            AddJobs(page, new Rect(0, 0, page.Width, Math.Min(page.Height, _scroll.ViewportHeight)), scale, pixelsPerDip, prefetch, prefetch);
        }

        // Bound memory: far-away pages give their bitmaps back.
        foreach (var page in _pages)
        {
            if (page.HasPixels && (page.Index < firstVisible - KeepAround || page.Index > lastVisible + KeepAround))
            {
                page.Generation++;
                page.ClearPixels();
            }
        }

        _renderer.Submit([.. previews, .. visible, .. prefetch]);
    }

    void AddJobs(ViewerPage page, Rect band, double scale, double pixelsPerDip, List<RenderJob> previews, List<RenderJob> jobs)
    {
        var pixelWidth = (int)Math.Round(page.WidthPts * scale);
        var pixelHeight = (int)Math.Round(page.HeightPts * scale);
        var pixels = (double)pixelWidth * pixelHeight;
        var tiled = _dipPerPoint * 72.0 / 96.0 > TileAboveZoom + 1e-6 || pixels > FullPageMaxPixels;

        // A quick low-resolution render first, so a page never sits blank while its full render runs.
        if (page.Image is null)
        {
            var previewScale = Math.Min(scale * PreviewFraction, scale * Math.Sqrt(PreviewMaxPixels / pixels));
            previews.Add(new RenderJob(page, page.Generation, previewScale, pixelsPerDip, Preview: true));
        }

        if (!tiled)
        {
            if (page.Image is null || page.IsPreview || Math.Abs(page.RenderedScale - scale) > 1e-6)
                jobs.Add(new RenderJob(page, page.Generation, scale, pixelsPerDip));
            return;
        }

        // High zoom: sharp tiles for the part in view, over the preview.
        var x0 = (int)(band.Left * pixelsPerDip) / TileSize;
        var x1 = (int)(Math.Max(band.Left, band.Right - 1) * pixelsPerDip) / TileSize;
        var y0 = (int)(band.Top * pixelsPerDip) / TileSize;
        var y1 = (int)(Math.Max(band.Top, band.Bottom - 1) * pixelsPerDip) / TileSize;
        for (var ty = y0; ty <= y1; ty++)
        {
            for (var tx = x0; tx <= x1; tx++)
            {
                var x = tx * TileSize;
                var y = ty * TileSize;
                if (x >= pixelWidth || y >= pixelHeight || page.RenderedTiles.Contains((x, y))) continue;
                var tile = new PixelRect(x, y, Math.Min(TileSize, pixelWidth - x), Math.Min(TileSize, pixelHeight - y));
                jobs.Add(new RenderJob(page, page.Generation, scale, pixelsPerDip, tile));
            }
        }
    }

    // ---------- Position ----------

    /// <summary>Where to scroll once the pages are laid out: a page, how far down it, or a spot on it in PDF points.</summary>
    readonly record struct GoTo(int Index, double Fraction, double? PdfTop);

    /// <summary>The page under the top third of the view, and how far down it the top of the view is.</summary>
    GoTo CurrentPosition()
    {
        if (_pages.Count == 0) return default;
        _scroll ??= FindChild<ScrollViewer>(Pages);
        _panel ??= FindChild<VirtualizingStackPanel>(Pages);
        if (_scroll is null || _panel is null) return new GoTo(CurrentPageIndex, 0, null);
        var probe = _scroll.ViewportHeight / 3;
        foreach (var child in _panel.Children.OfType<ListBoxItem>())
        {
            if (child.DataContext is not ViewerPage page) continue;
            var item = child.TransformToAncestor(_scroll).TransformBounds(new Rect(child.RenderSize));
            if (item.Top <= probe && item.Bottom > probe)
                return new GoTo(page.Index, Math.Clamp(-(item.Top + PageMargin) / Math.Max(1, page.Height), 0, 1), null);
        }
        return new GoTo(CurrentPageIndex, 0, null);
    }

    void ScrollToPending()
    {
        if (_pendingGoTo is not { } target || Pages.ActualWidth <= 0 || _pages.Count == 0) return;
        _scroll ??= FindChild<ScrollViewer>(Pages);
        if (_scroll is null) return;
        _pendingGoTo = null;
        var index = Math.Clamp(target.Index, 0, _pages.Count - 1);
        var page = _pages[index];
        var offset = target.PdfTop is { } pdfTop
            ? Math.Max(0, ((page.HeightPts - pdfTop) * _dipPerPoint) - HitTopMargin)
            : target.Fraction * page.Height;
        Pages.ScrollIntoView(page);
        Pages.UpdateLayout();
        if (Pages.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
        {
            var top = container.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + top + PageMargin + offset);
        }
        QueueRender();
        UpdatePosition();
    }

    void UpdatePosition()
    {
        if (_pages.Count == 0)
        {
            SetValue(PositionTextKey, "");
            return;
        }
        var index = CurrentPosition().Index;
        SetValue(CurrentPageIndexKey, index);
        var page = _pages[index];
        var pdf = $"PDF page {(index + 1).ToString("N0", CultureInfo.CurrentCulture)} of {_pages.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        SetValue(PositionTextKey, page.Label is { Length: > 0 } label && label != (index + 1).ToString(CultureInfo.InvariantCulture)
            ? $"Printed p. {label} · {pdf}"
            : pdf);
    }

    void Pages_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pages.Count == 0) return;
        if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control) GoToPage(0);
        else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control) GoToPage(_pages.Count - 1);
        else if (e.Key == Key.Escape && HasSelection) ClearSelection();
        else return;
        e.Handled = true;
    }

    // ---------- Highlights ----------

    /// <summary>Converts hit rectangles (PDF points, origin bottom-left) to DIPs on each page.</summary>
    void RefreshHighlights()
    {
        foreach (var page in _pages) if (page.Highlights.Count > 0) page.Highlights.Clear();
        for (var i = 0; i < _highlights.Count; i++)
        {
            var hit = _highlights[i];
            if (hit.PageIndex < 0 || hit.PageIndex >= _pages.Count) continue;
            var page = _pages[hit.PageIndex];
            foreach (var rect in hit.Rects) page.Highlights.Add(ToBox(page, rect, i == _currentHighlight));
        }
    }

    PageBox ToBox(ViewerPage page, PageRect r, bool current = false) => new(
        r.Left * _dipPerPoint,
        (page.HeightPts - r.Top) * _dipPerPoint,
        Math.Max(1, r.Width * _dipPerPoint),
        Math.Max(1, r.Height * _dipPerPoint),
        current);

    // ---------- Selection ----------

    async void Pages_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Pages.Focus();
        if (Surface(e.OriginalSource) is not { DataContext: ViewerPage page } surface || _textSource is null)
        {
            ClearSelection();
            return;
        }
        e.Handled = true;
        var clicks = e.ClickCount;
        var point = ToPdf(page, e.GetPosition(surface));
        if (_selectionPage != page) ClearSelection();
        _selectionPage = page;
        _selectionSurface = surface;
        _dragging = clicks == 1;
        if (_dragging) Mouse.Capture(Pages);

        var layer = await TextOf(page);
        if (_selectionPage != page) return;
        if (layer.HitTest(point.X, point.Y) is not { } index)
        {
            _anchor = null;
            SetSelection(default);
            return;
        }
        _anchor = index;
        SetSelection(clicks switch
        {
            2 => layer.WordAt(index),
            >= 3 => layer.LineAt(index),
            _ => default,
        });
    }

    void Pages_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && _selectionPage is { Text.IsCompletedSuccessfully: true } page && _selectionSurface is { } surface && _anchor is { } anchor)
        {
            var point = ToPdf(page, e.GetPosition(surface));
            if (page.Text.Result.HitTest(point.X, point.Y) is { } focus) SetSelection(PageTextLayer.Between(anchor, focus));
            return;
        }
        // Read a page's text as the pointer reaches it, so a drag can start at once.
        if (!_dragging && Surface(e.OriginalSource) is { DataContext: ViewerPage hovered } && hovered.Text is null) _ = TextOf(hovered);
    }

    void Pages_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Mouse.Capture(null);
    }

    Task<PageTextLayer> TextOf(ViewerPage page) => page.Text ??= LoadTextAsync(page);

    async Task<PageTextLayer> LoadTextAsync(ViewerPage page)
    {
        try
        {
            return _textSource is { } source ? await source(page.Index) : PageTextLayer.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Selection is a convenience; a page whose text can't be read just can't be selected.
            return PageTextLayer.Empty;
        }
    }

    Point ToPdf(ViewerPage page, Point dip) => new(dip.X / _dipPerPoint, page.HeightPts - (dip.Y / _dipPerPoint));

    void SetSelection(TextRange range)
    {
        _selection = range;
        RefreshSelection();
        CommandManager.InvalidateRequerySuggested();
    }

    void RefreshSelection()
    {
        if (_selectionPage is not { } page) return;
        page.Selection.Clear();
        if (_selection.IsEmpty || page.Text is not { IsCompletedSuccessfully: true } text) return;
        foreach (var rect in text.Result.RectsOf(_selection)) page.Selection.Add(ToBox(page, rect));
    }

    void ClearSelection()
    {
        _selectionPage?.Selection.Clear();
        _selectionPage = null;
        _selectionSurface = null;
        _anchor = null;
        _selection = default;
        _dragging = false;
    }

    /// <summary>Selects all the text on the page in view (Ctrl+A).</summary>
    public async Task SelectAllOnCurrentPageAsync()
    {
        if (_pages.Count == 0) return;
        var page = _pages[CurrentPageIndex];
        ClearSelection();
        _selectionPage = page;
        var layer = await TextOf(page);
        if (_selectionPage == page) SetSelection(layer.All);
    }

    /// <summary>Copies the selection (Ctrl+C), unless the PDF forbids it, in which case it says so.</summary>
    public void CopySelection()
    {
        if (_selectionPage is not { Text.IsCompletedSuccessfully: true } page || _selection.IsEmpty) return;
        if (!CanCopy)
        {
            Notice?.Invoke(this, "This PDF doesn't allow copying its text.");
            return;
        }
        var text = page.Text.Result.TextOf(_selection);
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notice?.Invoke(this, "The clipboard is busy. Try copying again.");
        }
    }

    static FrameworkElement? Surface(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = UpTree(current))
        {
            if (current is FrameworkElement { Name: "Surface" } surface) return surface;
            if (current is ListBox) return null;
        }
        return null;
    }

    static DependencyObject? UpTree(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}

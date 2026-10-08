using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.Spike.Contracts;
using Microsoft.Win32;

namespace Bibliotaph.Spike.Viewer;

public partial class MainWindow : Window
{
    const double PageMargin = 8;                    // matches Margin="0,8" in the item template
    const double FullPageMaxPixels = 12_000_000;    // above this a page is drawn as tiles over a low-res preview
    const double PreviewMaxPixels = 2_000_000;
    const int TileSize = 1024;
    const int KeepAround = 6;                       // pages beyond this distance from the viewport drop their pixels

    readonly PageRenderer _renderer;
    readonly ObservableCollection<PageModel> _pages = [];
    readonly DispatcherTimer _stallTimer;
    readonly DispatcherTimer _statsTimer;
    readonly Stopwatch _sinceTick = Stopwatch.StartNew();

    ScrollViewer? _scroll;
    VirtualizingStackPanel? _panel;
    double _zoom;                                   // 0 = fit width, otherwise 1.0 = 100%
    double _dipPerPoint = 1;
    bool _renderQueued;
    List<SearchHit> _hits = [];
    int _hitIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        Pages.ItemsSource = _pages;
        _renderer = new PageRenderer(Dispatcher);

        // A 16 ms heartbeat: any tick that arrives much later means the UI thread was blocked.
        _stallTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, (_, _) =>
        {
            var late = _sinceTick.Elapsed.TotalMilliseconds - 16;
            _sinceTick.Restart();
            if (_pages.Count > 0 && late > _renderer.Metrics.WorstUiStallMs) _renderer.Metrics.WorstUiStallMs = late;
        }, Dispatcher);
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            var peak = _renderer.WorkerPeakBytes is { } b ? $"{b / 1048576.0:0} MB" : "n/a";
            Stats.Text = $"{_renderer.Metrics.Summary()} · worker peak {peak} · worker restarts {_renderer.WorkerRestarts}";
        }, Dispatcher);

        Loaded += async (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(Pages);
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            if (arg is not null && File.Exists(arg)) await OpenDocumentAsync(arg);
        };
        Closed += async (_, _) => await _renderer.DisposeAsync();
    }

    // ---------- Opening ----------

    async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Open a PDF" };
        if (dialog.ShowDialog(this) == true) await OpenDocumentAsync(dialog.FileName);
    }

    async Task OpenDocumentAsync(string path)
    {
        var name = Path.GetFileName(path);
        string? password = null;
        DocInfo doc;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                doc = await _renderer.OpenAsync(path, password);
                break;
            }
            catch (PdfOpenException ex) when (ex.Kind == ErrorKind.Password)
            {
                var dialog = new PasswordDialog(name, retry: password is not null) { Owner = this };
                if (dialog.ShowDialog() != true) { LabelInfo.Text = $"{name} was not opened."; return; }
                password = dialog.Password;
                sw.Restart();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{name} could not be opened.\n\n{ex.Message}", "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Title = $"{name} · Bibliotaph PDF spike viewer";
        _hits = [];
        _hitIndex = -1;
        HitInfo.Text = "";
        _renderer.Metrics.Reset();
        _pages.Clear();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var size = doc.PageSizes[i];
            _pages.Add(new PageModel(i, doc.PageLabels[i], size.Width > 0 ? size.Width : 612, size.Height > 0 ? size.Height : 792));
        }
        var protection = doc.IsEncrypted ? $" · encrypted (copy {(doc.CanCopy ? "allowed" : "not allowed")})" : "";
        LabelInfo.Text = $"Opened in {sw.ElapsedMilliseconds} ms{protection}";
        ApplyZoom(keepPosition: false);
    }

    // ---------- Zoom and layout ----------

    void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem { Tag: string tag }) _zoom = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        if (_pages.Count > 0) ApplyZoom(keepPosition: true);
    }

    void Pages_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        if (_zoom == 0 && e.WidthChanged) ApplyZoom(keepPosition: true);
        else QueueRender();
    }

    void ApplyZoom(bool keepPosition)
    {
        var (index, fraction) = keepPosition ? CurrentPosition() : (0, 0.0);
        _dipPerPoint = _zoom > 0 ? _zoom * 96.0 / 72.0 : FitWidthDipPerPoint();
        foreach (var page in _pages)
        {
            page.Generation++;
            page.ClearPixels();
            page.BlankSince = null;
            page.Width = page.WidthPts * _dipPerPoint;
            page.Height = page.HeightPts * _dipPerPoint;
        }
        RefreshHighlights();
        GoToPage(index, fraction * _pages[index].Height);
    }

    double FitWidthDipPerPoint()
    {
        // Fit the most common page width, so one wide fold-out does not shrink the whole book.
        var common = _pages.GroupBy(p => Math.Round(p.WidthPts)).OrderByDescending(g => g.Count()).First().Key;
        var available = Math.Max(200, Pages.ActualWidth - SystemParameters.VerticalScrollBarWidth - 32);
        return available / common;
    }

    // ---------- Rendering what is on screen ----------

    void Pages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        QueueRender();
        UpdatePageInfo();
    }

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
        if (_scroll is null || _pages.Count == 0) return;
        _panel ??= FindChild<VirtualizingStackPanel>(Pages);
        if (_panel is null) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var scale = _dipPerPoint * pixelsPerDip;
        var now = DateTime.UtcNow;
        var visibleJobs = new List<RenderJob>();
        var prefetchJobs = new List<RenderJob>();
        int minRealized = int.MaxValue, maxRealized = -1;

        foreach (var child in _panel.Children.OfType<ListBoxItem>())
        {
            if (child.DataContext is not PageModel page) continue;
            minRealized = Math.Min(minRealized, page.Index);
            maxRealized = Math.Max(maxRealized, page.Index);

            var item = child.TransformToAncestor(_scroll).TransformBounds(new Rect(child.RenderSize));
            var pageTop = item.Top + PageMargin;
            var pageLeft = item.Left + (item.Width - page.Width) / 2;
            // The part of the page inside the viewport, in DIPs from the page's top-left corner.
            var left = Math.Max(0, -pageLeft);
            var top = Math.Max(0, -pageTop);
            var right = Math.Min(page.Width, _scroll.ViewportWidth - pageLeft);
            var bottom = Math.Min(page.Height, _scroll.ViewportHeight - pageTop);

            if (right > left && bottom > top)
            {
                if (!page.HasPixels) page.BlankSince ??= now;
                AddJobs(page, new Rect(left, top, right - left, bottom - top), scale, pixelsPerDip, visibleJobs);
            }
            else
            {
                page.BlankSince = null;
                AddJobs(page, new Rect(0, 0, page.Width, page.Height), scale, pixelsPerDip, prefetchJobs);
            }
        }

        // Bound memory: far-away pages give their bitmaps back.
        if (maxRealized >= 0)
        {
            foreach (var page in _pages)
            {
                if (page.HasPixels && (page.Index < minRealized - KeepAround || page.Index > maxRealized + KeepAround))
                {
                    page.Generation++;
                    page.ClearPixels();
                    page.BlankSince = null;
                }
            }
        }

        visibleJobs.AddRange(prefetchJobs);
        _renderer.Submit(visibleJobs);
    }

    void AddJobs(PageModel page, Rect band, double scale, double pixelsPerDip, List<RenderJob> jobs)
    {
        var pixelWidth = (int)Math.Round(page.WidthPts * scale);
        var pixelHeight = (int)Math.Round(page.HeightPts * scale);
        if ((double)pixelWidth * pixelHeight <= FullPageMaxPixels)
        {
            if (page.Image is null || Math.Abs(page.RenderedScale - scale) > 1e-6)
                jobs.Add(new RenderJob(page, page.Generation, scale, pixelsPerDip, null));
            return;
        }

        // High zoom: a quick low-res preview first, then sharp tiles for the visible part only.
        if (page.Image is null)
        {
            var previewScale = scale * Math.Sqrt(PreviewMaxPixels / ((double)pixelWidth * pixelHeight));
            jobs.Add(new RenderJob(page, page.Generation, previewScale, pixelsPerDip, null));
        }
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

    // ---------- Navigation ----------

    /// <summary>The page under the top third of the viewport, and how far down it the viewport is.</summary>
    (int Index, double Fraction) CurrentPosition()
    {
        if (_scroll is null || _pages.Count == 0) return (0, 0);
        _panel ??= FindChild<VirtualizingStackPanel>(Pages);
        if (_panel is null) return (0, 0);
        var probe = _scroll.ViewportHeight / 3;
        foreach (var child in _panel.Children.OfType<ListBoxItem>())
        {
            if (child.DataContext is not PageModel page) continue;
            var item = child.TransformToAncestor(_scroll).TransformBounds(new Rect(child.RenderSize));
            if (item.Top <= probe && item.Bottom > probe)
                return (page.Index, Math.Clamp(-item.Top / Math.Max(1, page.Height), 0, 1));
        }
        return (0, 0);
    }

    void GoToPage(int index, double offsetInPage = 0)
    {
        if (_scroll is null || index < 0 || index >= _pages.Count) return;
        Pages.ScrollIntoView(_pages[index]);
        Pages.UpdateLayout();
        if (Pages.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
        {
            var top = container.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + top + offsetInPage);
        }
        QueueRender();
        UpdatePageInfo();
    }

    void UpdatePageInfo()
    {
        if (_pages.Count == 0) return;
        var (index, _) = CurrentPosition();
        var page = _pages[index];
        if (!PageBox.IsKeyboardFocused) PageBox.Text = (index + 1).ToString();
        PageOf.Text = $"of {_pages.Count}";
        LabelInfo.Text = page.Label is { } label && label != (index + 1).ToString()
            ? $"Printed p. {label} · PDF page {index + 1} of {_pages.Count}"
            : $"PDF page {index + 1} of {_pages.Count}";
    }

    void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = PageBox.Text.Trim();
        // A printed label wins over a PDF page number, because that is what the book says.
        var byLabel = _pages.FirstOrDefault(p => string.Equals(p.Label, text, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null) GoToPage(byLabel.Index);
        else if (int.TryParse(text, out var number)) GoToPage(Math.Clamp(number - 1, 0, _pages.Count - 1));
        Pages.Focus();
    }

    void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentPosition().Index - 1);
    void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentPosition().Index + 1);

    // ---------- Search ----------

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Find_Click(sender, e);
    }

    async void Find_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || _pages.Count == 0) return;
        HitInfo.Text = "Searching…";
        var sw = Stopwatch.StartNew();
        try
        {
            _hits = await _renderer.FindAsync(query);
        }
        catch (Exception ex)
        {
            HitInfo.Text = $"Search failed: {ex.Message}";
            return;
        }
        var pages = _hits.Select(h => h.PageIndex).Distinct().Count();
        HitInfo.Text = _hits.Count == 0
            ? $"No matches · {sw.ElapsedMilliseconds} ms"
            : $"{_hits.Count} matches on {pages} pages · {sw.ElapsedMilliseconds} ms";
        _hitIndex = _hits.Count > 0 ? 0 : -1;
        RefreshHighlights();
        ShowCurrentHit();
    }

    void NextHit_Click(object sender, RoutedEventArgs e) => StepHit(+1);
    void PrevHit_Click(object sender, RoutedEventArgs e) => StepHit(-1);

    void StepHit(int delta)
    {
        if (_hits.Count == 0) return;
        _hitIndex = (_hitIndex + delta + _hits.Count) % _hits.Count;
        RefreshHighlights();
        ShowCurrentHit();
    }

    void ShowCurrentHit()
    {
        if (_hitIndex < 0) return;
        var hit = _hits[_hitIndex];
        var page = _pages[hit.PageIndex];
        var top = hit.Rects.Count > 0 ? (page.HeightPts - hit.Rects[0].Top) * _dipPerPoint : 0;
        GoToPage(hit.PageIndex, Math.Max(0, top - 120));
    }

    /// <summary>Converts hit rectangles (PDF points, origin bottom-left) to DIPs on each page.</summary>
    void RefreshHighlights()
    {
        foreach (var page in _pages) if (page.Highlights.Count > 0) page.Highlights.Clear();
        for (var i = 0; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            var page = _pages[hit.PageIndex];
            foreach (var r in hit.Rects)
            {
                page.Highlights.Add(new HighlightBox(
                    r.Left * _dipPerPoint,
                    (page.HeightPts - r.Top) * _dipPerPoint,
                    Math.Max(1, (r.Right - r.Left) * _dipPerPoint),
                    Math.Max(1, (r.Top - r.Bottom) * _dipPerPoint),
                    i == _hitIndex));
            }
        }
    }

    // ---------- Keyboard ----------

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.O) { e.Handled = true; Open_Click(sender, e); }
        else if (ctrl && e.Key == Key.F) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (e.Key == Key.F3) { e.Handled = true; StepHit(shift ? -1 : +1); }
        else if (e.Key == Key.Escape && _hits.Count > 0) { e.Handled = true; _hits = []; _hitIndex = -1; HitInfo.Text = ""; RefreshHighlights(); }
    }

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

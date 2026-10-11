using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Processing;
using Bibliotaph.Viewer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// One page in the reader's thumbnail strip: its picture once drawn (its number until then), and its marks: the page in
/// view, a page of the binder item in hand, and how many notes are on it.
/// </summary>
public sealed partial class PageThumbnail(int index, string page, double width, double height, double scale) : ObservableObject
{
    public int Index { get; } = index;

    /// <summary>The page as the book numbers it: "xii", "42".</summary>
    public string Page { get; } = page;

    /// <summary>The thumbnail's size in DIPs, so the strip is laid out before anything is drawn.</summary>
    public double Width { get; } = width;

    public double Height { get; } = height;

    /// <summary>Pixels per PDF point that draw the page at the size the thumbnail cache keeps.</summary>
    public double Scale { get; } = scale;

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool InItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes), nameof(AccessibleName))]
    public partial int NoteCount { get; set; }

    public bool HasNotes => NoteCount > 0;

    /// <summary>"Page 12, 2 notes, in this item, the page in view": read out, and the tooltip.</summary>
    public string AccessibleName => PageStrip.Describe(Page, NoteCount, InItem, IsCurrent);
}

/// <summary>
/// The reader's thumbnail strip: a small picture of every page of the open PDF, to scroll and click (or Enter) to go to
/// a page. The page in view stands out, pages with notes carry a badge, and in a binder the item's pages are marked.
/// Only the thumbnails near the strip's view are drawn (<see cref="Draw"/>), through <see cref="PageThumbnails"/>, so
/// a 600-page book draws a dozen; those that scroll away let go of their pictures and stop waiting to be drawn.
/// </summary>
public sealed partial class PageStripViewModel(PageThumbnails thumbnails, Action<int> goTo, ILogger log) : ObservableObject
{
    /// <summary>Thumbnails are this tall in the strip; their width follows the page.</summary>
    public const double ThumbnailHeight = 96;

    /// <summary>Pictures of pages this far beyond those drawn are let go, so a long strip doesn't hold every page.</summary>
    const int KeepAround = 40;

    readonly Dictionary<int, CancellationTokenSource> _drawing = [];
    PdfRenderer? _renderer;
    string? _contentHash;
    bool _keepOnDisk;
    int _version;
    int _current;
    (int First, int Last)? _item;

    /// <summary>Every page of the open PDF, in order; empty when nothing is open.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PageThumbnail> Pages { get; private set; } = [];

    /// <summary>The thumbnail of the page in view, which the strip keeps in sight.</summary>
    [ObservableProperty]
    public partial PageThumbnail? Current { get; private set; }

    /// <summary>
    /// Shows the pages of a newly opened PDF. <paramref name="keepOnDisk"/> is false for a book whose text was
    /// forgotten, whose thumbnails then stay in memory only.
    /// </summary>
    public void Show(PdfRenderer renderer, string contentHash, DocInfo doc, bool keepOnDisk, int currentPage)
    {
        Clear();
        _renderer = renderer;
        _contentHash = contentHash;
        _keepOnDisk = keepOnDisk;
        var pages = new PageThumbnail[doc.PageCount];
        for (var i = 0; i < pages.Length; i++)
        {
            var size = i < doc.PageSizes.Count ? doc.PageSizes[i] : new PageSize(612, 792);
            var aspect = size.Width > 0 && size.Height > 0 ? size.Width / size.Height : 612.0 / 792;
            pages[i] = new PageThumbnail(i, PageNumbers.Display(i, doc.PageLabels), Math.Clamp(ThumbnailHeight * aspect, 40, 180), ThumbnailHeight,
                PageThumbnailCache.ScaleFor(size.Width > 0 ? size.Width : 612, size.Height > 0 ? size.Height : 792))
            {
                Image = thumbnails.TryGet(contentHash, i),
            };
        }
        Pages = pages;
        _current = -1;
        SetCurrent(currentPage);
        MarkItem();
        if (keepOnDisk) thumbnails.Touch(contentHash);
    }

    /// <summary>Nothing is open: the pages go, and any still waiting to be drawn stop.</summary>
    public void Clear()
    {
        _version++;
        foreach (var cts in _drawing.Values) cts.Cancel();
        _drawing.Clear();
        _renderer = null;
        _contentHash = null;
        _item = null;
        Pages = [];
        Current = null;
    }

    /// <summary>The page in view changed.</summary>
    public void SetCurrent(int pageIndex)
    {
        if (pageIndex == _current && Current is not null) return;
        Current?.IsCurrent = false;
        _current = pageIndex;
        Current = pageIndex >= 0 && pageIndex < Pages.Count ? Pages[pageIndex] : null;
        Current?.IsCurrent = true;
    }

    /// <summary>
    /// The pages of the binder item in hand, marked in the strip, with every other page still there to go to; null for
    /// a whole book or outside a binder.
    /// </summary>
    public void SetItemPages(int? first, int? last)
    {
        _item = first is null || last is null ? null : (first.Value, last.Value);
        MarkItem();
    }

    void MarkItem()
    {
        var item = _item is { } pages ? PageStrip.ItemPages(pages.First, pages.Last, Pages.Count) : null;
        foreach (var page in Pages) page.InItem = item is { } range && page.Index >= range.First && page.Index <= range.Last;
    }

    /// <summary>The open book's page notes, each as its first and last page in this copy: the pages with notes get a badge.</summary>
    public void SetNotes(IEnumerable<(int First, int Last)> notes)
    {
        var counts = PageStrip.NoteCounts(Pages.Count, notes);
        foreach (var page in Pages) page.NoteCount = counts[page.Index];
    }

    /// <summary>
    /// The strip shows the thumbnails from <paramref name="firstInView"/> to <paramref name="lastInView"/>: those and a
    /// few either side are drawn, nearest first; any no longer near stop waiting, and pictures far away are let go.
    /// </summary>
    public void Draw(int firstInView, int lastInView)
    {
        if (_renderer is not { } renderer || _contentHash is not { } hash || Pages.Count == 0) return;
        var wanted = PageStrip.PagesToDraw(firstInView, lastInView, Pages.Count);
        var near = wanted.ToHashSet();
        foreach (var (index, cts) in _drawing.Where(d => !near.Contains(d.Key)).ToList())
        {
            cts.Cancel();
            _drawing.Remove(index);
        }
        var (keepFrom, keepTo) = (wanted.Min() - KeepAround, wanted.Max() + KeepAround);
        foreach (var page in Pages)
            if (page.Image is not null && (page.Index < keepFrom || page.Index > keepTo)) page.Image = null;
        foreach (var index in wanted)
        {
            var page = Pages[index];
            if (page.Image is not null || _drawing.ContainsKey(index)) continue;
            if (thumbnails.TryGet(hash, index) is { } known)
            {
                page.Image = known;
                continue;
            }
            var cts = new CancellationTokenSource();
            _drawing[index] = cts;
            _ = DrawAsync(page, hash, renderer, cts);
        }
    }

    async Task DrawAsync(PageThumbnail page, string hash, PdfRenderer renderer, CancellationTokenSource cts)
    {
        var version = _version;
        try
        {
            var image = await thumbnails.LoadAsync(hash, page.Index, page.Scale, renderer, _keepOnDisk, cts.Token);
            if (version == _version && image is not null) page.Image = image;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away, or the book closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogDebug(ex, "Drawing the thumbnail of page {Page} failed", page.Index);
        }
        finally
        {
            if (version == _version && _drawing.TryGetValue(page.Index, out var mine) && mine == cts) _drawing.Remove(page.Index);
            cts.Dispose();
        }
    }

    /// <summary>A click on a thumbnail, or Enter on it: the reader goes to its page.</summary>
    [RelayCommand]
    void GoToPage(PageThumbnail page) => goTo(page.Index);
}

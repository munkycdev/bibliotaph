using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Processing;
using Bibliotaph.Viewer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

public enum ViewerMode
{
    Empty,
    Opening,
    Pdf,
    Image,
    Problem,
}

/// <summary>An open PDF for the page surface: its renderer, what the worker said about it, and where to start.</summary>
public sealed record OpenPdf(PdfRenderer Renderer, DocInfo Doc, Func<int, Task<PageTextLayer>> Text, int PageIndex, double? PdfTop);

/// <summary>The marks on the pages; the one at <see cref="Current"/> stands out.</summary>
public sealed record HighlightSet(IReadOnlyList<PageHighlight> Items, int Current)
{
    public static HighlightSet None { get; } = new([], -1);
}

/// <summary>A place to scroll to: a page, and with <see cref="PdfTop"/> a spot on it (PDF points from the bottom).</summary>
public sealed record PageTarget(int PageIndex, double? PdfTop);

/// <summary>A bookmark in the book's outline, with the page it goes to as the reader would name it.</summary>
public sealed record OutlineEntry(string Title, int PageIndex, int Depth, string Page)
{
    public System.Windows.Thickness Indent => new(Depth * 14, 0, 0, 0);
}

/// <summary>
/// One open book. A PDF opens in the viewer worker at the page the search hit was on, with the search's words marked
/// and the first one in view; an image opens decoded at a capped size. Asks for a PDF's password and can remember
/// it. Leaving the page closes the book; coming Back reopens it where the reader was.
/// </summary>
public sealed partial class ViewerViewModel : PageViewModel
{
    const long ImageMaxPixels = 40_000_000;  // about 160 MB decoded; a bigger map is shrunk to fit
    const int MaxFindHits = 2000;
    const int MaxHitsPerTerm = 200;

    readonly ViewerRequest? _request;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly PdfWorkerPool _workers;
    readonly PasswordVault _vault;
    readonly IPasswordPrompt _prompt;
    readonly IndexingService _indexing;
    readonly JobBoard _jobs;
    readonly ISourceFileReader _files;
    readonly WpfImageCodec _codec;
    readonly ILogger<ViewerViewModel> _log;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _noticeTimer;

    DocumentSource? _source;
    PdfRenderer? _renderer;
    IReadOnlyList<string?> _labels = [];
    PageTarget? _resume;
    int _version;
    IReadOnlyList<PageHighlight> _termHighlights = [];
    IReadOnlyList<PageHighlight> _findHits = [];
    string _foundText = "";
    int _findIndex = -1;

    public ViewerViewModel(ViewerRequests requests, LibraryStore library, LibraryQueries queries, PdfWorkerPool workers, PasswordVault vault,
        IPasswordPrompt prompt, IndexingService indexing, JobBoard jobs, ISourceFileReader files, WpfImageCodec codec, ILogger<ViewerViewModel> log)
    {
        _request = requests.Take();
        _library = library;
        _queries = queries;
        _workers = workers;
        _vault = vault;
        _prompt = prompt;
        _indexing = indexing;
        _jobs = jobs;
        _files = files;
        _codec = codec;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Notice = null, _dispatcher);
        _noticeTimer.Stop();
    }

    public override Route Route => Route.Viewer;

    public override string Title => _request?.Title ?? "Reader";

    public override string Section => "Library";

    public override Route? SectionRoute => Route.Library;

    public override bool ScrollsItself => true;

    /// <summary>Raised when the view should scroll to a page, as for the next find result.</summary>
    public event EventHandler<PageTarget>? GoToRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPdf), nameof(IsImage), nameof(IsOpening), nameof(ShowEmptyState), nameof(ZoomChoices), nameof(OutlineVisible))]
    public partial ViewerMode Mode { get; private set; }

    public bool IsPdf => Mode == ViewerMode.Pdf;

    public bool IsImage => Mode == ViewerMode.Image;

    public bool IsOpening => Mode == ViewerMode.Opening;

    public bool ShowEmptyState => Mode is ViewerMode.Empty or ViewerMode.Problem;

    /// <summary>"PDF · 320 pages · Adventures / Winter", under the title.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    [ObservableProperty]
    public partial string EmptyTitle { get; private set; } = "Open a book from the Library.";

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = "Search for something, then open a matching page, or open a book from its details.";

    [ObservableProperty]
    public partial string? EmptyActionText { get; private set; }

    [ObservableProperty]
    public partial IRelayCommand? EmptyCommand { get; private set; }

    [ObservableProperty]
    public partial OpenPdf? Pdf { get; private set; }

    [ObservableProperty]
    public partial ImageSource? Image { get; private set; }

    [ObservableProperty]
    public partial HighlightSet Highlights { get; private set; } = HighlightSet.None;

    /// <summary>The PDF's bookmarks, for the Contents panel. Empty when the book has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutline), nameof(OutlineVisible))]
    public partial IReadOnlyList<OutlineEntry> Outline { get; private set; } = [];

    public bool HasOutline => Outline.Count > 0;

    /// <summary>Whether the reader wants the Contents panel; it stays hidden for a book without bookmarks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutlineVisible))]
    public partial bool ShowOutline { get; set; } = true;

    public bool OutlineVisible => ShowOutline && HasOutline && IsPdf;

    /// <summary>False when the PDF forbids copying its text; selection still works, Copy explains.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyNote))]
    public partial bool CanCopy { get; private set; } = true;

    public string CopyNote => CanCopy ? "" : "This PDF doesn't allow copying its text.";

    /// <summary>
    /// <see cref="PdfPagesView.FitWidth"/> or <see cref="PdfPagesView.FitPage"/> (an image fits the window for
    /// either); otherwise 1 is 100%.
    /// </summary>
    [ObservableProperty]
    public partial double Zoom { get; set; }

    public IReadOnlyList<Choice<double>> ZoomChoices =>
    [
        .. IsImage
            ? new Choice<double>[] { new(PdfPagesView.FitWidth, "Fit to window") }
            : [new(PdfPagesView.FitWidth, "Fit width"), new(PdfPagesView.FitPage, "Fit page")],
        .. ZoomSteps.All.Select(z => new Choice<double>(z, z.ToString("P0", CultureInfo.CurrentCulture))),
    ];

    /// <summary>Set by the view as the reader scrolls.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCountText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageCount { get; private set; }

    public string PageCountText => $"of {PageCount.ToString("N0", CultureInfo.CurrentCulture)}";

    /// <summary>The page box: the printed label of the page in view, or what the reader is typing.</summary>
    [ObservableProperty]
    public partial string PageEntry { get; set; } = "";

    [ObservableProperty]
    public partial string FindText { get; set; } = "";

    /// <summary>"3 of 41", "No matches".</summary>
    [ObservableProperty]
    public partial string FindStatus { get; private set; } = "";

    /// <summary>A short message that clears itself, such as why Copy did nothing.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    partial void OnCurrentPageIndexChanged(int value) => PageEntry = PageNumbers.Display(value, _labels);

    public override async Task LoadAsync()
    {
        if (_request is not { } request)
        {
            Mode = ViewerMode.Empty;
            return;
        }
        var version = ++_version;
        Mode = ViewerMode.Opening;
        try
        {
            _source = await _library.GetSourceAsync(request.DocumentId);
            if (version != _version) return;
            if (_source is null)
            {
                Problem("This book can't be reached right now.",
                    "Its folder is offline or the file has moved. Bibliotaph finds it again when the folder is back.");
                return;
            }
            Subtitle = DescribeSource(_source, null);
            _ = PrioritizeAsync(request.DocumentId);
            if (SourceFormats.IsImage(_source.Format)) await OpenImageAsync(_source, version);
            else await OpenPdfAsync(_source, request, version);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The file went away, is locked, or is still downloading: worth saying, not worth an error in the log.
            _log.LogWarning("Reading document {DocumentId} failed: {Reason}", request.DocumentId, ex.Message);
            if (version == _version) Problem("The file could not be read.", "It may have moved, be in use, or still be downloading.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Opening document {DocumentId} failed", request.DocumentId);
            if (version == _version) Problem("This book could not be opened.", ex.Message);
        }
    }

    /// <summary>Closes the book, remembering the page in view so Back reopens it there.</summary>
    public override void Unload()
    {
        _version++;
        if (Mode == ViewerMode.Pdf) _resume = new PageTarget(CurrentPageIndex, null);
        _noticeTimer.Stop();
        Pdf = null;
        Image = null;
        _findHits = [];
        _findIndex = -1;
        _foundText = "";
        FindStatus = "";
        Highlights = HighlightSet.None;
        if (_renderer is { } renderer)
        {
            _renderer = null;
            _ = CloseAsync(renderer);
        }
    }

    async Task OpenImageAsync(DocumentSource source, int version)
    {
        var image = await Task.Run(() =>
        {
            using var stream = _files.OpenRead(source.FullPath);
            return _codec.DecodeForViewing(stream, ImageMaxPixels);
        });
        if (version != _version) return;
        if (image is null)
        {
            Problem("This image could not be read.", "The file may be damaged, or still downloading.");
            return;
        }
        Image = image;
        Subtitle = DescribeSource(source, $"{source.Format.ToUpperInvariant()} image");
        Mode = ViewerMode.Image;
    }

    async Task OpenPdfAsync(DocumentSource source, ViewerRequest request, int version)
    {
        var renderer = new PdfRenderer(_workers[WorkerSlot.Viewer], _dispatcher);
        try
        {
            var (doc, password, remember) = await OpenWithPasswordAsync(renderer, source, version);
            if (doc is null || version != _version)
            {
                await renderer.DisposeAsync();
                return;
            }
            if (remember && password is not null)
            {
                // A remembered password also lets indexing read the book, so its blocked stages run again.
                if (_vault.Remember(source.ContentHash, password)) await _indexing.RetryAsync(source.DocumentId);
                else ShowNotice("Windows didn't store the password, so Bibliotaph will ask for it next time.");
            }

            _renderer = renderer;
            _labels = doc.PageLabels;
            Outline = [.. doc.Outline
                .Where(o => o.PageIndex >= 0 && o.PageIndex < doc.PageCount && !string.IsNullOrWhiteSpace(o.Title))
                .Select(o => new OutlineEntry(o.Title.Trim(), o.PageIndex, Math.Min(o.Depth, 5), PageNumbers.Display(o.PageIndex, doc.PageLabels)))];
            PageCount = doc.PageCount;
            CanCopy = doc.CanCopy;
            Subtitle = DescribeSource(source, $"PDF · {doc.PageCount.ToString("N0", CultureInfo.CurrentCulture)} {(doc.PageCount == 1 ? "page" : "pages")}");

            var start = Math.Clamp(_resume?.PageIndex ?? request.PageIndex, 0, Math.Max(0, doc.PageCount - 1));
            _termHighlights = await TermHighlightsAsync(renderer, source.DocumentId, request, start);
            if (version != _version) return;
            // From a search hit, the first marked word is brought into view; coming Back, the page as it was left.
            var top = _resume is null && _termHighlights is [{ Rects: [var first, ..] }, ..] ? first.Top : (double?)null;
            CurrentPageIndex = start;
            OnCurrentPageIndexChanged(start);
            Pdf = new OpenPdf(renderer, doc, TextAsync, start, top);
            Highlights = new HighlightSet(_termHighlights, _termHighlights.Count > 0 && _resume is null ? 0 : -1);
            Mode = ViewerMode.Pdf;
        }
        catch
        {
            if (!ReferenceEquals(_renderer, renderer)) await renderer.DisposeAsync();
            throw;
        }
    }

    /// <summary>Opens the PDF, trying a remembered password first and then asking. A null document means it didn't open.</summary>
    async Task<(DocInfo? Doc, string? Password, bool Remember)> OpenWithPasswordAsync(PdfRenderer renderer, DocumentSource source, int version)
    {
        var password = _vault.Find(source.ContentHash);
        var remembered = password is not null;
        var remember = false;
        var asked = 0;
        while (true)
        {
            try
            {
                return (await renderer.OpenAsync(source.FullPath, password), password, remember);
            }
            catch (PdfOpenException ex) when (ex.Kind == ErrorKind.Password)
            {
                if (version != _version) return (null, null, false);
                if (remembered)
                {
                    // The book changed its password, or a different file now has this content hash's name.
                    _vault.Forget(source.ContentHash);
                    remembered = false;
                }
                var entered = _prompt.Ask(Title, retry: asked++ > 0);
                if (entered is null)
                {
                    Problem("This PDF needs a password.", "Enter its password to read it. Bibliotaph can remember it, so indexing can read the book too.",
                        "Enter password");
                    return (null, null, false);
                }
                password = entered.Password;
                remember = entered.Remember;
            }
            catch (PdfOpenException ex)
            {
                var (title, message) = OpenFailure(ex);
                Problem(title, message);
                return (null, null, false);
            }
        }
    }

    static (string Title, string Message) OpenFailure(PdfOpenException ex) => ex.Kind switch
    {
        ErrorKind.Format => ("This file isn't a readable PDF.", "It may be damaged, or only partly downloaded."),
        ErrorKind.Security => ("This PDF uses a protection scheme Bibliotaph can't open.", "Another PDF reader may be able to open it."),
        ErrorKind.File => ("The file could not be read.", "It may be in use, or still downloading."),
        _ => ("This book could not be opened.", ex.Message),
    };

    /// <summary>
    /// The search's words on the page the hit was on, top of the page first. PDF text is searched by the worker; a
    /// scanned page's words come from OCR.
    /// </summary>
    async Task<IReadOnlyList<PageHighlight>> TermHighlightsAsync(PdfRenderer renderer, long documentId, ViewerRequest request, int page)
    {
        if (request.Query is not { } query) return [];
        var terms = query.HighlightTerms();
        if (terms.Count == 0) return [];

        var found = new List<PageHighlight>();
        foreach (var term in terms)
            foreach (var hit in await renderer.FindAsync(term, page, MaxHitsPerTerm))
                found.Add(new PageHighlight(page, [.. hit.Rects.Select(PdfRenderer.ToPageRect)]));
        if (found.Count == 0)
        {
            var words = await Task.Run(() => _queries.GetOcrWordsAsync(documentId, page));
            var parts = terms.SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            found.AddRange(words
                .Where(w => parts.Any(p => w.Text.Contains(p, StringComparison.CurrentCultureIgnoreCase)))
                .Select(w => new PageHighlight(page, [new PageRect(w.Left, w.Top, w.Right, w.Bottom)])));
        }
        return [.. found.Where(h => h.Rects.Count > 0).OrderByDescending(h => h.Rects[0].Top).ThenBy(h => h.Rects[0].Left)];
    }

    /// <summary>A page's selectable text: the PDF's own, or the words OCR read from a scanned page.</summary>
    async Task<PageTextLayer> TextAsync(int pageIndex)
    {
        if (_renderer is not { } renderer || _source is not { } source) return PageTextLayer.Empty;
        var layer = await renderer.GetTextAsync(pageIndex);
        if (layer.Text.Any(char.IsLetterOrDigit)) return layer;
        var words = await Task.Run(() => _queries.GetOcrWordsAsync(source.DocumentId, pageIndex));
        return words.Count == 0 ? layer : PageTextLayer.FromWords(words.Select(w => (w.Text, new PageRect(w.Left, w.Top, w.Right, w.Bottom))));
    }

    /// <summary>Goes to a bookmark's page (click or Enter in the Contents panel).</summary>
    [RelayCommand]
    void OpenOutlineEntry(OutlineEntry entry) => GoTo(entry.PageIndex, null);

    [RelayCommand]
    void ToggleOutline() => ShowOutline = !ShowOutline;

    /// <summary>Back to the starting zoom, fitted to the width (Ctrl+0).</summary>
    [RelayCommand]
    void ResetZoom() => Zoom = PdfPagesView.FitWidth;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    void PreviousPage() => GoTo(CurrentPageIndex - 1, null);

    bool CanGoPrevious() => CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    void NextPage() => GoTo(CurrentPageIndex + 1, null);

    bool CanGoNext() => CurrentPageIndex < PageCount - 1;

    /// <summary>Goes to the page typed in the page box: a printed label, or a PDF page number.</summary>
    [RelayCommand]
    void GoToPage()
    {
        if (PageNumbers.Find(PageEntry, _labels, PageCount) is { } index) GoTo(index, null);
        else
        {
            ShowNotice($"There's no page “{PageEntry.Trim()}” in this book.");
            PageEntry = PageNumbers.Display(CurrentPageIndex, _labels);
        }
    }

    /// <summary>Finds text in the whole book (Enter in the find box). Enter again goes to the next match.</summary>
    [RelayCommand]
    async Task Find()
    {
        var text = FindText.Trim();
        if (text.Length == 0 || _renderer is not { } renderer)
        {
            ClearFind();
            return;
        }
        if (text == _foundText && _findHits.Count > 0)
        {
            FindNext();
            return;
        }
        FindStatus = "Searching…";
        var version = _version;
        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await renderer.FindAsync(text, -1, MaxFindHits);
        }
        catch (Exception ex) when (ex is WorkerException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Find in document {DocumentId} failed", _source?.DocumentId);
            FindStatus = "Find didn't finish";
            return;
        }
        if (version != _version) return;
        _foundText = text;
        _findHits = [.. hits.Where(h => h.Rects.Count > 0).Select(h => new PageHighlight(h.PageIndex, [.. h.Rects.Select(PdfRenderer.ToPageRect)]))];
        if (_findHits.Count == 0)
        {
            _findIndex = -1;
            FindStatus = "No matches";
            Highlights = new HighlightSet(_termHighlights, -1);
            return;
        }
        // The first match from the page in view onwards, wrapping round to the start.
        var next = 0;
        for (var i = 0; i < _findHits.Count; i++)
        {
            if (_findHits[i].PageIndex < CurrentPageIndex) continue;
            next = i;
            break;
        }
        ShowFindHit(next);
    }

    [RelayCommand]
    void FindNext()
    {
        if (_findHits.Count > 0) ShowFindHit((_findIndex + 1) % _findHits.Count);
    }

    [RelayCommand]
    void FindPrevious()
    {
        if (_findHits.Count > 0) ShowFindHit((_findIndex - 1 + _findHits.Count) % _findHits.Count);
    }

    /// <summary>Clears the find box and its marks, leaving the search's own marks.</summary>
    [RelayCommand]
    void ClearFind()
    {
        FindText = "";
        FindStatus = "";
        _foundText = "";
        _findHits = [];
        _findIndex = -1;
        Highlights = new HighlightSet(_termHighlights, -1);
    }

    void ShowFindHit(int index)
    {
        _findIndex = index;
        var hit = _findHits[index];
        Highlights = new HighlightSet(_findHits, index);
        var count = _findHits.Count.ToString("N0", CultureInfo.CurrentCulture);
        FindStatus = $"{(index + 1).ToString("N0", CultureInfo.CurrentCulture)} of {count}{(_findHits.Count >= MaxFindHits ? "+" : "")}";
        GoTo(hit.PageIndex, hit.Rects[0].Top);
    }

    void GoTo(int index, double? pdfTop)
    {
        if (index < 0 || index >= PageCount) return;
        GoToRequested?.Invoke(this, new PageTarget(index, pdfTop));
    }

    /// <summary>Tries again after a problem: the folder is back, or the reader will now enter the password.</summary>
    [RelayCommand]
    Task Retry() => LoadAsync();

    public void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    void Problem(string title, string message, string action = "Try again")
    {
        EmptyTitle = title;
        EmptyMessage = message;
        EmptyActionText = action;
        EmptyCommand = RetryCommand;
        Mode = ViewerMode.Problem;
    }

    static string DescribeSource(DocumentSource source, string? kind) =>
        string.Join(" · ", new[] { kind, source.FolderHint }.Where(s => !string.IsNullOrEmpty(s)));

    async Task PrioritizeAsync(long documentId)
    {
        try
        {
            // An open book's waiting stages (text, OCR) run next, so its search catches up while it's being read.
            await _jobs.PrioritizeAsync(documentId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Prioritizing document {DocumentId} failed", documentId);
        }
    }

    async Task CloseAsync(PdfRenderer renderer)
    {
        try
        {
            await renderer.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Closing a document in the viewer failed");
        }
    }
}

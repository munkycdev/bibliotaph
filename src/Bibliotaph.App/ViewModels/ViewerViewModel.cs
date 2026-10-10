using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
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

/// <summary>What every reader needs, in the main window or a pop-out. One for the app; <see cref="ReaderWindows"/> makes the readers.</summary>
public sealed record ViewerServices(LibraryStore Library, LibraryQueries Queries, PdfWorkerPool Workers, PasswordVault Vault,
    UnlockedPasswords Unlocked, IPasswordPrompt Prompt, IndexingService Indexing, JobBoard Jobs, ISourceFileReader Files, WpfImageCodec Codec,
    SourceFiles Sources, FavoritesService Favorites, ReadingService Reading, SessionsService Sessions, Func<SessionActions> SessionActions,
    ILogger<ViewerViewModel> Log);

/// <summary>
/// One open book. A PDF opens in the viewer worker at the page the search hit was on, with the search's words marked
/// and the first one in view; an image opens decoded at a capped size. Asks for a PDF's password and can remember
/// it. Leaving the page closes the book; coming Back reopens it where the reader was. The same reader runs in the
/// main window and in a pop-out window; it is given its request when it's made.
/// </summary>
public sealed partial class ViewerViewModel : PageViewModel
{
    const long ImageMaxPixels = 40_000_000;  // about 160 MB decoded; a bigger map is shrunk to fit
    const int MaxFindHits = 2000;
    const int MaxHitsPerTerm = 200;

    ViewerRequest? _request;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly PdfWorkerPool _workers;
    readonly PasswordVault _vault;
    readonly UnlockedPasswords _unlocked;
    readonly IPasswordPrompt _prompt;
    readonly IndexingService _indexing;
    readonly JobBoard _jobs;
    readonly ISourceFileReader _files;
    readonly SourceFiles _sources;
    readonly WpfImageCodec _codec;
    readonly FavoritesService _favorites;
    readonly ReadingService _reading;
    readonly SessionsService _sessions;
    readonly ILogger<ViewerViewModel> _log;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _noticeTimer;
    readonly DispatcherTimer _placeTimer;
    readonly ReaderWindows _windows;

    DocumentSource? _source;
    ViewerLease? _lease;
    Task _closing = Task.CompletedTask;
    bool _windowClosed;
    PdfRenderer? _renderer;
    /// <summary>The open PDF's file, held so a file extracted from a ZIP stays while the book is open.</summary>
    LocalFile? _file;
    IReadOnlyList<string?> _labels = [];
    PageTarget? _resume;
    int _version;
    IReadOnlyList<PageHighlight> _termHighlights = [];
    IReadOnlyList<PageHighlight> _findHits = [];
    string _foundText = "";
    int _findIndex = -1;

    public ViewerViewModel(ViewerRequest? request, ViewerServices services, ReaderWindows windows, bool poppedOut = false)
    {
        _request = request;
        _library = services.Library;
        _queries = services.Queries;
        _workers = services.Workers;
        _vault = services.Vault;
        _unlocked = services.Unlocked;
        _prompt = services.Prompt;
        _indexing = services.Indexing;
        _jobs = services.Jobs;
        _files = services.Files;
        _sources = services.Sources;
        _codec = services.Codec;
        _favorites = services.Favorites;
        _reading = services.Reading;
        _sessions = services.Sessions;
        Sessions = services.SessionActions();
        _log = services.Log;
        _windows = windows;
        IsPoppedOut = poppedOut;
        Zoom = request?.Zoom ?? PdfPagesView.FitWidth;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _noticeTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Notice = null, _dispatcher);
        _noticeTimer.Stop();
        // The page is kept once reading settles on it, so it survives the app closing with the book open.
        _placeTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _placeTimer!.Stop();
            if (Mode == ViewerMode.Pdf) KeepPlace(CurrentPageIndex);
        }, _dispatcher);
        _placeTimer.Stop();
    }

    /// <summary>True in a pop-out window, false in the main window's Reading route.</summary>
    public bool IsPoppedOut { get; }

    /// <summary>True for the reader inside run mode (slice 3d), which frames it itself.</summary>
    public bool InRunMode { get; init; }

    /// <summary>No margins of its own: a pop-out's window or run mode sets them.</summary>
    public bool FitsFrame => IsPoppedOut || InRunMode;

    /// <summary>
    /// Shows another book or another place in this one, as run mode does going from item to item (slice 3 plan, choice
    /// 16). Pages of the PDF already open are only scrolled to, so the next item in the same book shows at once.
    /// </summary>
    public async Task ShowAsync(ViewerRequest request)
    {
        if (Mode == ViewerMode.Pdf && _renderer is not null && _request is { Pack: null } open && open.DocumentId == request.DocumentId && request.Pack is null)
        {
            _request = request with { Zoom = Zoom };
            OnPropertyChanged(nameof(Title));
            Sessions.Close();
            ShowSessionBanner();
            GoTo(Math.Clamp(request.PageIndex, 0, Math.Max(0, PageCount - 1)), null);
            return;
        }
        Unload();
        _resume = null;
        _request = request;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsInPack));
        OnPropertyChanged(nameof(PackPosition));
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        await LoadAsync();
    }

    /// <summary>Shows why nothing can open here, as for a run mode item whose file can't be reached, with no Try again.</summary>
    public void ShowUnavailable(string title, string message)
    {
        Unload();
        _resume = null;
        _request = null;
        OnPropertyChanged(nameof(Title));
        Subtitle = "";
        SessionBanner = null;
        CanUsePage = false;
        EmptyTitle = title;
        EmptyMessage = message;
        EmptyActionText = null;
        EmptyCommand = null;
        Mode = ViewerMode.Problem;
    }

    public override Route Route => Route.Viewer;

    public override string Title => _request?.Title ?? "Reader";

    /// <summary>True for an image opened from a pack: Previous and Next step through its images.</summary>
    public bool IsInPack => _request?.Pack is { Count: > 0 };

    int PackIndex => _request?.Pack is { } pack ? pack.ToList().FindIndex(p => p.DocumentId == _request.DocumentId) : -1;

    /// <summary>"12 of 120", for an image in a pack.</summary>
    public string PackPosition => _request?.Pack is { } pack
        ? $"{(PackIndex + 1).ToString("N0", CultureInfo.CurrentCulture)} of {pack.Count.ToString("N0", CultureInfo.CurrentCulture)}"
        : "";

    /// <summary>The previous image in the pack (Left arrow).</summary>
    [RelayCommand(CanExecute = nameof(CanGoToPreviousImage))]
    Task PreviousImage() => StepAsync(-1);

    bool CanGoToPreviousImage() => IsInPack && PackIndex > 0;

    /// <summary>The next image in the pack (Right arrow).</summary>
    [RelayCommand(CanExecute = nameof(CanGoToNextImage))]
    Task NextImage() => StepAsync(1);

    bool CanGoToNextImage() => _request?.Pack is { } pack && PackIndex >= 0 && PackIndex < pack.Count - 1;

    /// <summary>Shows the image <paramref name="by"/> places on in the pack, at the same zoom.</summary>
    async Task StepAsync(int by)
    {
        if (_request?.Pack is not { } pack) return;
        var next = PackIndex + by;
        if (next < 0 || next >= pack.Count) return;
        _request = _request with { DocumentId = pack[next].DocumentId, Title = pack[next].Title, PageIndex = 0, SessionItem = null };
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PackPosition));
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        await LoadAsync();
    }

    public override string Section => "Library";

    public override Route? SectionRoute => Route.Library;

    public override bool ScrollsItself => true;

    /// <summary>Raised when the view should scroll to a page, as for the next find result.</summary>
    public event EventHandler<PageTarget>? GoToRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPdf), nameof(IsImage), nameof(IsOpening), nameof(ShowEmptyState), nameof(ZoomChoices), nameof(OutlineVisible))]
    [NotifyCanExecuteChangedFor(nameof(PopOutCommand), nameof(AddPageCommand))]
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

    partial void OnCurrentPageIndexChanged(int value)
    {
        PageEntry = PageNumbers.Display(value, _labels);
        if (Mode == ViewerMode.Pdf && KeepsPlace)
        {
            _placeTimer.Stop();
            _placeTimer.Start();
        }
    }

    public override async Task LoadAsync()
    {
        if (_request is not { } request)
        {
            Mode = ViewerMode.Empty;
            return;
        }
        if (_windowClosed) return;
        var version = ++_version;
        // Stepping through a pack keeps the image in view until the next one is ready.
        if (!(IsInPack && Mode == ViewerMode.Image)) Mode = ViewerMode.Opening;
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
        if (Mode == ViewerMode.Pdf)
        {
            _resume = new PageTarget(CurrentPageIndex, null);
            KeepPlace(CurrentPageIndex);
        }
        _placeTimer.Stop();
        _noticeTimer.Stop();
        Sessions.Close();
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
            _closing = CloseAsync(renderer, _file);
            _file = null;
        }
    }

    /// <summary>
    /// The pop-out window is closing: closes the book, and once it's closed gives the window's viewer worker back,
    /// which stops the worker if no other window is using it.
    /// </summary>
    public async Task CloseWindowAsync()
    {
        // A load still running stops at its next step rather than leasing a worker nobody would give back.
        _windowClosed = true;
        Unload();
        await _closing;
        if (_lease is { } lease)
        {
            _lease = null;
            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// The main window's readers share one viewer worker; a pop-out leases one of its own when it first shows a PDF
    /// (an image needs none) and gives it back when it closes.
    /// </summary>
    WorkerClient Worker() => IsPoppedOut ? (_lease ??= _workers.LeaseViewer()).Worker : _windows.MainWorker;

    /// <summary>A request that opens this book again as it is now: at the page in view and the same zoom.</summary>
    public ViewerRequest? Here() =>
        _request is null ? null : _request with
        {
            PageIndex = Mode == ViewerMode.Pdf ? CurrentPageIndex : _request.PageIndex,
            Query = null,
            Zoom = Zoom,
            Resume = false,
            KeepsPlace = KeepsPlace,
        };

    /// <summary>True for an ordinary read, whose page is kept for next time (slice 3 plan, choice 6).</summary>
    bool KeepsPlace => _request is { Resume: true } or { KeepsPlace: true };

    /// <summary>Keeps the page an ordinary read is left at, so the book opens there next time.</summary>
    void KeepPlace(int pageIndex)
    {
        if (KeepsPlace && _request is { } request) _ = KeepPlaceAsync(request.DocumentId, pageIndex);
    }

    async Task KeepPlaceAsync(long documentId, int pageIndex)
    {
        try
        {
            await _reading.SavePositionAsync(documentId, pageIndex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Keeping the page of document {DocumentId} failed", documentId);
        }
    }

    /// <summary>Records the open for Home and Recently opened, and shows whether the book is a favorite.</summary>
    async Task RecordOpenAsync(long documentId)
    {
        try
        {
            _card = await _reading.RecordOpenAsync(documentId);
            IsFavorite = _card is { } card && await _favorites.IsFavoriteAsync(card);
            ToggleFavoriteCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recording the open of document {DocumentId} failed", documentId);
        }
    }

    /// <summary>The card the open book shows under in the Library: its own, or its pack's.</summary>
    EntryId? _card;

    /// <summary>The book is marked with a heart (slice 3 plan, choice 5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteAction))]
    public partial bool IsFavorite { get; private set; }

    public string FavoriteAction => IsFavorite ? "Remove from favorites" : "Add to favorites";

    /// <summary>The heart in the toolbar: marks the book, or the pack an image is in, as a favorite or not.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleFavorite))]
    async Task ToggleFavorite()
    {
        if (_card is not { } card) return;
        var favorite = !IsFavorite;
        IsFavorite = favorite;
        try
        {
            await _favorites.SetAsync([card], favorite);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Marking entry {EntryId} as a favorite failed", card);
            IsFavorite = !favorite;
            ShowNotice("The heart couldn't be saved.");
        }
    }

    bool CanToggleFavorite() => _card is not null;

    /// <summary>Moves the book to a window of its own at the same page and zoom; the main window goes Back.</summary>
    [RelayCommand(CanExecute = nameof(CanPopOut))]
    Task PopOut() => _windows.PopOutAsync(this);

    bool CanPopOut() => !IsPoppedOut && Mode is ViewerMode.Pdf or ViewerMode.Image;

    /// <summary>Moves a pop-out's book back to the main window, at the same page and zoom, and closes the pop-out.</summary>
    [RelayCommand]
    void ReturnToMainWindow() => _windows.ReturnToMainWindow(this);

    /// <summary>Add page, Add pages… and Use this page, with their note and Undo, and the dialog over the reader.</summary>
    public SessionActions Sessions { get; }

    /// <summary>
    /// Add page (slice 3 plan, choice 13): the page in view, or the whole image, to the current session pack; before
    /// there is one, it asks for one first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddPage))]
    void AddPage() => AddPageTo(null);

    /// <summary>One of the Add page menu's Add to session items: the page in view to that pack.</summary>
    [RelayCommand]
    void AddPageToSession(SessionRequest request) => AddPageTo(request.Choice);

    void AddPageTo(SessionMenuChoice? choice)
    {
        if (_request is not { } request) return;
        if (Mode == ViewerMode.Image) Sessions.AddPage(request.DocumentId, 0, request.Title, choice, wholeFile: true);
        else if (Mode == ViewerMode.Pdf) Sessions.AddPage(request.DocumentId, CurrentPageIndex, $"page {PageNumbers.Display(CurrentPageIndex, _labels)}", choice);
    }

    bool CanAddPage() => Mode is ViewerMode.Pdf or ViewerMode.Image;

    /// <summary>
    /// Add pages… (choice 13): From and To start at the page with selected text on it, or the page in view, and are
    /// typed as the book numbers its pages.
    /// </summary>
    [RelayCommand]
    async Task AddPages(int? selectionPage)
    {
        if (_request is not { } request) return;
        if (Mode == ViewerMode.Image)
        {
            AddPageTo(null);
            return;
        }
        if (Mode != ViewerMode.Pdf) return;
        var page = Math.Clamp(selectionPage ?? CurrentPageIndex, 0, Math.Max(0, PageCount - 1));
        var shown = PageNumbers.Display(page, _labels);
        var labels = _labels;
        var count = PageCount;
        await Sessions.AddPagesAsync(request.DocumentId, shown, shown, page, text => PageNumbers.Find(text, labels, count));
    }

    /// <summary>
    /// Opened from a session pack's item whose pages weren't where they were (choice 14): why, over the pages. For a
    /// page that changed, Use this page points the item at the page in view.
    /// </summary>
    [ObservableProperty]
    public partial string? SessionBanner { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UsePageCommand))]
    public partial bool CanUsePage { get; private set; }

    void ShowSessionBanner()
    {
        if (_request?.SessionItem is not { } item || item.State is not (SessionItemState.Changed or SessionItemState.Original))
        {
            SessionBanner = null;
            CanUsePage = false;
            return;
        }
        SessionBanner = item.State == SessionItemState.Changed
            ? $"This page changed since it was added to {item.PackTitle}. If this isn't the right page, go to it, then choose Use this page."
            : item.Reason;
        CanUsePage = item.State == SessionItemState.Changed && Mode == ViewerMode.Pdf;
    }

    /// <summary>Points the session item at the page in view, keeping how many pages it spans.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePage))]
    async Task UsePage()
    {
        if (_request is not { SessionItem: { } item } request) return;
        var span = item.LastPage - item.FirstPage;
        var first = CurrentPageIndex;
        var last = Math.Min(first + span, Math.Max(0, PageCount - 1));
        try
        {
            await _sessions.RepointAsync(item.ItemId, request.DocumentId, first, last);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pointing session item {ItemId} at a page failed", item.ItemId);
            Sessions.Say("That didn't work. The log has the details.");
            return;
        }
        _request = request with { SessionItem = item with { State = SessionItemState.Ready, Reason = null, FirstPage = first, LastPage = last } };
        CanUsePage = false;
        SessionBanner = null;
        Sessions.Say($"Saved. In {item.PackTitle}, this item now opens at page {PageNumbers.Display(first, _labels)}.");
    }

    [RelayCommand]
    void DismissSessionBanner()
    {
        SessionBanner = null;
        CanUsePage = false;
    }

    /// <summary>Hides the note from Add page, and closes the dialog if it is open.</summary>
    [RelayCommand]
    void DismissSessionMessage() => Sessions.Close();

    async Task OpenImageAsync(DocumentSource source, int version)
    {
        await using var file = await _sources.OpenAsync(source);
        var image = await Task.Run(() =>
        {
            using var stream = _files.OpenRead(file.Path);
            return _codec.DecodeForViewing(stream, ImageMaxPixels);
        });
        if (version != _version) return;
        if (image is null)
        {
            Problem("This image could not be read.", "The file may be damaged, or still downloading.");
            return;
        }
        Image = image;
        Subtitle = DescribeSource(source, _request?.PackTitle is { } pack ? $"{source.Format.ToUpperInvariant()} image in {pack}" : $"{source.Format.ToUpperInvariant()} image");
        Mode = ViewerMode.Image;
        ShowSessionBanner();
        await RecordOpenAsync(source.DocumentId);
    }

    async Task OpenPdfAsync(DocumentSource source, ViewerRequest request, int version)
    {
        var file = await _sources.OpenAsync(source);
        var renderer = new PdfRenderer(Worker(), _dispatcher);
        try
        {
            var (doc, password, remember) = await OpenWithPasswordAsync(renderer, source, file.Path, version);
            if (doc is null || version != _version)
            {
                await renderer.DisposeAsync();
                await file.DisposeAsync();
                return;
            }
            // Kept in memory for this sitting, so the book moving to another window isn't asked for again.
            if (password is not null) _unlocked.Add(source.ContentHash, password);
            if (remember && password is not null)
            {
                // A remembered password also lets indexing read the book, so its blocked stages run again.
                if (_vault.Remember(source.ContentHash, password)) await _indexing.RetryAsync(source.DocumentId);
                else ShowNotice("Windows didn't store the password, so Bibliotaph will ask for it next time.");
            }

            _renderer = renderer;
            _file = file;
            _labels = doc.PageLabels;
            Outline = [.. doc.Outline
                .Where(o => o.PageIndex >= 0 && o.PageIndex < doc.PageCount && !string.IsNullOrWhiteSpace(o.Title))
                .Select(o => new OutlineEntry(o.Title.Trim(), o.PageIndex, Math.Min(o.Depth, 5), PageNumbers.Display(o.PageIndex, doc.PageLabels)))];
            PageCount = doc.PageCount;
            CanCopy = doc.CanCopy;
            Subtitle = DescribeSource(source, $"PDF · {doc.PageCount.ToString("N0", CultureInfo.CurrentCulture)} {(doc.PageCount == 1 ? "page" : "pages")}");

            // An ordinary open goes back to the page the book was left at (choice 6); Back, to the page as it was.
            var kept = _resume is null && request.Resume && request.PageIndex == 0 ? await _reading.GetPositionAsync(source.DocumentId) : 0;
            if (version != _version) return;
            var start = Math.Clamp(_resume?.PageIndex ?? (kept > 0 ? kept : request.PageIndex), 0, Math.Max(0, doc.PageCount - 1));
            _termHighlights = await TermHighlightsAsync(renderer, source.DocumentId, request, start);
            if (version != _version) return;
            // From a search hit, the first marked word is brought into view; coming Back, the page as it was left.
            var top = _resume is null && _termHighlights is [{ Rects: [var first, ..] }, ..] ? first.Top : (double?)null;
            CurrentPageIndex = start;
            OnCurrentPageIndexChanged(start);
            Pdf = new OpenPdf(renderer, doc, TextAsync, start, top);
            Highlights = new HighlightSet(_termHighlights, _termHighlights.Count > 0 && _resume is null ? 0 : -1);
            Mode = ViewerMode.Pdf;
            ShowSessionBanner();
            if (start > 0 && start == kept) ShowNotice($"Back at page {PageNumbers.Display(start, _labels)}, where you left off.");
            await RecordOpenAsync(source.DocumentId);
        }
        catch
        {
            if (!ReferenceEquals(_renderer, renderer))
            {
                await renderer.DisposeAsync();
                await file.DisposeAsync();
            }
            throw;
        }
    }

    /// <summary>
    /// Opens the PDF, trying the password that opened it earlier in this sitting or a remembered one first, and then
    /// asking. A null document means it didn't open.
    /// </summary>
    async Task<(DocInfo? Doc, string? Password, bool Remember)> OpenWithPasswordAsync(PdfRenderer renderer, DocumentSource source, string path, int version)
    {
        var unlocked = _unlocked.Find(source.ContentHash);
        var password = unlocked ?? _vault.Find(source.ContentHash);
        var remembered = unlocked is null && password is not null;
        var remember = false;
        var asked = 0;
        while (true)
        {
            try
            {
                return (await renderer.OpenAsync(path, password), password, remember);
            }
            catch (PdfOpenException ex) when (ex.Kind == ErrorKind.Password)
            {
                if (version != _version) return (null, null, false);
                if (unlocked is not null)
                {
                    _unlocked.Forget(source.ContentHash);
                    unlocked = null;
                }
                else if (remembered)
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

    async Task CloseAsync(PdfRenderer renderer, LocalFile? file)
    {
        try
        {
            await renderer.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Closing a document in the viewer failed");
        }
        finally
        {
            file?.Dispose();
        }
    }
}

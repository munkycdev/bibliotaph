using System.Windows.Threading;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// What indexing is doing, in words, for the sidebar and the Library folders page. The indexing service reports
/// changes from background threads, often; this coalesces them into at most one refresh per tick on the UI thread.
/// </summary>
public sealed partial class LibraryActivity : ObservableObject
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(750);

    readonly IndexingService _indexing;
    readonly IndexQueries _queries;
    readonly LibraryStore _library;
    readonly ILogger<LibraryActivity> _log;
    readonly DispatcherTimer _timer;
    int _dirty = 1;
    bool _refreshing;

    public LibraryActivity(IndexingService indexing, IndexQueries queries, LibraryStore library, ILogger<LibraryActivity> log)
    {
        _indexing = indexing;
        _queries = queries;
        _library = library;
        _log = log;
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, async (_, _) => await OnTickAsync(), Dispatcher.CurrentDispatcher);
        _indexing.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Raised on the UI thread after each refresh, for pages that show more detail.</summary>
    public event EventHandler? Refreshed;

    public IndexProgress Progress { get; private set; } = new(0, 0, 0, 0, 0, 0);

    public LibraryCounts Counts { get; private set; } = new(0, 0, 0, 0, 0);

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Nothing to process";

    /// <summary>Files or stages the user could do something about (a password, a damaged file).</summary>
    [ObservableProperty]
    public partial bool NeedsAttention { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndexPause))]
    public partial bool IndexPaused { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOcrPause))]
    public partial bool OcrPaused { get; private set; }

    [ObservableProperty]
    public partial bool WaitingForDiskSpace { get; private set; }

    /// <summary>Files to read or index-lane stages waiting or running, so Pause indexing would pause something.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndexPause))]
    public partial bool HasIndexWork { get; private set; }

    /// <summary>Scanned pages waiting for OCR, so Pause reading scans would pause something.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOcrPause))]
    public partial bool HasOcrWork { get; private set; }

    /// <summary>The Pause indexing button shows only while there is indexing to pause, or to resume.</summary>
    public bool ShowIndexPause => HasIndexWork || IndexPaused;

    public bool ShowOcrPause => HasOcrWork || OcrPaused;

    public void Start() => _timer.Start();

    /// <summary>Refreshes now, as after the user pauses a lane, rather than at the next tick.</summary>
    public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    public void SetPaused(Lane lane, bool paused)
    {
        if (paused) _indexing.Pause(lane);
        else _indexing.Resume(lane);
        Invalidate();
    }

    async Task OnTickAsync()
    {
        if (_refreshing || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _refreshing = true;
        try
        {
            Progress = await _queries.GetProgressAsync();
            Counts = await _library.GetCountsAsync();
            IndexPaused = _indexing.IsPaused(Lane.Index);
            OcrPaused = _indexing.IsPaused(Lane.Ocr);
            WaitingForDiskSpace = _indexing.WaitingForDiskSpace;
            NeedsAttention = Progress.NeedAttention > 0 || _indexing.Unreadable.Count > 0;
            HasIndexWork = _indexing.IsScanning || Counts.Unhashed - _indexing.Unreadable.Count > 0 || Progress.Indexing > 0;
            HasOcrWork = Progress.PagesAwaitingOcr > 0;
            Summary = Describe();
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not refresh indexing progress");
        }
        finally
        {
            _refreshing = false;
        }
    }

    string Describe()
    {
        var unhashed = Counts.Unhashed - _indexing.Unreadable.Count;
        var indexWork = unhashed > 0 || Progress.Indexing > 0;
        if (_indexing.IsScanning) return "Looking through your folders";
        if (indexWork && IndexPaused) return "Indexing paused";
        if (unhashed > 0) return WaitingForDiskSpace && unhashed <= Counts.OnlineOnly
            ? "Waiting for disk space"
            : $"Reading {unhashed:N0} new {Plural(unhashed, "file", "files")}";
        if (Progress.Indexing > 0) return $"Indexing · {Progress.Searchable:N0} of {Progress.Documents:N0} searchable";
        if (Progress.PagesAwaitingOcr > 0)
            return OcrPaused ? "Reading scanned pages paused" : $"Reading scanned pages · {Progress.PagesAwaitingOcr:N0} left";
        return Progress.Documents == 0 ? "Nothing to process" : $"Up to date · {Progress.Documents:N0} {Plural(Progress.Documents, "item", "items")}";
    }

    internal static string Plural(long count, string one, string many) => count == 1 ? one : many;
}

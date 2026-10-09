using System.Windows.Threading;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Progress;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

public enum PhaseState { Waiting, Running, Paused, Done }

/// <summary>
/// One step of indexing as Settings > Processing shows it: a bar, "1,100 of 1,240 · about 2 minutes left".
/// Updated in place each refresh, so the row (and an indeterminate bar's sweep) isn't rebuilt every tick.
/// <see cref="Left"/> is the estimate while it runs, null until there is enough to go on.
/// </summary>
public sealed partial class PhaseProgress(string label, string detail, string unit = "") : ObservableObject
{
    readonly RateEstimator _rate = new();

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    [ObservableProperty]
    public partial PhaseState State { get; private set; }

    [ObservableProperty]
    public partial double Percent { get; private set; }

    /// <summary>Running with no total yet, as while the folders are listed.</summary>
    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public TimeSpan? Left { get; private set; }

    /// <summary>A step with no count, such as listing folders.</summary>
    public void Show(PhaseState state, string status, long done = 0, long total = 0)
    {
        Set(state, done, total, status, null);
        IsIndeterminate = state == PhaseState.Running;
    }

    /// <summary>A step with a count: its state, and its rate fed and asked for the time left.</summary>
    public void Count(DateTimeOffset now, long done, long total, bool paused, string? waiting = null)
    {
        IsIndeterminate = false;
        var remaining = Math.Max(0, total - done);
        var units = unit.Length > 0 ? " " + unit : "";
        if (remaining == 0)
        {
            _rate.Reset();
            Set(PhaseState.Done, done, total, total == 0 ? "Nothing to do" : $"Done · {total:N0}{units}", null);
            return;
        }
        var counted = $"{done:N0} of {total:N0}{units}";
        if (paused || waiting is not null)
        {
            // Time spent paused or waiting would read as slowness once it goes again.
            _rate.Reset();
            Set(PhaseState.Paused, done, total, $"{counted} · {waiting ?? "Paused"}", null);
            return;
        }
        _rate.Record(now, done);
        var left = _rate.Estimate(now, remaining);
        Set(PhaseState.Running, done, total, $"{counted} · {(left is { } l ? TimeLeft.Describe(l) + " left" : "estimating time left")}", left);
    }

    void Set(PhaseState state, long done, long total, string status, TimeSpan? left)
    {
        State = state;
        Percent = total > 0 ? 100.0 * done / total : state == PhaseState.Done ? 100 : 0;
        Status = status;
        Left = left;
    }
}

/// <summary>
/// What indexing is doing, in words, for the sidebar and Settings > Processing. The indexing service reports
/// changes from background threads, often; this coalesces them into at most one refresh per tick on the UI thread.
/// </summary>
public sealed partial class LibraryActivity : ObservableObject
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(750);

    readonly IndexingService _indexing;
    readonly IndexQueries _queries;
    readonly LibraryStore _library;
    readonly ReviewService _review;
    readonly ILogger<LibraryActivity> _log;
    readonly DispatcherTimer _timer;
    readonly PhaseProgress _finding = new("Finding files", "Listing your folders");
    readonly PhaseProgress _reading = new("Reading files", "Checking each file; online-only files download");
    readonly PhaseProgress _making = new("Making searchable", "Text and covers");
    readonly PhaseProgress _scans = new("Reading scans", "Text from scanned pages, by OCR", "pages");
    readonly Dictionary<long, string> _titles = [];
    int _dirty = 1;
    bool _refreshing;

    public LibraryActivity(IndexingService indexing, IndexQueries queries, LibraryStore library, MetadataProjector metadata, ReviewService review,
        ILogger<LibraryActivity> log)
    {
        _indexing = indexing;
        _queries = queries;
        _library = library;
        _review = review;
        _log = log;
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, async (_, _) => await OnTickAsync(), Dispatcher.CurrentDispatcher);
        _indexing.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
        // Metadata edits and projections change what the library shows without any indexing.
        metadata.Projected += (_, _) => Interlocked.Exchange(ref _dirty, 1);
        Phases = [_finding, _reading, _making, _scans];
    }

    /// <summary>Raised on the UI thread after each refresh, for pages that show more detail.</summary>
    public event EventHandler? Refreshed;

    public IndexProgress Progress { get; private set; } = new(0, 0, 0, 0, 0, 0);

    public LibraryCounts Counts { get; private set; } = new(0, 0, 0, 0, 0);

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Nothing to process";

    /// <summary>Indexing step by step: finding files, reading them, making them searchable, reading scans.</summary>
    public IReadOnlyList<PhaseProgress> Phases { get; }

    /// <summary>"About 25 minutes left", while there is work; otherwise empty.</summary>
    [ObservableProperty]
    public partial string TimeLeftText { get; private set; } = "";

    /// <summary>What each part of indexing is working on right now, one line each.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Now { get; private set; } = [];

    /// <summary>Files needing attention and online-only files still to download, in a line.</summary>
    [ObservableProperty]
    public partial string Notes { get; private set; } = "";

    /// <summary>Metadata suggestions in Needs review: cards for documents' fields, and new terms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewCount))]
    public partial long SuggestionCount { get; private set; }

    /// <summary>Files needing attention, as Needs review's second tab lists them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReviewCount))]
    public partial long AttentionCount { get; private set; }

    /// <summary>Everything in Needs review, for the sidebar.</summary>
    public long ReviewCount => SuggestionCount + AttentionCount;

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
        if (_refreshing) return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            // No news, but while there is work the clock still moves, so a stall shows in the time left.
            if (HasIndexWork || HasOcrWork) UpdatePhases(DateTimeOffset.UtcNow);
            return;
        }
        _refreshing = true;
        try
        {
            Progress = await _queries.GetProgressAsync();
            Counts = await _library.GetCountsAsync();
            IndexPaused = _indexing.IsPaused(Lane.Index);
            OcrPaused = _indexing.IsPaused(Lane.Ocr);
            WaitingForDiskSpace = _indexing.WaitingForDiskSpace;
            NeedsAttention = Progress.NeedAttention > 0 || _indexing.Unreadable.Count > 0;
            AttentionCount = Progress.NeedAttention + _indexing.Unreadable.Count;
            SuggestionCount = await _review.CountAsync();
            HasIndexWork = _indexing.IsScanning || Counts.Unhashed - _indexing.Unreadable.Count > 0 || Progress.Indexing > 0;
            HasOcrWork = Progress.PagesAwaitingOcr > 0;
            Summary = Describe();
            UpdatePhases(DateTimeOffset.UtcNow);
            var lines = await DescribeNowAsync();
            if (!lines.SequenceEqual(Now)) Now = lines;
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

    void UpdatePhases(DateTimeOffset now)
    {
        var scanning = _indexing.IsScanning;
        var unreadable = _indexing.Unreadable.Count;
        var toHash = Math.Max(0, Counts.Unhashed - unreadable);
        var waitingForSpace = WaitingForDiskSpace && toHash <= Counts.UnhashedOnlineOnly;

        if (scanning) _finding.Show(PhaseState.Running, "Looking through your folders…");
        else _finding.Show(PhaseState.Done, $"{Counts.Files:N0} {Plural(Counts.Files, "file", "files")}", Counts.Files, Counts.Files);
        _reading.Count(now, Counts.Files - toHash, Counts.Files, IndexPaused, waitingForSpace ? "Waiting for disk space" : null);
        _making.Count(now, Progress.Indexed, Progress.Queued, IndexPaused);
        _scans.Count(now, Progress.OcrPagesDone, Progress.OcrPages, OcrPaused);

        var running = Phases.Where(p => p.State == PhaseState.Running).ToList();
        if (scanning) TimeLeftText = "Working out what there is to do";
        else if (running.Count == 0) TimeLeftText = Phases.Any(p => p.State == PhaseState.Paused) ? "Paused" : "";
        // The steps run side by side, so the slowest one says when everything is done.
        else if (running.Any(p => p.Left is null)) TimeLeftText = "Working out how long this will take";
        else TimeLeftText = $"About {TimeLeft.Describe(running.Max(p => p.Left!.Value))} left";

        var notes = new List<string>();
        if (Progress.NeedAttention + unreadable > 0)
            notes.Add($"{Progress.NeedAttention + unreadable:N0} {Plural(Progress.NeedAttention + unreadable, "file needs", "files need")} attention");
        if (Counts.UnhashedOnlineOnly > 0)
            notes.Add($"{Counts.UnhashedOnlineOnly:N0} online-only {Plural(Counts.UnhashedOnlineOnly, "file", "files")} to download ({Size(Counts.UnhashedOnlineOnlyBytes)})");
        Notes = string.Join(" · ", notes);
    }

    async Task<IReadOnlyList<string>> DescribeNowAsync()
    {
        var lines = new List<string>();
        if (_indexing.Hashing is { } file) lines.Add($"Reading {System.IO.Path.GetFileName(file)}");
        if (_indexing.Running(Lane.Index) is { } job)
        {
            var title = await TitleAsync(job.DocumentId);
            lines.Add(job.Stage switch
            {
                Stage.Probe => $"Opening {title}",
                Stage.Text => $"Reading the text of {title}",
                Stage.Covers => $"Making a cover for {title}",
                _ => $"Working on {title}",
            });
        }
        if (_indexing.Running(Lane.Ocr) is { } ocr) lines.Add($"Reading scanned pages of {await TitleAsync(ocr.DocumentId)}");
        return lines;
    }

    async Task<string> TitleAsync(long documentId)
    {
        if (_titles.TryGetValue(documentId, out var known)) return known;
        var title = await _queries.GetTitleAsync(documentId);
        if (title is null) return "a new file"; // Probe hasn't named it yet; ask again next time
        if (_titles.Count > 256) _titles.Clear();
        return _titles[documentId] = title;
    }

    internal static string Plural(long count, string one, string many) => count == 1 ? one : many;

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };
}

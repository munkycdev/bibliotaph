using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>Where reading the library for meaning is.</summary>
public enum LibraryVectorsState
{
    /// <summary>There is no embedding model, or AI is off: nothing to do.</summary>
    Unavailable,

    /// <summary>Switched off in Settings > AI.</summary>
    Off,

    /// <summary>Making a book's vectors.</summary>
    Reading,

    /// <summary>Waiting while the AI is being used, or for its turn after starting up.</summary>
    Waiting,

    /// <summary>Every page with text has a vector.</summary>
    Done,

    /// <summary>The embedding model stopped answering; it is tried again later.</summary>
    Stopped,
}

/// <summary>How far reading the library for meaning has got: pages with text, those with vectors, and what it is doing.</summary>
public sealed record LibraryVectorsStatus(LibraryVectorsState State, int Pages, int WithVectors, string? Problem)
{
    public static LibraryVectorsStatus Unavailable { get; } = new(LibraryVectorsState.Unavailable, 0, 0, null);
}

/// <summary>What one step of reading the library for meaning did.</summary>
public enum LibraryVectorsStep
{
    /// <summary>Made some of a book's vectors (all of them, or as many as it could before giving way).</summary>
    Read,

    /// <summary>Nothing to do now: off, unavailable, or the AI is in use.</summary>
    Waiting,

    /// <summary>Every page with text has a vector.</summary>
    Done,

    /// <summary>The embedding model failed; wait before trying again.</summary>
    Failed,
}

/// <summary>
/// Reads the whole library for meaning in the background, so questions to the library find pages by meaning as well as
/// by words: book by book, it makes the page vectors Ask this book makes for a book opened in the reader, with the same
/// code, model and storage (index.db's page_vec). It is slow work (hours, for a large library) and goes last: it waits
/// a minute after starting, gives way between batches while the AI is being used (and for half a minute after) or a
/// book being read wants its vectors, and stops for a while when the model server fails. Vectors are kept as they are
/// made, so it carries on where it was after a restart. It runs with an embedding model set, and can be switched off in
/// Settings > AI. Ahead of the pages it has <see cref="BookVectors"/> make each card's vector, which runs whenever an
/// embedding model is set, even with the pages switched off.
/// </summary>
public sealed class LibraryVectors(
    AiSettings ai, AskService ask, LibraryAskQueries queries, LibraryStore library, AiUse use, BookVectors books, TimeProvider? clock = null,
    ILogger<LibraryVectors>? log = null)
    : IHostedService, IDisposable
{
    /// <summary>How long after starting it waits, so the app's own start-up work goes first.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    /// <summary>How long after the user's last request to the AI it waits before carrying on.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    /// <summary>How often it looks again while waiting for the AI to be free.</summary>
    static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);

    /// <summary>How long it waits after the model server fails.</summary>
    static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>How long it waits once everything is read, before looking for books that have gained text since.</summary>
    static readonly TimeSpan AfterDone = TimeSpan.FromMinutes(10);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly CancellationTokenSource _stopping = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    Task? _running;

    /// <summary>The last document finished or passed over in this pass, in id order: where the next step starts looking.</summary>
    long _after;

    /// <summary>The model the current pass is for; a different one starts again from the first book.</summary>
    string? _model;

    volatile LibraryVectorsStatus _status = LibraryVectorsStatus.Unavailable;

    /// <summary>What it is doing and how far it has got.</summary>
    public LibraryVectorsStatus Status => _status;

    /// <summary>Raised, on any thread, when <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether it should run: AI on, an embedding model set, and not switched off.</summary>
    bool IsWanted => ai.Setup.IsReady && ai.Setup.LibraryVectors && ask.HasEmbeddingModel;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ai.Changed += OnAiChanged;
        _running = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ai.Changed -= OnAiChanged;
        await _stopping.CancelAsync();
        if (_running is { } running)
        {
            try
            {
                await running.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    void OnAiChanged(object? sender, EventArgs e) => Wake();

    /// <summary>Looks again now rather than at the end of a wait: AI's setup changed.</summary>
    void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RefreshStatusAsync(ct);
            await WaitAsync(StartDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                var step = await FillNextAsync(ct);
                var wait = step switch
                {
                    LibraryVectorsStep.Read => TimeSpan.Zero,
                    LibraryVectorsStep.Waiting => Recheck,
                    LibraryVectorsStep.Done => AfterDone,
                    _ => AfterFailure,
                };
                if (wait > TimeSpan.Zero) await WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Reading the library for meaning failed");
            Report(_status with { State = LibraryVectorsState.Stopped, Problem = "Reading the library for meaning stopped. The log has the details." });
        }
    }

    /// <summary>Waits for <paramref name="span"/>, or until AI's setup changes.</summary>
    async Task WaitAsync(TimeSpan span, CancellationToken ct)
    {
        using var delay = new CancellationTokenSource(span, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, delay.Token);
        try
        {
            await _wake.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One step: unless the AI is in use, makes the next few cards' vectors, or, unless it is off, the vectors of the next
    /// book that lacks some (as many as it can before giving way), and says what it did.
    /// </summary>
    public async Task<LibraryVectorsStep> FillNextAsync(CancellationToken ct = default)
    {
        var quiet = use.QuietFor(Quiet);
        if (quiet)
        {
            // The cards' vectors are few and quick, and go first.
            switch (await books.FillNextAsync(ct))
            {
                case BookVectorsStep.Made:
                    return LibraryVectorsStep.Read;
                case BookVectorsStep.Failed:
                    await RefreshStatusAsync(ct, LibraryVectorsState.Stopped, books.Problem);
                    return LibraryVectorsStep.Failed;
            }
        }
        if (!IsWanted || ai.Setup.EmbeddingName is not { } model)
        {
            await RefreshStatusAsync(ct);
            return LibraryVectorsStep.Waiting;
        }
        if (!quiet)
        {
            Report(_status with { State = LibraryVectorsState.Waiting });
            return LibraryVectorsStep.Waiting;
        }
        if (model != _model)
        {
            _model = model;
            _after = 0;
        }
        IReadOnlyList<long> kept = ai.Setup.SkippedRoots.Count == 0 ? [] : await library.GetDocumentIdsInRootsAsync([.. ai.Setup.SkippedRoots], ct);
        var next = await queries.NextWithoutVectorsAsync(model, EmbeddingText.Version, _after, kept, ct)
            ?? (_after > 0 ? await queries.NextWithoutVectorsAsync(model, EmbeddingText.Version, 0, kept, ct) : null);
        if (next is not { } documentId)
        {
            _after = 0;
            await RefreshStatusAsync(ct, LibraryVectorsState.Done);
            return LibraryVectorsStep.Done;
        }

        Report(_status with { State = LibraryVectorsState.Reading, Problem = null });
        var fill = await ask.FillInBackgroundAsync(documentId, () => ct.IsCancellationRequested || !IsWanted || !use.QuietFor(Quiet), ct);
        ct.ThrowIfCancellationRequested();
        if (fill is { Problem: { } problem })
        {
            await RefreshStatusAsync(ct, LibraryVectorsState.Stopped, problem);
            return LibraryVectorsStep.Failed;
        }
        // A book done, or one that can't be read (kept from AI as Ask decides it), is passed over until the next pass;
        // one that gave way is carried on from where it stopped.
        if (fill is null || fill.Done >= fill.Total || await ask.WhyNotAsync(documentId, ct) is not null) _after = documentId;
        await RefreshStatusAsync(ct, LibraryVectorsState.Reading);
        return LibraryVectorsStep.Read;
    }

    /// <summary>Counts the pages read so far, and says what it is doing.</summary>
    async Task RefreshStatusAsync(CancellationToken ct, LibraryVectorsState? state = null, string? problem = null)
    {
        if (!ask.HasEmbeddingModel || !ai.Setup.IsReady || ai.Setup.EmbeddingName is not { } model)
        {
            Report(LibraryVectorsStatus.Unavailable);
            return;
        }
        IReadOnlyList<long> kept = ai.Setup.SkippedRoots.Count == 0 ? [] : await library.GetDocumentIdsInRootsAsync([.. ai.Setup.SkippedRoots], ct);
        var (pages, withVectors) = await queries.GetVectorProgressAsync(model, EmbeddingText.Version, kept, ct);
        var now = !ai.Setup.LibraryVectors ? LibraryVectorsState.Off
            : state ?? (withVectors >= pages ? LibraryVectorsState.Done : LibraryVectorsState.Waiting);
        Report(new LibraryVectorsStatus(now, pages, withVectors, problem));
    }

    void Report(LibraryVectorsStatus status)
    {
        if (status == _status) return;
        _status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _wake.Dispose();
    }
}

using Bibliotaph.Classification;

namespace Bibliotaph.Processing;

/// <summary>Where the AI is with a book, as the AI reading page shows it.</summary>
public enum AiReadingStep
{
    /// <summary>Sent; the model is loading or hasn't started on the excerpt.</summary>
    Waiting,

    /// <summary>Reading the excerpt, with progress when the server reports it.</summary>
    Reading,

    /// <summary>Writing its answer.</summary>
    Writing,

    /// <summary>Done: the evidence check has kept some values and dropped others.</summary>
    Done,

    /// <summary>The model couldn't read it, or its answer couldn't be used.</summary>
    Failed,
}

/// <summary>
/// One book the AI is reading or has read: its step, the answer's fields as they are written, then what the evidence
/// check kept and dropped, or why it failed. Held in memory only, for the page; book text never goes to the log.
/// </summary>
public sealed record AiReadingBook(long DocumentId, string Title, string Model, DateTimeOffset Started, AiReadingStep Step,
    double? Read, IReadOnlyList<PartialField> Fields, ClassifierResult? Result = null, string? Problem = null, TimeSpan? Took = null);

/// <summary>
/// The live view of the Classify lane: the book being read now and the last <see cref="Keep"/> read.
/// <see cref="Changed"/> is raised on the lane's thread, often while an answer streams, so a page should look at
/// <see cref="Current"/> on a timer rather than redraw for each event.
/// </summary>
public sealed class AiReadingFeed(TimeProvider? clock = null)
{
    public const int Keep = 20;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Lock _gate = new();
    readonly List<AiReadingBook> _recent = [];

    public AiReadingBook? Current { get; private set; }

    /// <summary>Raised on any thread whenever the current book moves on or a book finishes.</summary>
    public event EventHandler? Changed;

    /// <summary>The last books read, newest first.</summary>
    public IReadOnlyList<AiReadingBook> Recent
    {
        get
        {
            lock (_gate) return [.. _recent];
        }
    }

    /// <summary>A book is sent to the model. Returns the progress the classifier reports the answer to.</summary>
    public IProgress<ModelProgress> Start(long documentId, string title, string model)
    {
        Set(new AiReadingBook(documentId, title, model, _clock.GetUtcNow(), AiReadingStep.Waiting, null, []));
        return new Reporter(this, documentId);
    }

    /// <summary>The evidence check is done.</summary>
    public void Finish(long documentId, ClassifierResult result) => End(documentId, b => b with { Step = AiReadingStep.Done, Result = result });

    /// <summary>The book failed: an answer that couldn't be used, or a model that couldn't read it.</summary>
    public void Fail(long documentId, string problem) => End(documentId, b => b with { Step = AiReadingStep.Failed, Problem = problem });

    /// <summary>The endpoint couldn't be used, so the book goes back to waiting: it isn't a result, so it isn't kept.</summary>
    public void Abandon(long documentId)
    {
        lock (_gate)
        {
            if (Current?.DocumentId != documentId) return;
            Current = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void End(long documentId, Func<AiReadingBook, AiReadingBook> finish)
    {
        lock (_gate)
        {
            if (Current is not { } book || book.DocumentId != documentId) return;
            var done = finish(book) with { Took = _clock.GetUtcNow() - book.Started };
            _recent.Insert(0, done);
            if (_recent.Count > Keep) _recent.RemoveRange(Keep, _recent.Count - Keep);
            Current = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Set(AiReadingBook book)
    {
        lock (_gate) Current = book;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Update(long documentId, ModelProgress progress)
    {
        lock (_gate)
        {
            if (Current is not { } book || book.DocumentId != documentId) return;
            Current = progress.Step switch
            {
                ModelStep.Reading => book with { Step = AiReadingStep.Reading, Read = progress.Read },
                ModelStep.Writing => book with { Step = AiReadingStep.Writing, Read = book.Read is null ? null : 1, Fields = PartialAnswer.Read(progress.Answer) },
                _ => book with { Step = AiReadingStep.Waiting },
            };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    sealed class Reporter(AiReadingFeed feed, long documentId) : IProgress<ModelProgress>
    {
        public void Report(ModelProgress value) => feed.Update(documentId, value);
    }
}

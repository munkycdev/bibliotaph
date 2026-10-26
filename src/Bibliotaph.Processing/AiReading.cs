using System.Globalization;
using Bibliotaph.Core.Metadata;
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

/// <summary>A book the AI has read, as the AI reading page lists it: what it kept, what it dropped and why, or why it failed.</summary>
public sealed record AiReadingDone(string Title, string Summary, string? Problem, IReadOnlyList<AiTestLine> Kept, IReadOnlyList<AiTestLine> Dropped);

/// <summary>The AI reading page's words, in the Windows app and on the web.</summary>
public static class AiReadingText
{
    public const string Subtitle =
        "What the AI is reading now, its answer as it writes it, and what the evidence check kept from the last books. Only values that quote their page are kept.";

    public const string NothingRead = "Nothing read since Bibliotaph started.";

    /// <summary>What the AI is doing with the book now.</summary>
    public static string Step(AiReadingBook book) => book.Step switch
    {
        AiReadingStep.Reading => $"Reading the excerpt with {book.Model}",
        AiReadingStep.Writing => $"{book.Model} is writing its answer",
        _ => $"Waiting for {book.Model}: a model that isn't loaded yet takes a little while to start",
    };

    /// <summary>With no book being read: why not.</summary>
    public static string Idle(bool ready, bool paused, string? waiting) =>
        !ready ? "AI is off, or not set up yet, so no book is being read."
        : paused ? "Cataloguing with AI is paused."
        : waiting is { Length: > 0 } ? $"Cataloguing with AI is waiting: {waiting}"
        : "No book is being read right now. Books are read one at a time as they're added or changed.";

    /// <summary>The answer's fields so far: "Title: The Sunken Lantern", "…" while a field has no value yet.</summary>
    public static IReadOnlyList<AiTestLine> Fields(AiReadingBook book) =>
        [.. book.Fields.Select(field => new AiTestLine(MetadataFields.Find(field.Name)?.Label ?? field.Name,
            field.Values.Count == 0 ? "…" : string.Join(", ", field.Values)))];

    /// <summary>A book read: "Kept 5 values, dropped 2 · 41 s", with each value's page and quote, or why it couldn't be read.</summary>
    public static AiReadingDone Describe(AiReadingBook book, Vocabulary vocabulary)
    {
        var took = book.Took is { } t ? $" · {Seconds(t)}" : "";
        if (book.Result is not { } result) return new AiReadingDone(book.Title, $"Couldn't be read{took}", book.Problem, [], []);
        var kept = AiText.Kept(result, vocabulary);
        var dropped = AiText.Dropped(result);
        return new AiReadingDone(book.Title, $"Kept {(kept.Count == 1 ? "1 value" : $"{kept.Count.ToString(CultureInfo.CurrentCulture)} values")}, dropped {dropped.Count.ToString(CultureInfo.CurrentCulture)}{took}",
            null, kept, dropped);
    }

    public static string Seconds(TimeSpan time) => $"{Math.Max(0, time.TotalSeconds).ToString("0", CultureInfo.CurrentCulture)} s";
}

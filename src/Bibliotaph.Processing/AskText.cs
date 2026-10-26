using System.Globalization;
using Bibliotaph.Classification;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What Ask this book's panel says as it finds pages and answers from them, kept here so the Windows reader and the
/// web reader say it in the same words.
/// </summary>
public static class AskText
{
    public const string PagesHeading = "Pages that look like they answer it";
    public const string Rewriting = "Working out the follow-up…";
    public const string Looking = "Looking through the book…";
    public const string Waiting = "Waiting for the model…";
    public const string NotAnswered = "The pages it read don't answer that. They're below, in case they help.";
    public const string Stopped = "Stopped. What was written so far is above.";
    public const string Empty = "The model sent back nothing. Try asking another way.";
    public const string Failed = "That didn't work. The log has the details.";

    public static string CouldNotAnswer(string problem) => $"The model couldn't answer: {problem}";

    /// <summary>"Looked for: where is the red dragon's lair?", for a follow-up the model rewrote.</summary>
    public static string LookedFor(string rewritten) => $"Looked for: {rewritten}";

    /// <summary>Over the pages once answered from them: "Read from pages 112, 113 and 140".</summary>
    public static string ReadFrom(IReadOnlyList<AskPromptPage> pages) => $"Read from {Pages([.. pages.Select(p => p.Marker)])}";

    /// <summary>"p. 112 · The Mill", or "p. 112" for a page under no bookmark.</summary>
    public static string Link(AskPassage passage) => passage.Heading is { } heading ? $"p. {passage.Page} · {heading}" : $"p. {passage.Page}";

    /// <summary>"Found by its words", "by its meaning", or both.</summary>
    public static string Found(AskPassage passage) => (passage.ByWords, passage.ByMeaning) switch
    {
        (true, true) => "Found by its words and its meaning",
        (false, true) => "Found by its meaning",
        _ => "Found by its words",
    };

    /// <summary>Why there is nothing to answer from: nothing looks like it's about the question. Null when something does.</summary>
    public static string? Weak(AskFindings findings) => findings.Weak
        ? "Nothing in this book looks like it's about that." + (findings.LookedAt.Count > 0 ? $" These pages came closest: {Pages(findings.LookedAt)}." : "")
        : null;

    /// <summary>"212 of 226 pages have text; 14 await OCR."</summary>
    public static string Coverage(PageCoverage coverage) => coverage.Pages == 0 ? "This book has no pages with text yet."
        : $"{Count(coverage.WithText)} of {Count(coverage.Pages)} pages have text" + (coverage.AwaitingOcr > 0 ? $"; {Count(coverage.AwaitingOcr)} await OCR." : ".");

    /// <summary>Whether pages were found by meaning too, and how far the book's vectors have got since.</summary>
    public static string Meaning(AskFindings findings, AskFill? fill) => findings.Meaning switch
    {
        AskMeaning.Off => "Found by words alone. Choose an embedding model in Settings > AI to find passages by meaning too.",
        _ when findings.MeaningProblem is { } problem => $"Found by words alone this time: {problem}",
        AskMeaning.On => "Found by words and by meaning.",
        _ when fill is { Running: true } => $"Found by words, and by meaning where it could: {Count(fill.Done)} of {Count(fill.Total)} pages are read for meaning so far, the rest in the background.",
        _ when fill is { Problem: { } stopped } => $"Found by words, and by meaning where it could. Reading the rest for meaning stopped: {stopped}",
        _ when fill is { Done: var done, Total: var total } && done >= total && total > 0 => "Found by words, and partly by meaning. Every page is read for meaning now: ask again to use them all.",
        _ => "Found by words, and by meaning where it could.",
    };

    /// <summary>What the model is doing with the pages: "Waiting for the model…", "Reading the pages… 40%", "Writing…".</summary>
    public static string Step(ModelProgress progress) => progress.Step switch
    {
        ModelStep.Reading when progress.Read is { } read => $"Reading the pages… {read.ToString("P0", CultureInfo.CurrentCulture)}",
        ModelStep.Reading => "Reading the pages…",
        ModelStep.Writing => "Writing…",
        _ => Waiting,
    };

    /// <summary>"page 4", or "pages 112, 113 and 140".</summary>
    public static string Pages(IReadOnlyList<string> pages) => pages.Count switch
    {
        0 => "",
        1 => $"page {pages[0]}",
        _ => $"pages {string.Join(", ", pages.Take(pages.Count - 1))} and {pages[^1]}",
    };

    static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}

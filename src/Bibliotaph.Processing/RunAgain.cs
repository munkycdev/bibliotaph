using System.Globalization;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// The button at the end of a Settings › Processing row, on Windows and the web: looking through the folders again,
/// looking every book up again, reading every book again with the AI, and scanning every book whole again. Each but
/// the first asks first, since it costs a pass over the library; the user's own values stay as they are. Reading and
/// indexing files, and reading scans, have none: a whole library's worth changes nothing unless a file changed, and
/// Reprocess does one book.
/// </summary>
public sealed class RunAgainService(IJobControl jobs, AiService ai, ChapterScanService chapters, SourceRootStore roots)
{
    public const string Finding = "finding";
    public const string LookingUp = "lookup";
    public const string Cataloguing = "ai";
    public const string Scanning = "scan";

    const string Kept = "Values you've accepted, typed or rejected stay as they are.";

    static string N(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    static string Books(long count) => count == 1 ? "book" : "books";

    /// <summary>
    /// The steps that can run again now: the folders once there are any, lookups once a book has been looked up, and
    /// the AI's steps while <paramref name="aiOn"/> (this library's own AI is set up) once it has read a book.
    /// </summary>
    public async Task<IReadOnlyList<RunAgainOffer>> OffersAsync(IndexProgress progress, bool aiOn, CancellationToken ct = default)
    {
        var offers = new List<RunAgainOffer>();
        if ((await roots.ListAsync(ct)).Count > 0) offers.Add(new RunAgainOffer(Finding, "Look through your folders again now"));
        if (progress.LookedUp > 0)
            offers.Add(new RunAgainOffer(LookingUp, "Look every book up again",
                $"{N(progress.LookedUp)} {Books(progress.LookedUp)} will be looked up again, one at a time in the background, asking every catalogue afresh. " +
                $"With RPGGeek's pace of a book every few seconds that can take a while. {Kept}",
                $"Look up {N(progress.LookedUp)} again"));
        if (aiOn && AiText.ReadAgain(await ai.SummarizeAsync(ct)) is { } readAgain)
            offers.Add(new RunAgainOffer(Cataloguing, "Read every book again, with your lists as they stand now", readAgain.Question, readAgain.Label));
        if (aiOn && chapters.IsOn && progress.ScannedWhole > 0)
            offers.Add(new RunAgainOffer(Scanning, "Scan every book again from the start",
                $"What the AI found in the chapters of {N(progress.ScannedWhole)} {Books(progress.ScannedWhole)} is forgotten, and they go back to it one at a time, " +
                $"in the background, which takes as long as scanning them did. {Kept}",
                $"Scan {N(progress.ScannedWhole)} again"));
        return offers;
    }

    /// <summary>Runs a step again for every book. Returns what it did, in a line, or null for a step that can't be run again.</summary>
    public async Task<string?> RunAsync(string step, CancellationToken ct = default)
    {
        switch (step)
        {
            case Finding:
                jobs.RequestScan();
                return "Looking through your folders now.";
            case LookingUp:
                var looking = await jobs.RerunAsync(Stage.LookUp, ct);
                return looking == 0 ? "There are no books to look up." : "The books go back to be looked up one at a time, in the background.";
            case Cataloguing:
                return AiText.ReadingAgain(await ai.ReadAgainAsync(ct));
            case Scanning:
                var scanning = await chapters.RescanAllAsync(ct);
                return scanning == 0 ? "There are no books to scan." : "The books go back to the AI to be scanned one at a time, in the background.";
            default:
                return null;
        }
    }
}

using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>One line of a Test result: a value the model gave and the page that backs it, or one the check threw out.</summary>
public sealed record AiTestLine(string Text, string Detail);

/// <summary>Settings > AI's words and Test with a book's, which the Windows app and the web both show.</summary>
public static class AiText
{
    public const string Connecting = "Connecting…";

    public const string SwitchedOn = "On. Books are classified in the background, one at a time.";

    public const string SwitchedOff = "Off. Nothing is sent; your setup is kept.";

    public const string ConnectFirst = "Connect to a model server and pick a model first. Saving turns AI on.";

    public static string Books(long count) => count == 1 ? "book" : "books";

    /// <summary>"Found Ollama 0.5.1 with 3 models.", or that it has none.</summary>
    public static string Found(EndpointInfo info)
    {
        var server = info.Kind == EndpointKind.Ollama ? $"Ollama{(info.Version is { } v ? " " + v : "")}" : "an OpenAI-compatible server";
        return info.Models.Count == 0
            ? $"Found {server}, but it has no models. Download one there first."
            : $"Found {server} with {info.Models.Count:N0} {(info.Models.Count == 1 ? "model" : "models")}.";
    }

    /// <summary>What Save says: a new model reclassifies nothing by itself.</summary>
    public static string Saved(string? before, string model, bool enabled) =>
        before is { } old && old != model
            ? "Saved. Books already classified keep their values; Reclassify reads them again with this model."
            : enabled ? "Saved. Books are classified in the background, one at a time." : "Saved.";

    public static string Reclassified(int queued) =>
        queued == 0 ? "Nothing to reclassify." : "The books go back to the AI one at a time, in the background.";

    /// <summary>How far classification has got, under Use AI.</summary>
    public static string Progress(AiSetup setup, ClassificationSummary summary, IndexProgress progress)
    {
        var model = setup.ModelName;
        return model is null || !setup.Enabled
            ? $"{summary.Current + summary.Earlier:N0} {Books(summary.Current + summary.Earlier)} classified so far."
            : progress.ToClassify > 0
                ? $"{summary.Current:N0} {Books(summary.Current)} classified with {model}; {progress.ToClassify:N0} waiting."
                : $"{summary.Current:N0} {Books(summary.Current)} classified with {model}. Nothing is waiting.";
    }

    /// <summary>How far reading whole books has got, as "Scanning whole books · 12 of 840", or empty.</summary>
    public static string ScanProgress(AiSetup setup, bool scanWholeBooks, IndexProgress progress)
    {
        var scans = progress.ScannedWhole + progress.ToScanWhole;
        return setup.ModelName is null || !setup.Enabled || !scanWholeBooks || scans == 0 ? ""
            : progress.ToScanWhole > 0 ? $"Scanning whole books · {progress.ScannedWhole:N0} of {scans:N0}"
            : $"{progress.ScannedWhole:N0} whole {Books(progress.ScannedWhole)} scanned.";
    }

    /// <summary>"12,345 of 98,765 pages read for meaning (13%). Waiting while the AI is in use.", or null without an embedding model.</summary>
    public static string? LibraryVectors(LibraryVectorsStatus status)
    {
        if (status.State == LibraryVectorsState.Unavailable) return null;
        var share = status.Pages == 0 ? "" : $" ({(double)status.WithVectors / status.Pages:P0})";
        var counted = $"{status.WithVectors:N0} of {status.Pages:N0} pages read for meaning{share}.";
        return status.State switch
        {
            LibraryVectorsState.Off => counted + " Switched off: questions to the library find the other pages by their words.",
            LibraryVectorsState.Reading => counted + " Reading the rest in the background.",
            LibraryVectorsState.Waiting => counted + " Waiting while the AI is in use.",
            LibraryVectorsState.Done => counted + " Every page is read.",
            LibraryVectorsState.Stopped => counted + $" Stopped for now: {status.Problem ?? "the model server didn't answer"}. It tries again in a few minutes.",
            _ => counted,
        };
    }

    public static string Reclassify(int earlier, long failed) => earlier > 0 && failed > 0
        ? $"{earlier:N0} {Books(earlier)} were classified by another model or an earlier version of Bibliotaph, and {failed:N0} couldn't be."
        : earlier > 0 ? $"{earlier:N0} {Books(earlier)} were classified by another model or an earlier version of Bibliotaph."
        : $"{failed:N0} {Books(failed)} couldn't be classified.";

    public static string ReclassifyLabel(int earlier, long failed) => earlier > 0 ? $"Reclassify {earlier + failed:N0}" : "Try again";

    /// <summary>What a test is doing now, and a line more about it.</summary>
    public static (string Text, string? Detail) Step(AiTestProgress progress, string model) => progress.Step switch
    {
        AiTestStep.Choosing => ("Picking a book from your library…", null),
        AiTestStep.Reading => ($"Reading {progress.Title}…", "Its opening pages, contents, introduction and a sample of the rest."),
        _ => ($"Waiting for {model}…", "A model that isn't loaded yet can take a minute or two to answer the first time."),
    };

    /// <summary>The line over a test's result: which book it read, how long it took, and whether it found anything.</summary>
    public static string? Summary(AiTestResult test)
    {
        if (test.Result is not { } result) return test.Title.Length > 0 ? $"Tried {test.Title}." : null;
        var seconds = test.Took.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
        return result.Accepted.Count == 0
            ? $"Read {test.Title} in {seconds} s, and found nothing it could back up with a quote."
            : $"Read {test.Title} in {seconds} s. Every value below quotes the page it came from.";
    }

    /// <summary>What the model kept, each with the page and quote that back it.</summary>
    public static IReadOnlyList<AiTestLine> Kept(ClassifierResult result, Vocabulary vocabulary) =>
        [.. result.Accepted.Select(claim =>
        {
            var value = claim.Field.Kind == FieldKind.Term && !claim.IsNewTerm
                ? vocabulary.Label(claim.Field.Vocabulary!, claim.Value)
                : claim.IsNewTerm ? $"{claim.Value} (new)" : claim.Value;
            return new AiTestLine($"{claim.Field.Label}: {value}",
                $"Page {ClassifierPrompt.Marker(claim.PdfPage).ToString(CultureInfo.CurrentCulture)}{(claim.FromSampling ? ", from sampled pages" : "")}: “{claim.Quote}”");
        })];

    /// <summary>What the evidence check threw out, and why.</summary>
    public static IReadOnlyList<AiTestLine> Dropped(ClassifierResult result) =>
        [.. result.Dropped.Select(dropped => new AiTestLine($"{MetadataFields.Find(dropped.Claim.Field)?.Label ?? dropped.Claim.Field}: {dropped.Claim.Value}", dropped.Reason))];
}

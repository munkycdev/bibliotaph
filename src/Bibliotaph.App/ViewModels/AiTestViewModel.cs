using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>One line of a Test result: a value the model gave and the page that backs it, or one the check threw out.</summary>
public sealed record AiTestLine(string Text, string Detail);

/// <summary>
/// The Test with a book dialog: classifies one book with the endpoint and model on screen, saved or not, and stores
/// nothing. While it runs it says which step it is on and for how long; then it shows what the model kept, with page
/// and quote, and what the evidence check threw out.
/// </summary>
public sealed partial class AiTestViewModel(AiService ai, string endpoint, string model) : ObservableObject
{
    /// <summary>Stops the test running now, if there is one.</summary>
    Action? _stop;

    public string Heading { get; } = $"Testing {model}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAgainCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>What the test is doing now: picking a book, reading it, or waiting for the model.</summary>
    [ObservableProperty]
    public partial string StepText { get; private set; } = "";

    [ObservableProperty]
    public partial string? StepDetail { get; private set; }

    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    public ObservableCollection<AiTestLine> Kept { get; } = [];

    public ObservableCollection<AiTestLine> Dropped { get; } = [];

    [ObservableProperty]
    public partial bool HasDropped { get; private set; }

    /// <summary>Runs the test; a second run reads the same book, so it compares models fairly.</summary>
    [RelayCommand(CanExecute = nameof(CanTestAgain))]
    public async Task TestAgainAsync()
    {
        using var work = new CancellationTokenSource();
        _stop = work.Cancel;
        IsRunning = true;
        Summary = null;
        Problem = null;
        Kept.Clear();
        Dropped.Clear();
        HasDropped = false;
        var clock = Stopwatch.StartNew();
        ElapsedText = "";
        var ticking = TickAsync(clock, work.Token);
        try
        {
            // Progress posts each step back to this (the UI) thread, whichever thread reports it.
            var test = await ai.TestAsync(endpoint, model, new Progress<AiTestProgress>(Show), work.Token);
            var seconds = test.Took.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
            if (test.Result is not { } result)
            {
                Summary = test.Title.Length > 0 ? $"Tried {test.Title}." : null;
                Problem = test.Problem;
                return;
            }
            Summary = result.Accepted.Count == 0
                ? $"Read {test.Title} in {seconds} s, and found nothing it could back up with a quote."
                : $"Read {test.Title} in {seconds} s. Every value below quotes the page it came from.";
            var vocabulary = await ai.VocabularyAsync();
            foreach (var claim in result.Accepted)
            {
                var value = claim.Field.Kind == FieldKind.Term && !claim.IsNewTerm
                    ? vocabulary.Label(claim.Field.Vocabulary!, claim.Value)
                    : claim.IsNewTerm ? $"{claim.Value} (new)" : claim.Value;
                Kept.Add(new AiTestLine($"{claim.Field.Label}: {value}",
                    $"Page {ClassifierPrompt.Marker(claim.PdfPage).ToString(CultureInfo.CurrentCulture)}{(claim.FromSampling ? ", from sampled pages" : "")}: “{claim.Quote}”"));
            }
            foreach (var dropped in result.Dropped)
                Dropped.Add(new AiTestLine($"{MetadataFields.Find(dropped.Claim.Field)?.Label ?? dropped.Claim.Field}: {dropped.Claim.Value}", dropped.Reason));
            HasDropped = Dropped.Count > 0;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            Summary = "Stopped.";
        }
        finally
        {
            await work.CancelAsync();
            await ticking;
            _stop = null;
            IsRunning = false;
        }
    }

    bool CanTestAgain() => !IsRunning;

    /// <summary>Stops a test still running: the dialog closing, or Cancel.</summary>
    public void Stop() => _stop?.Invoke();

    /// <summary>Counts the seconds, so a model that is still loading never looks like a frozen window.</summary>
    async Task TickAsync(Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                ElapsedText = $"{clock.Elapsed.TotalSeconds.ToString("0", CultureInfo.CurrentCulture)} s";
            }
        }
        catch (OperationCanceledException)
        {
            // The test finished or was stopped.
        }
    }

    void Show(AiTestProgress progress)
    {
        (StepText, StepDetail) = progress.Step switch
        {
            AiTestStep.Choosing => ("Picking a book from your library…", null),
            AiTestStep.Reading => ($"Reading {progress.Title}…", "Its opening pages, contents, introduction and a sample of the rest."),
            _ => ($"Waiting for {model}…", "A model that isn't loaded yet can take a minute or two to answer the first time."),
        };
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The Test with a book dialog: classifies one book with the endpoint and model on screen, or the built-in AI, and stores
/// nothing. While it runs it says which step it is on and for how long; then it shows what the model kept, with page
/// and quote, and what the evidence check threw out.
/// </summary>
public sealed partial class AiTestViewModel(AiService ai, Func<IProgress<AiTestProgress>, CancellationToken, Task<AiTestResult>> run, string model) : ObservableObject
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
            var test = await run(new Progress<AiTestProgress>(Show), work.Token);
            Summary = AiText.Summary(test);
            if (test.Result is not { } result)
            {
                Problem = test.Problem;
                return;
            }
            foreach (var line in AiText.Kept(result, await ai.VocabularyAsync())) Kept.Add(line);
            foreach (var line in AiText.Dropped(result)) Dropped.Add(line);
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

    void Show(AiTestProgress progress) => (StepText, StepDetail) = AiText.Step(progress, model);
}

using System.Globalization;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>Whether the app started with --pilot (slice 2d, choice P2), which shows the model pilot in Settings > AI.</summary>
public sealed class PilotMode(bool on)
{
    public bool IsOn { get; set; } = on;
}

/// <summary>
/// A pilot run, kept while the app is open so leaving Settings > AI doesn't stop it: the models to try, whether it is
/// running, and how far it has got.
/// </summary>
public sealed partial class PilotSession(PilotService pilot, ILogger<PilotSession> log) : ObservableObject
{
    Action? _stop;

    [ObservableProperty]
    public partial string Models { get; set; } = PilotService.DefaultModels;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>The review page's "Also save as my values in the catalog", on by default (choice P5).</summary>
    [ObservableProperty]
    public partial bool AlsoCatalog { get; set; } = true;

    /// <summary>Raised on the UI thread when a run stops, however it stopped.</summary>
    public event EventHandler? Stopped;

    /// <summary>Has each model read the books it hasn't yet, until it is done, paused, or the server stops answering.</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;
        using var work = new CancellationTokenSource();
        _stop = work.Cancel;
        IsRunning = true;
        Problem = null;
        ProgressText = "Starting…";
        var progress = new Progress<PilotProgress>(Show);
        var models = PilotService.ParseModels(Models);
        try
        {
            var end = await Task.Run(() => pilot.RunAsync(models, progress, work.Token));
            if (end.Finished) ProgressText = "Every model has read every book. Review your answers, then make the report.";
            else
            {
                ProgressText = "Stopped. Start carries on where it stopped.";
                Problem = end.Problem;
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            ProgressText = "Paused. Start carries on where it stopped.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The pilot run failed");
            ProgressText = "Stopped. Start carries on where it stopped.";
            Problem = ex.Message;
        }
        finally
        {
            _stop = null;
            IsRunning = false;
            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause() => _stop?.Invoke();

    void Show(PilotProgress p)
    {
        var each = p.AverageSeconds is { } s ? $", about {s.ToString("0", CultureInfo.CurrentCulture)} s each" : "";
        ProgressText = $"{p.Model} (model {p.ModelIndex + 1} of {p.Models}): {p.Done:N0} of {p.Total:N0} books read{each}.";
    }
}

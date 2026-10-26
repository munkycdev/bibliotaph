using Bibliotaph.Core.Progress;

namespace Bibliotaph.Web.Services;

public enum PhaseState { Waiting, Running, Paused, Done }

/// <summary>
/// One step of indexing as Settings › Processing shows it, as the Windows app's <c>PhaseProgress</c>: a bar and "1,100 of
/// 1,240 · 2 minutes left". The rate is fed from each poll, so the time left settles as the section stays open.
/// </summary>
public sealed class ProcessingPhase(string label, string detail, string unit = "")
{
    readonly RateEstimator _rate = new();

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public PhaseState State { get; private set; }

    public double Percent { get; private set; }

    /// <summary>Running with no total yet, as while the folders are listed.</summary>
    public bool IsIndeterminate { get; private set; }

    public string Status { get; private set; } = "";

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

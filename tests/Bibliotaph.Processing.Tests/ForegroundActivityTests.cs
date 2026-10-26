namespace Bibliotaph.Processing.Tests;

/// <summary>Background reading waits while a request is answered and just after, but never for long.</summary>
public sealed class ForegroundActivityTests
{
    /// <summary>Time moves only when the test says; delays still pass in real time, so a wait looks again.</summary>
    sealed class SteppedClock : TimeProvider
    {
        long _ticks = 1_000_000;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => 1_000_000;
    }

    readonly SteppedClock _clock = new();

    [Fact]
    public void A_request_and_the_moment_after_it_are_busy()
    {
        var foreground = new ForegroundActivity(_clock);
        var quietAtFirst = foreground.IsQuiet;

        var request = foreground.Begin();
        var whileAnswered = foreground.IsQuiet;
        request.Dispose();
        request.Dispose();
        var justAfter = foreground.IsQuiet;
        _clock.Advance(ForegroundActivity.Settle);

        Assert.Equal((true, false, false, true), (quietAtFirst, whileAnswered, justAfter, foreground.IsQuiet));
    }

    [Fact]
    public async Task Background_work_goes_ahead_when_the_requests_stop_or_once_it_has_waited_long_enough()
    {
        var ct = TestContext.Current.CancellationToken;
        var foreground = new ForegroundActivity(_clock);

        var first = foreground.Begin();
        var waiting = foreground.WaitForQuietAsync(ct);
        await Task.Delay(250, ct);
        var waitedWhileAnswered = !waiting.IsCompleted;
        first.Dispose();
        _clock.Advance(ForegroundActivity.Settle);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);

        using var endless = foreground.Begin();
        var capped = foreground.WaitForQuietAsync(ct);
        await Task.Delay(250, ct);
        var waitedAgain = !capped.IsCompleted;
        _clock.Advance(ForegroundActivity.MostWait);
        await capped.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.True(waitedWhileAnswered);
        Assert.True(waitedAgain);
    }
}

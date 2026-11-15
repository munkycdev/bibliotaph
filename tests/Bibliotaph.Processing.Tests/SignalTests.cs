namespace Bibliotaph.Processing.Tests;

/// <summary>A loop's signal, and the count of work in hand that says when indexing is idle.</summary>
public sealed class SignalTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Sets_before_a_wait_wake_it_once()
    {
        var signal = new Signal();
        signal.Set();
        signal.Set();

        Assert.True(await signal.WaitAsync(Long, Ct));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20), Ct));
    }

    [Fact]
    public async Task A_loop_is_busy_until_it_waits_and_again_from_the_moment_it_is_set()
    {
        var work = new WorkInHand();
        var signal = new Signal(work);
        var idleBefore = work.WhenIdleAsync(Ct).IsCompleted;

        signal.Begin();
        var startedBusy = work.WhenIdleAsync(Ct).IsCompleted;
        var waiting = signal.WaitAsync(Long, Ct);
        var idleWhileWaiting = work.WhenIdleAsync(Ct).IsCompleted;
        signal.Set();
        // Before the waiting loop has run again: what was handed to it is in hand already.
        var idleOnceSet = work.WhenIdleAsync(Ct).IsCompleted;
        await waiting;

        Assert.True(idleBefore);
        Assert.False(startedBusy);
        Assert.True(idleWhileWaiting);
        Assert.False(idleOnceSet);
        Assert.False(work.WhenIdleAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task A_wait_that_is_only_a_pause_keeps_the_loop_busy_and_one_that_times_out_is_busy_after()
    {
        var work = new WorkInHand();
        var signal = new Signal(work);
        signal.Begin();

        var pausing = signal.WaitAsync(TimeSpan.FromMilliseconds(50), Ct, idle: false);
        var idleWhilePausing = work.WhenIdleAsync(Ct).IsCompleted;
        Assert.False(await pausing);
        var looking = signal.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);
        var idleWhileLooking = work.WhenIdleAsync(Ct).IsCompleted;
        Assert.False(await looking);

        Assert.False(idleWhilePausing);
        Assert.True(idleWhileLooking);
        Assert.False(work.WhenIdleAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task A_loop_stopped_while_busy_or_waiting_counts_no_more_so_a_restart_starts_from_nothing()
    {
        var work = new WorkInHand();
        var waits = new Signal(work);
        var works = new Signal(work);
        using var stopping = new CancellationTokenSource();
        waits.Begin();
        works.Begin();
        var waiting = waits.WaitAsync(Long, stopping.Token);

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        waits.End();
        works.End();

        Assert.True(work.WhenIdleAsync(Ct).IsCompleted);
        works.Begin();
        Assert.False(work.WhenIdleAsync(Ct).IsCompleted);
    }

    [Fact]
    public async Task Waiting_for_idle_completes_when_the_last_work_is_done()
    {
        var work = new WorkInHand();
        work.Start();
        work.Start();
        var idle = work.WhenIdleAsync(Ct);

        work.Stop();
        var afterOne = idle.IsCompleted;
        work.Stop();
        await idle.WaitAsync(Long, Ct);

        Assert.False(afterOne);
    }
}

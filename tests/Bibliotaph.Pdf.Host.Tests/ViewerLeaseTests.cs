using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>Which viewer worker each reader window gets: the bookkeeping, then the pool with real workers.</summary>
public sealed class ViewerLeaseTests(SyntheticPdfs pdfs)
{
    sealed class Worker(string name)
    {
        public override string ToString() => name;
    }

    static ViewerLeases<Worker> Book(out Func<Worker> start)
    {
        var started = 0;
        start = () => new Worker($"worker {++started}");
        return new ViewerLeases<Worker>(3);
    }

    [Fact]
    public void Each_lease_gets_a_worker_of_its_own_up_to_the_limit()
    {
        var leases = Book(out var start);

        var main = leases.Acquire(start);
        var second = leases.Acquire(start);
        var third = leases.Acquire(start);

        Assert.Equal(3, leases.Workers.Count);
        Assert.Equal(3, new[] { main, second, third }.Distinct().Count());
    }

    [Fact]
    public void A_lease_past_the_limit_shares_the_least_busy_worker()
    {
        var leases = Book(out var start);
        var main = leases.Acquire(start);
        var second = leases.Acquire(start);
        var third = leases.Acquire(start);

        var fourth = leases.Acquire(start);   // all tied: the earliest started
        var fifth = leases.Acquire(start);    // main has two now: the next least busy

        Assert.Same(main, fourth);
        Assert.Same(second, fifth);
        Assert.Equal(3, leases.Workers.Count);
        Assert.Equal(2, leases.LeasesOn(main));
        Assert.Equal(1, leases.LeasesOn(third));
    }

    [Fact]
    public void A_shared_worker_stops_only_with_its_last_lease()
    {
        var leases = Book(out var start);
        var main = leases.Acquire(start);
        leases.Acquire(start);
        leases.Acquire(start);
        var shared = leases.Acquire(start);

        Assert.False(leases.Release(shared));
        Assert.Contains(main, leases.Workers);
        Assert.True(leases.Release(main));
        Assert.DoesNotContain(main, leases.Workers);
        Assert.Equal(0, leases.LeasesOn(main));
        Assert.False(leases.Release(main));
    }

    [Fact]
    public void A_released_slot_goes_to_the_next_lease_as_a_new_worker()
    {
        var leases = Book(out var start);
        leases.Acquire(start);
        var second = leases.Acquire(start);
        leases.Acquire(start);

        Assert.True(leases.Release(second));
        var next = leases.Acquire(start);

        Assert.NotSame(second, next);
        Assert.Equal(3, leases.Workers.Count);
        Assert.All(leases.Workers, w => Assert.Equal(1, leases.LeasesOn(w)));
    }

    [Fact]
    public async Task The_pool_starts_a_worker_per_reader_and_stops_it_when_its_last_reader_closes()
    {
        await using var pool = new PdfWorkerPool(new PdfWorkerPoolOptions());
        var main = pool.LeaseViewer();
        var popOut = pool.LeaseViewer();
        var second = pool.LeaseViewer();
        var third = pool.LeaseViewer();

        Assert.NotSame(main.Worker, popOut.Worker);
        Assert.NotSame(popOut.Worker, second.Worker);
        Assert.Same(main.Worker, third.Worker);
        Assert.Equal(3, pool.ViewerWorkerCount);

        var response = await popOut.Worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.KnownText }, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.True(response.Ok, $"{response.Error}: {response.Message}");
        var pid = popOut.Worker.WorkerProcessId;
        Assert.NotNull(pid);

        await popOut.DisposeAsync();
        await popOut.DisposeAsync();   // a second release is ignored

        Assert.Equal(2, pool.ViewerWorkerCount);
        Assert.False(popOut.Worker.IsRunning);
        await Assert.ThrowsAsync<WorkerException>(() => popOut.Worker.SendAsync(new Request { Op = Op.Ping }));
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid.Value));

        await third.DisposeAsync();
        Assert.Equal(2, pool.ViewerWorkerCount);   // main still holds it
        await main.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(0, pool.ViewerWorkerCount);
    }
}

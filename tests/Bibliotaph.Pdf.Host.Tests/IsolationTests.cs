using System.Diagnostics;
using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>
/// The spike's --self-test, as tests: a crash, hang or memory runaway in the worker is contained,
/// the worker dies with the app, can't start other processes, and a page that keeps killing it is poisoned.
/// The job-object tests run on Windows only; CI runs them on windows-latest.
/// </summary>
public sealed class IsolationTests(SyntheticPdfs pdfs)
{
    static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    static async Task<bool> PingAsync(WorkerClient worker) =>
        (await worker.SendAsync(new Request { Op = Op.Ping }, Short)).Ok;

    [Fact]
    public async Task The_worker_dies_when_the_app_dies()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Kill-on-close is a Windows job object feature.");

        var hostPath = Path.Combine(AppContext.BaseDirectory, "testhost", "Bibliotaph.Pdf.Host.TestHost.exe");
        var psi = new ProcessStartInfo(hostPath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add(WorkerOptions.DefaultWorkerPath);
        using var host = Process.Start(psi)!;
        try
        {
            // The test host starts a worker, makes it hang (so it can't notice its pipe closing), and prints its PID.
            var line = await host.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            var workerPid = int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture);
            using var worker = Process.GetProcessById(workerPid);

            host.Kill(entireProcessTree: false);

            Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(10)), "The worker outlived the process that started it.");
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_memory_cap_kills_a_worker_that_exceeds_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The memory cap is a Windows job object limit.");
        await using var worker = new WorkerClient(new WorkerOptions { MemoryLimitBytes = 512L * 1024 * 1024 });
        Assert.True(await PingAsync(worker));

        await Assert.ThrowsAsync<WorkerCrashedException>(() =>
            worker.SendAsync(new Request { Op = Op.AllocateUntilKilled }, TimeSpan.FromSeconds(60)));

        Assert.True(await PingAsync(worker), "The worker did not come back after being killed.");
        Assert.Equal(1, worker.Restarts);
    }

    [Fact]
    public async Task The_worker_cannot_start_a_child_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The one-process limit is a Windows job object limit.");
        await using var worker = new WorkerClient();

        var response = await worker.SendAsync(new Request { Op = Op.SpawnChild }, TimeSpan.FromSeconds(30));

        Assert.False(response.Ok, "The worker started a child process.");
        Assert.Equal(ErrorKind.Security, response.Error);
    }

    [Fact]
    public async Task A_hung_worker_is_killed_at_the_timeout_and_restarted()
    {
        await using var worker = new WorkerClient();
        Assert.True(await PingAsync(worker));
        var generation = worker.Generation;

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<WorkerTimeoutException>(() =>
            worker.SendAsync(new Request { Op = Op.Hang }, TimeSpan.FromSeconds(2)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Timing out took {stopwatch.Elapsed}.");

        Assert.True(await PingAsync(worker));
        Assert.Equal(1, worker.Restarts);
        Assert.Equal(generation + 1, worker.Generation);
    }

    [Fact]
    public async Task A_page_that_kills_the_worker_twice_is_poisoned()
    {
        var poison = new PoisonTracker();
        await using var worker = new WorkerClient(poison: poison);
        var deadlyPage = new Request { Op = Op.Crash, Path = pdfs.KnownText, PageIndex = 1 };

        await Assert.ThrowsAsync<WorkerCrashedException>(() => worker.SendAsync(deadlyPage, Short));
        Assert.False(poison.IsPoisoned(new PageKey(pdfs.KnownText, 1)));
        await Assert.ThrowsAsync<WorkerCrashedException>(() => worker.SendAsync(deadlyPage, Short));

        var refused = await Assert.ThrowsAsync<PagePoisonedException>(() => worker.SendAsync(deadlyPage, Short));
        Assert.Equal(1, refused.Page.PageIndex);
        Assert.False(worker.IsRunning, "A poisoned page must be refused without starting a worker.");

        // The rest of the book is still usable.
        var open = await worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.KnownText }, Short);
        Assert.True(open.Ok, open.Message);
    }
}

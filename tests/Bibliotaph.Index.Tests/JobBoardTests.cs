using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index.Tests;

public sealed class JobBoardTests : IndexFixture
{
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    static readonly Stage[] IndexLane = [Stage.Probe, Stage.Text, Stage.Covers];
    JobBoard _queue = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _queue = new JobBoard(Writer, Database, Clock);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string? StatusOf(long documentId, Stage stage) =>
        Connection.ExecuteScalar<string?>("SELECT status FROM stage_status WHERE document_id = @documentId AND stage = @stage",
            new { documentId, stage = stage.ToString() });

    string? JobStatus(long jobId) => Connection.ExecuteScalar<string?>("SELECT status FROM job WHERE id = @jobId", new { jobId });

    [Fact]
    public async Task Enqueueing_twice_makes_one_job()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);

        Assert.Equal(1, Connection.ExecuteScalar<long>("SELECT count(*) FROM job"));
        Assert.Equal("Pending", StatusOf(1, Stage.Probe));
    }

    [Fact]
    public async Task A_lease_takes_the_most_urgent_job_in_the_lane_and_marks_it_running()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Ocr, priority: 50, ct: Ct);
        await _queue.EnqueueAsync(3, "cc", Stage.Probe, priority: 10, ct: Ct);

        var job = await _queue.LeaseAsync(IndexLane, "test", Lease, Ct);

        Assert.NotNull(job);
        Assert.Equal(3, job.DocumentId);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("leased", JobStatus(job.Id));
        Assert.Equal("Running", StatusOf(3, Stage.Probe));
    }

    [Fact]
    public async Task Completing_a_job_records_the_outcome_and_queues_the_next_stages_at_its_priority()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, priority: 100, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;

        await _queue.CompleteAsync(job, next: [Stage.Text, Stage.Covers], ct: Ct);

        Assert.Equal("done", JobStatus(job.Id));
        Assert.Equal("Complete", StatusOf(1, Stage.Probe));
        Assert.Equal("Pending", StatusOf(1, Stage.Text));
        Assert.Equal([100L, 100L], Connection.Query<long>("SELECT priority FROM job WHERE stage IN ('Text', 'Covers')"));
    }

    [Fact]
    public async Task A_failure_waits_and_retries_then_fails_for_good_after_the_last_attempt()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Text, ct: Ct);

        for (var attempt = 1; attempt <= JobBoard.MaxAttempts; attempt++)
        {
            var job = await _queue.LeaseAsync(IndexLane, "test", Lease, Ct);
            Assert.NotNull(job);
            Assert.Equal(attempt, job.Attempts);
            await _queue.FailAsync(job, "worker crashed", ct: Ct);

            // Not ready again until its backoff has passed.
            Assert.Null(await _queue.LeaseAsync(IndexLane, "test", Lease, Ct));
            Clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal("Failed", StatusOf(1, Stage.Text));
        Assert.Null(await _queue.LeaseAsync(IndexLane, "test", Lease, Ct));
    }

    [Fact]
    public async Task A_failure_without_retry_fails_at_once()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;

        await _queue.FailAsync(job, "not a PDF", retry: false, ct: Ct);

        Assert.Equal("failed", JobStatus(job.Id));
    }

    [Fact]
    public async Task A_blocked_job_waits_for_retry_and_retry_gives_it_a_full_set_of_attempts()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;
        await _queue.BlockAsync(job, "Needs a password", Ct);

        Assert.Equal("Blocked", StatusOf(1, Stage.Probe));
        Assert.Null(await _queue.LeaseAsync(IndexLane, "test", Lease, Ct));

        await _queue.RetryAsync(1, Ct);
        var again = await _queue.LeaseAsync(IndexLane, "test", Lease, Ct);
        Assert.Equal(1, again?.Attempts);
    }

    [Fact]
    public async Task Leases_left_by_a_closed_app_go_back_to_pending_at_startup()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Probe, ct: Ct);
        await _queue.LeaseAsync(IndexLane, "old app", Lease, Ct);

        Assert.Equal(1, await _queue.RecoverLeasesAsync(Ct));

        Assert.Equal(2, Connection.ExecuteScalar<long>("SELECT count(*) FROM job WHERE status = 'pending'"));
        Assert.Equal("Pending", StatusOf(1, Stage.Probe));
    }

    [Fact]
    public async Task Releasing_a_job_does_not_use_up_an_attempt()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;

        await _queue.ReleaseAsync(job, Ct);

        Assert.Equal(1, (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))?.Attempts);
    }

    [Fact]
    public async Task Prioritizing_a_document_puts_its_waiting_jobs_first_and_ends_their_backoff()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Text, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Text, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;
        await _queue.FailAsync(job, "timeout", ct: Ct);

        await _queue.PrioritizeAsync(1, Ct);

        Assert.Equal(1, (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))?.DocumentId);
    }

    [Fact]
    public async Task Next_ready_reports_when_a_waiting_job_comes_off_its_backoff()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Text, ct: Ct);
        var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;
        await _queue.FailAsync(job, "timeout", ct: Ct);

        var next = await _queue.NextReadyAsync(IndexLane, Ct);

        Assert.Equal(Clock.GetUtcNow() + TimeSpan.FromSeconds(30), next);
        Assert.Null(await _queue.NextReadyAsync([Stage.Ocr], Ct));
    }
}

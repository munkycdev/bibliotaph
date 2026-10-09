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
    public async Task A_deferred_job_waits_without_spending_an_attempt_and_wakes_when_another_stage_of_its_book_ends()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Ocr, ct: Ct);
        await _queue.EnqueueAsync(1, "aa", Stage.Classify, ct: Ct);
        var classify = (await _queue.LeaseAsync([Stage.Classify], "test", Lease, Ct))!;

        await _queue.DeferAsync(classify, TimeSpan.FromMinutes(5), "Waiting for its scanned pages to be read.", Ct);

        Assert.Null(await _queue.LeaseAsync([Stage.Classify], "test", Lease, Ct));
        Assert.Equal(0, Connection.ExecuteScalar<long>("SELECT attempts FROM job WHERE id = @Id", new { classify.Id }));
        Assert.Equal("Pending", StatusOf(1, Stage.Classify));

        var ocr = (await _queue.LeaseAsync([Stage.Ocr], "test", Lease, Ct))!;
        await _queue.CompleteAsync(ocr, ct: Ct);

        Assert.Equal(classify.Id, (await _queue.LeaseAsync([Stage.Classify], "test", Lease, Ct))?.Id);
    }

    [Fact]
    public async Task A_stage_blocked_on_the_user_wakes_a_deferred_job_too_and_a_retry_waiting_after_a_failure_keeps_its_wait()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Ocr, ct: Ct);
        await _queue.EnqueueAsync(1, "aa", Stage.Classify, ct: Ct);
        await _queue.EnqueueAsync(1, "aa", Stage.Covers, ct: Ct);
        var covers = (await _queue.LeaseAsync([Stage.Covers], "test", Lease, Ct))!;
        await _queue.FailAsync(covers, "The PDF engine stopped.", ct: Ct);
        var classify = (await _queue.LeaseAsync([Stage.Classify], "test", Lease, Ct))!;
        await _queue.DeferAsync(classify, TimeSpan.FromMinutes(5), ct: Ct);

        var ocr = (await _queue.LeaseAsync([Stage.Ocr], "test", Lease, Ct))!;
        await _queue.BlockAsync(ocr, "OCR is not available.", Ct);

        Assert.Equal(classify.Id, (await _queue.LeaseAsync([Stage.Classify], "test", Lease, Ct))?.Id);
        Assert.Null(await _queue.LeaseAsync([Stage.Covers], "test", Lease, Ct));
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
    public async Task Unblocking_releases_only_the_named_documents_jobs_blocked_for_that_reason()
    {
        const string Unreachable = "Its folder is offline";
        await _queue.EnqueueAsync(1, "aa", Stage.Text, ct: Ct);
        await _queue.EnqueueAsync(1, "aa", Stage.Covers, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Text, ct: Ct);
        await _queue.EnqueueAsync(3, "cc", Stage.Probe, ct: Ct);
        for (var i = 0; i < 4; i++)
        {
            var job = (await _queue.LeaseAsync(IndexLane, "test", Lease, Ct))!;
            await _queue.BlockAsync(job, job.DocumentId == 3 ? "Needs a password" : Unreachable, Ct);
        }

        Assert.Equal([1L, 2L], (await _queue.BlockedForAsync(Unreachable, Ct)).Order());
        Assert.Equal(2, await _queue.UnblockAsync([1, 3], Unreachable, Ct));

        Assert.Equal("Pending", StatusOf(1, Stage.Text));
        Assert.Equal("Pending", StatusOf(1, Stage.Covers));
        Assert.Equal("Blocked", StatusOf(2, Stage.Text));
        Assert.Equal("Blocked", StatusOf(3, Stage.Probe));
        Assert.Equal([2L], await _queue.BlockedForAsync(Unreachable, Ct));
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

    static readonly Stage[] AllLanes = [Stage.Probe, Stage.Text, Stage.Covers, Stage.RuleHints, Stage.Ocr];

    /// <summary>Runs every ready job as the lanes would, queueing what each stage queues; Covers fails for good.</summary>
    async Task RunAllAsync()
    {
        while (await _queue.LeaseAsync(AllLanes, "test", Lease, Ct) is { } job)
        {
            if (job.Stage == Stage.Covers)
            {
                await _queue.FailAsync(job, "no cover", retry: false, Ct);
                continue;
            }
            Stage[] next = job.Stage switch
            {
                Stage.Probe => [Stage.Text, Stage.Covers, Stage.RuleHints],
                Stage.Text => [Stage.Ocr],
                _ => [],
            };
            await _queue.CompleteAsync(job, next: next, ct: Ct);
        }
    }

    (string Stage, string Status)[] Jobs(long documentId) =>
        [.. Connection.Query<(string, string)>("SELECT stage, status FROM job WHERE document_id = @documentId ORDER BY stage", new { documentId })];

    [Fact]
    public async Task Reprocessing_runs_every_stage_of_one_book_again_and_leaves_other_books_alone()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Probe, ct: Ct);
        await RunAllAsync();
        var otherBook = Jobs(2);
        await _queue.EnqueueAsync(3, "cc", Stage.Probe, ct: Ct);

        Assert.True(await _queue.ReprocessAsync(1, ct: Ct));

        // The book starts again at Probe, at the front of the queue with a full set of attempts.
        Assert.Equal([("Probe", "pending")], Jobs(1));
        Assert.Equal("Pending", StatusOf(1, Stage.Probe));
        Assert.Null(StatusOf(1, Stage.Ocr));
        var probe = (await _queue.LeaseAsync(AllLanes, "test", Lease, Ct))!;
        Assert.Equal((1L, Stage.Probe, 1), (probe.DocumentId, probe.Stage, probe.Attempts));

        // Each later stage comes back waiting once the one before it is done, the failed cover included.
        await _queue.CompleteAsync(probe, next: [Stage.Text, Stage.Covers, Stage.RuleHints], ct: Ct);
        foreach (var stage in new[] { Stage.Text, Stage.Covers, Stage.RuleHints }) Assert.Equal("Pending", StatusOf(1, stage));
        Assert.Equal(1, (await _queue.LeaseAsync(AllLanes, "test", Lease, Ct))?.DocumentId);

        // Other books' jobs and statuses are as they were.
        Assert.Equal(otherBook, Jobs(2));
        Assert.Equal("Failed", StatusOf(2, Stage.Covers));
        Assert.Equal("Complete", StatusOf(2, Stage.Ocr));
        Assert.Equal([("Probe", "pending")], Jobs(3));
    }

    [Fact]
    public async Task Reprocessing_waits_while_a_stage_runs_and_can_ask_for_ocr_on_every_page()
    {
        await _queue.EnqueueAsync(1, "aa", Stage.Probe, ct: Ct);
        await _queue.EnqueueAsync(2, "bb", Stage.Probe, ct: Ct);
        await RunAllAsync();
        await Writer.WriteAsync((c, t) => c.Execute(
            "INSERT INTO page (document_id, pdf_page, width_pt, height_pt, needs_ocr) VALUES (1, 0, 612, 792, 0), (1, 1, 612, 792, 1), (2, 0, 612, 792, 1)",
            transaction: t), Ct);
        long Flagged(long documentId) =>
            Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = @documentId AND needs_ocr = 1", new { documentId });

        Assert.False(await _queue.ReprocessAsync(9, ct: Ct));
        Assert.True(await _queue.ReprocessAsync(1, ocrEveryPage: true, Ct));
        Assert.Equal((2L, 1L), (Flagged(1), Flagged(2)));

        // While its Probe runs, asking again changes nothing.
        var running = (await _queue.LeaseAsync(AllLanes, "test", Lease, Ct))!;
        Assert.False(await _queue.ReprocessAsync(1, ct: Ct));
        Assert.Equal(2L, Flagged(1));

        // Plain reprocessing clears the flags, for Text to set again.
        await _queue.CompleteAsync(running, ct: Ct);
        Assert.True(await _queue.ReprocessAsync(1, ct: Ct));
        Assert.Equal((0L, 1L), (Flagged(1), Flagged(2)));
    }
}

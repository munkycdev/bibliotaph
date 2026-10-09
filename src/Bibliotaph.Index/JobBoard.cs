using System.Globalization;
using Bibliotaph.Core;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index;

public sealed record JobRecord(long Id, long DocumentId, string ContentHash, Stage Stage, int StageVersion, int Attempts);

/// <summary>
/// The durable job queue in index.db. One job per document, stage and stage version; leases mark a job
/// as running, and leases left behind by a closed or crashed app go back to pending at startup (A11).
/// Every write goes through the <see cref="IndexWriter"/>, so leasing is atomic without extra locking.
/// Each job change also updates <c>stage_status</c>, which is what the UI reads.
/// </summary>
public sealed class JobBoard(IndexWriter writer, IndexDatabase database, TimeProvider? clock = null)
{
    /// <summary>Attempts before a job fails for good. A job that kills the PDF worker counts as an attempt.</summary>
    public const int MaxAttempts = 3;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    string Now() => Timestamp(_clock.GetUtcNow());

    internal static string Timestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Queues a stage for a document at the current stage version. A job that already exists for that
    /// content, stage and version is left alone, whatever its state, so this is safe to call repeatedly.
    /// </summary>
    public Task EnqueueAsync(long documentId, string contentHash, Stage stage, int priority = 0, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) => Enqueue(c, t, documentId, contentHash, stage, priority, Now()), ct);

    internal static void Enqueue(SqliteConnection c, SqliteTransaction t, long documentId, string contentHash, Stage stage, int priority, string now)
    {
        var inserted = c.Execute(
            """
            INSERT INTO job (document_id, content_hash, stage, stage_version, priority, created_utc)
            VALUES (@documentId, @contentHash, @stage, @version, @priority, @now)
            ON CONFLICT (content_hash, stage, stage_version) DO NOTHING
            """,
            new { documentId, contentHash, stage = stage.ToString(), version = Pipeline.Version(stage), priority, now }, t);
        if (inserted > 0) SetStatus(c, t, documentId, stage, StageStatus.Pending, null, now);
    }

    /// <summary>
    /// Queues <paramref name="stage"/> for every document whose <paramref name="after"/> stage has finished and that has
    /// no job for it at the current version: how a stage added in an update reaches a library indexed before it.
    /// Returns the number queued.
    /// </summary>
    public Task<int> EnqueueMissingAsync(Stage stage, Stage after, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            var missing = c.Query<(long DocumentId, string ContentHash)>(
                """
                SELECT s.document_id, j.content_hash
                FROM stage_status s
                JOIN job j ON j.document_id = s.document_id AND j.stage = @after
                WHERE s.stage = @after AND s.status IN ('Complete', 'Partial')
                  AND NOT EXISTS (SELECT 1 FROM job k WHERE k.content_hash = j.content_hash AND k.stage = @stage AND k.stage_version = @version)
                GROUP BY s.document_id
                """,
                new { stage = stage.ToString(), after = after.ToString(), version = Pipeline.Version(stage) }, t).ToList();
            foreach (var (documentId, contentHash) in missing) Enqueue(c, t, documentId, contentHash, stage, 0, now);
            return missing.Count;
        }, ct);

    /// <summary>Leases the most urgent ready job for one of <paramref name="stages"/>, or returns null when there is none.</summary>
    public Task<JobRecord?> LeaseAsync(IReadOnlyCollection<Stage> stages, string owner, TimeSpan leaseFor, CancellationToken ct = default)
    {
        var names = stages.Select(s => s.ToString()).ToArray();
        return writer.WriteAsync<JobRecord?>((c, t) =>
        {
            var now = Now();
            var row = c.QueryFirstOrDefault<JobRow>(
                """
                SELECT id, document_id AS DocumentId, content_hash AS ContentHash, stage, stage_version AS StageVersion, attempts
                FROM job
                WHERE status = 'pending' AND stage IN @names AND (not_before_utc IS NULL OR not_before_utc <= @now)
                ORDER BY priority DESC, id
                LIMIT 1
                """, new { names, now }, t);
            if (row is null) return null;

            c.Execute(
                "UPDATE job SET status = 'leased', lease_owner = @owner, lease_expires_utc = @expires, attempts = attempts + 1 WHERE id = @Id",
                new { row.Id, owner, expires = Timestamp(_clock.GetUtcNow() + leaseFor) }, t);
            var job = row.ToRecord() with { Attempts = (int)row.Attempts + 1 };
            SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Running, null, now);
            return job;
        }, ct);
    }

    /// <summary>Marks a job done and records how the stage ended (complete, partial or skipped), then queues <paramref name="next"/>.</summary>
    public Task CompleteAsync(JobRecord job, StageStatus outcome = StageStatus.Complete, string? reason = null,
        IReadOnlyCollection<Stage>? next = null, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            c.Execute("UPDATE job SET status = 'done', lease_owner = NULL, lease_expires_utc = NULL, last_error = @reason WHERE id = @Id",
                new { job.Id, reason }, t);
            SetStatus(c, t, job.DocumentId, job.Stage, outcome, reason, now);
            foreach (var stage in next ?? []) Enqueue(c, t, job.DocumentId, job.ContentHash, stage, PriorityOf(c, t, job.Id), now);
        }, ct);

    /// <summary>
    /// Records a failure. Under <see cref="MaxAttempts"/> and with <paramref name="retry"/>, the job waits
    /// (30 s, then 2 min) and runs again; otherwise it fails for good with the reason shown to the user.
    /// </summary>
    public Task FailAsync(JobRecord job, string reason, bool retry = true, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            if (retry && job.Attempts < MaxAttempts)
            {
                var wait = TimeSpan.FromSeconds(30 * Math.Pow(4, job.Attempts - 1));
                c.Execute(
                    "UPDATE job SET status = 'pending', lease_owner = NULL, lease_expires_utc = NULL, last_error = @reason, not_before_utc = @notBefore WHERE id = @Id",
                    new { job.Id, reason, notBefore = Timestamp(_clock.GetUtcNow() + wait) }, t);
                SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Pending, reason, now);
            }
            else
            {
                c.Execute("UPDATE job SET status = 'failed', lease_owner = NULL, lease_expires_utc = NULL, last_error = @reason WHERE id = @Id",
                    new { job.Id, reason }, t);
                SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Failed, reason, now);
            }
        }, ct);

    /// <summary>Parks a job until the user does something, such as supplying a password. <see cref="RetryAsync"/> releases it.</summary>
    public Task BlockAsync(JobRecord job, string reason, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            c.Execute("UPDATE job SET status = 'blocked', lease_owner = NULL, lease_expires_utc = NULL, last_error = @reason, attempts = attempts - 1 WHERE id = @Id",
                new { job.Id, reason }, t);
            SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Blocked, reason, Now());
        }, ct);

    /// <summary>Puts a leased job back without counting the attempt, for a job interrupted by the app closing or a lane pausing.</summary>
    public Task ReleaseAsync(JobRecord job, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            c.Execute("UPDATE job SET status = 'pending', lease_owner = NULL, lease_expires_utc = NULL, attempts = attempts - 1 WHERE id = @Id",
                new { job.Id }, t);
            SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Pending, null, Now());
        }, ct);

    /// <summary>
    /// Puts a leased job back to wait at least <paramref name="wait"/>, without counting the attempt: for a stage that
    /// has to wait for another, as Classify waits for a book's OCR. <paramref name="reason"/> shows as its status.
    /// </summary>
    public Task DeferAsync(JobRecord job, TimeSpan wait, string? reason = null, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            c.Execute(
                "UPDATE job SET status = 'pending', lease_owner = NULL, lease_expires_utc = NULL, attempts = attempts - 1, not_before_utc = @notBefore WHERE id = @Id",
                new { job.Id, notBefore = Timestamp(_clock.GetUtcNow() + wait) }, t);
            SetStatus(c, t, job.DocumentId, job.Stage, StageStatus.Pending, reason, Now());
        }, ct);

    /// <summary>Returns every leased job to pending. Run once at startup, before any lane leases work (A11).</summary>
    public Task<int> RecoverLeasesAsync(CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            var leased = c.Query<JobRow>(
                "SELECT id, document_id AS DocumentId, content_hash AS ContentHash, stage, stage_version AS StageVersion, attempts FROM job WHERE status = 'leased'",
                transaction: t).ToList();
            // The attempt counts: if the job crashed the app, it should not loop forever.
            c.Execute("UPDATE job SET status = 'pending', lease_owner = NULL, lease_expires_utc = NULL WHERE status = 'leased'", transaction: t);
            foreach (var job in leased) SetStatus(c, t, job.DocumentId, Enum.Parse<Stage>(job.Stage), StageStatus.Pending, null, now);
            return leased.Count;
        }, ct);

    /// <summary>Moves a document's waiting jobs to the front of the queue, as when the user opens it.</summary>
    public Task PrioritizeAsync(long documentId, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
            c.Execute("UPDATE job SET priority = 100, not_before_utc = NULL WHERE document_id = @documentId AND status = 'pending'", new { documentId }, t), ct);

    /// <summary>Gives a document's failed and blocked jobs another full set of attempts.</summary>
    public Task RetryAsync(long documentId, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            var stages = c.Query<string>("SELECT stage FROM job WHERE document_id = @documentId AND status IN ('failed', 'blocked')", new { documentId }, t).ToList();
            c.Execute(
                "UPDATE job SET status = 'pending', attempts = 0, not_before_utc = NULL, last_error = NULL WHERE document_id = @documentId AND status IN ('failed', 'blocked')",
                new { documentId }, t);
            foreach (var stage in stages) SetStatus(c, t, documentId, Enum.Parse<Stage>(stage), StageStatus.Pending, null, now);
        }, ct);

    /// <summary>
    /// Runs <paramref name="stage"/> again for every document that has finished it at the current version, as when the
    /// vocabulary changes and the hints from names need reading again. Running and waiting jobs are left alone; the
    /// stages after it are not queued again. Returns the number queued.
    /// </summary>
    public Task<int> RerunAsync(Stage stage, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            var args = new { stage = stage.ToString(), version = Pipeline.Version(stage) };
            var documents = c.Query<long>(
                "SELECT document_id FROM job WHERE stage = @stage AND stage_version = @version AND status IN ('done', 'failed')", args, t).ToList();
            c.Execute(
                """
                UPDATE job SET status = 'pending', attempts = 0, not_before_utc = NULL, last_error = NULL
                WHERE stage = @stage AND stage_version = @version AND status IN ('done', 'failed')
                """, args, t);
            foreach (var documentId in documents) SetStatus(c, t, documentId, stage, StageStatus.Pending, null, now);
            return documents.Count;
        }, ct);

    /// <summary>Documents with a job blocked for exactly <paramref name="reason"/>.</summary>
    public async Task<IReadOnlyList<long>> BlockedForAsync(string reason, CancellationToken ct = default)
    {
        await using var c = database.OpenRead();
        return [.. await c.QueryAsync<long>(new CommandDefinition(
            "SELECT DISTINCT document_id FROM job WHERE status = 'blocked' AND last_error = @reason", new { reason }, cancellationToken: ct))];
    }

    /// <summary>
    /// Releases the jobs of <paramref name="documentIds"/> blocked for <paramref name="reason"/>, once whatever blocked
    /// them has cleared by itself, such as an offline folder coming back. Unlike <see cref="RetryAsync"/>, attempts stand.
    /// </summary>
    public Task<int> UnblockAsync(IReadOnlyCollection<long> documentIds, string reason, CancellationToken ct = default) =>
        writer.WriteAsync((c, t) =>
        {
            var now = Now();
            var released = 0;
            foreach (var documentId in documentIds)
            {
                var stages = c.Query<string>(
                    "SELECT stage FROM job WHERE document_id = @documentId AND status = 'blocked' AND last_error = @reason",
                    new { documentId, reason }, t).ToList();
                c.Execute(
                    "UPDATE job SET status = 'pending', last_error = NULL WHERE document_id = @documentId AND status = 'blocked' AND last_error = @reason",
                    new { documentId, reason }, t);
                foreach (var stage in stages) SetStatus(c, t, documentId, Enum.Parse<Stage>(stage), StageStatus.Pending, null, now);
                released += stages.Count;
            }
            return released;
        }, ct);

    /// <summary>The earliest time a waiting job becomes ready, so an idle lane knows when to look again.</summary>
    public async Task<DateTimeOffset?> NextReadyAsync(IReadOnlyCollection<Stage> stages, CancellationToken ct = default)
    {
        await using var c = database.OpenRead();
        var next = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT min(coalesce(not_before_utc, created_utc)) FROM job WHERE status = 'pending' AND stage IN @names",
            new { names = stages.Select(s => s.ToString()).ToArray() }, cancellationToken: ct));
        return next is null ? null : DateTimeOffset.Parse(next, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    static int PriorityOf(SqliteConnection c, SqliteTransaction t, long jobId) =>
        c.ExecuteScalar<int>("SELECT priority FROM job WHERE id = @jobId", new { jobId }, t);

    internal static void SetStatus(SqliteConnection c, SqliteTransaction t, long documentId, Stage stage, StageStatus status, string? reason, string now) =>
        c.Execute(
            """
            INSERT INTO stage_status (document_id, stage, status, stage_version, reason, updated_utc)
            VALUES (@documentId, @stage, @status, @version, @reason, @now)
            ON CONFLICT (document_id, stage) DO UPDATE SET
                status = excluded.status, stage_version = excluded.stage_version, reason = excluded.reason, updated_utc = excluded.updated_utc
            """,
            new { documentId, stage = stage.ToString(), status = status.ToString(), version = Pipeline.Version(stage), reason, now }, t);

    sealed record JobRow(long Id, long DocumentId, string ContentHash, string Stage, long StageVersion, long Attempts)
    {
        public JobRecord ToRecord() => new(Id, DocumentId, ContentHash, Enum.Parse<Core.Stage>(Stage), (int)StageVersion, (int)Attempts);
    }
}

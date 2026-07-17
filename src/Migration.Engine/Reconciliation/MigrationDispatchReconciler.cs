using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Models.ColdStorage;

namespace Migration.Engine.Reconciliation;

/// <summary>What the dispatch reconciler should do with a single item.</summary>
public enum DispatchAction
{
    /// <summary>Leave the item alone.</summary>
    None,
    /// <summary>Re-publish the item's bus message (it was never sent or is stale).</summary>
    ReDrive,
    /// <summary>Fail the item: it sat Queued longer than the max and never processed.</summary>
    FailGaveUp,
    /// <summary>Fail the item: an active operation made no progress (stalled/crashed worker).</summary>
    FailStalled,
}

/// <summary>Tunables that drive the dispatch decision, resolved from <see cref="Config"/>.</summary>
public readonly record struct DispatchThresholds(int EnqueueGraceSeconds, int MaxQueuedMinutes, int StallMinutes);

/// <summary>Result of one reconciler pass, for logging.</summary>
public readonly record struct DispatchReconcileSummary(int ReDriven, int FailedGaveUp, int FailedStalled, int EmptyJobsClosed, int JobsFinalized, int ThrottleRetried)
{
    public bool HasWork => ReDriven > 0 || FailedGaveUp > 0 || FailedStalled > 0 || EmptyJobsClosed > 0 || JobsFinalized > 0 || ThrottleRetried > 0;
}

/// <summary>
/// The safety net that guarantees a migration can never silently freeze. Each pass:
///   1. re-publishes <c>Queued</c> items whose Service Bus message was never sent or
///      is stale (e.g. the start request was cancelled mid-publish), and fails those
///      that sat <c>Queued</c> past <see cref="Config.ColdStorageMaxQueuedMinutes"/>
///      (source left intact);
///   2. re-drives <c>RetryScheduled</c> items whose transient/throttle backoff window
///      (<see cref="ThrottleBackoff"/>) has elapsed, so a throttled item resumes
///      automatically after waiting;
///   3. fails active items with no status change for <see cref="Config.ColdStorageStallMinutes"/>
///      (a crashed/stalled worker), so the job reaches a terminal state instead of
///      hanging forever;
///   4. closes non-terminal jobs that never produced any items;
///   5. finalizes non-terminal jobs whose items are all terminal but whose rollup
///      status was never written (e.g. a transient DB error lost the final rollup),
///      so a completed job can never display an in-progress status forever.
/// The per-item decision is the pure, unit-tested <see cref="Decide"/>.
/// </summary>
public sealed class MigrationDispatchReconciler : BaseComponent
{
    private const int MaxItemsPerPass = 500;

    private static readonly MigrationLifecycleStatus[] TerminalStatuses =
    [
        MigrationLifecycleStatus.ColdStorageMigrationCompleted,
        MigrationLifecycleStatus.RestoreCompleted,
        MigrationLifecycleStatus.ValidationFailed,
        MigrationLifecycleStatus.CopyToColdStorageFailed,
        MigrationLifecycleStatus.DeleteFailed,
        MigrationLifecycleStatus.PlaceholderFailed,
        MigrationLifecycleStatus.RestoreFailed,
        MigrationLifecycleStatus.PlaceholderRemoveFailed,
        MigrationLifecycleStatus.CompletedWithWarning,
        MigrationLifecycleStatus.Cancelled,
        MigrationLifecycleStatus.Skipped,
    ];

    private readonly IColdStorageQueuePublisher _publisher;

    public MigrationDispatchReconciler(Config config, ILogger logger, IColdStorageQueuePublisher publisher)
        : base(config, logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <summary>
    /// Pure decision for one item, given the current time and thresholds. Kept free
    /// of I/O so it can be unit-tested exhaustively.
    /// </summary>
    public static DispatchAction Decide(
        MigrationLifecycleStatus status,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        DateTime? lastEnqueuedAtUtc,
        DateTime nowUtc,
        DispatchThresholds thresholds)
    {
        if (status.IsTerminal())
        {
            return DispatchAction.None;
        }

        if (status == MigrationLifecycleStatus.Queued)
        {
            if ((nowUtc - createdAtUtc).TotalMinutes >= thresholds.MaxQueuedMinutes)
            {
                return DispatchAction.FailGaveUp;
            }
            if (lastEnqueuedAtUtc is null)
            {
                // Never sent — re-drive once it's older than the grace so we don't
                // race a publish that's still in flight from the original request.
                return (nowUtc - createdAtUtc).TotalSeconds >= thresholds.EnqueueGraceSeconds
                    ? DispatchAction.ReDrive
                    : DispatchAction.None;
            }
            return (nowUtc - lastEnqueuedAtUtc.Value).TotalSeconds >= thresholds.EnqueueGraceSeconds
                ? DispatchAction.ReDrive
                : DispatchAction.None;
        }

        // Active, non-terminal, non-Queued: stalled if nothing has advanced it.
        return (nowUtc - updatedAtUtc).TotalMinutes >= thresholds.StallMinutes
            ? DispatchAction.FailStalled
            : DispatchAction.None;
    }

    public DispatchThresholds Thresholds() => new(
        _config.ColdStorageEnqueueGraceSeconds > 0 ? _config.ColdStorageEnqueueGraceSeconds : 120,
        _config.ColdStorageMaxQueuedMinutes > 0 ? _config.ColdStorageMaxQueuedMinutes : 1440,
        _config.ColdStorageStallMinutes > 0 ? _config.ColdStorageStallMinutes : 30);

    public async Task<DispatchReconcileSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var thresholds = Thresholds();

        using var db = new SPOColdStorageDbContext(_config);
        var writer = new JobStatusWriter(db, _logger);

        var reDriven = 0;
        var gaveUp = 0;
        var stalled = 0;
        var emptyJobs = 0;
        var jobsFinalized = 0;
        var throttleRetried = 0;

        // 1) Queued items whose message was never sent or is stale.
        var enqueueCutoff = now.AddSeconds(-thresholds.EnqueueGraceSeconds);
        var queued = await db.MigrationJobItems
            .Include(i => i.Job)
            .Where(i => i.Status == MigrationLifecycleStatus.Queued
                        && i.CreatedAt < enqueueCutoff
                        && (i.LastEnqueuedAt == null || i.LastEnqueuedAt < enqueueCutoff))
            .OrderBy(i => i.LastEnqueuedAt)
            .ThenBy(i => i.CreatedAt)
            .Take(MaxItemsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var toPublish = new List<ColdStorageBusEnvelope>();
        var reDriveItems = new List<MigrationJobItem>();
        foreach (var item in queued)
        {
            var action = Decide(item.Status, item.CreatedAt, item.UpdatedAt, item.LastEnqueuedAt, now, thresholds);
            if (action == DispatchAction.FailGaveUp)
            {
                await writer.TransitionAsync(
                    item.ItemId,
                    MigrationLifecycleStatus.CopyToColdStorageFailed,
                    $"Not processed within {thresholds.MaxQueuedMinutes} min of being queued; giving up. The source file was left untouched in SharePoint.",
                    level: LogLevel.Error,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                gaveUp++;
            }
            else if (action == DispatchAction.ReDrive)
            {
                var envelope = ColdStorageBusMessageFactory.BuildEnvelopeFromItem(item, item.Job);
                if (envelope is null)
                {
                    _logger.LogWarning("Dispatch reconciler: item {ItemId} can't be rebuilt into a valid envelope; skipping.", item.ItemId);
                    continue;
                }
                toPublish.Add(envelope);
                reDriveItems.Add(item);
            }
        }

        if (toPublish.Count > 0)
        {
            try
            {
                await _publisher.PublishManyAsync(toPublish, cancellationToken).ConfigureAwait(false);
                foreach (var item in reDriveItems)
                {
                    item.LastEnqueuedAt = now;
                    item.UpdatedAt = now;
                }
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                reDriven = reDriveItems.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch reconciler failed to re-publish {Count} item(s); will retry next pass.", toPublish.Count);
            }
        }

        // 2) RetryScheduled items whose transient/throttle backoff window has elapsed:
        //    re-publish them so a throttled operation resumes automatically after waiting.
        //    The backoff is UpdatedAt (when it entered RetryScheduled) + ThrottleBackoff of
        //    the attempt count; computed in memory because 2^n isn't SQL-translatable.
        var retryCandidates = await db.MigrationJobItems
            .Include(i => i.Job)
            .Where(i => i.Status == MigrationLifecycleStatus.RetryScheduled)
            .OrderBy(i => i.UpdatedAt)
            .Take(MaxItemsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var retryToPublish = new List<ColdStorageBusEnvelope>();
        var retryItems = new List<MigrationJobItem>();
        foreach (var item in retryCandidates)
        {
            var dueAt = item.UpdatedAt.AddSeconds(ThrottleBackoff.SecondsFor(item.Attempts, _config));
            if (now < dueAt)
            {
                continue;
            }
            var envelope = ColdStorageBusMessageFactory.BuildEnvelopeFromItem(item, item.Job);
            if (envelope is null)
            {
                _logger.LogWarning("Dispatch reconciler: RetryScheduled item {ItemId} can't be rebuilt into a valid envelope; skipping.", item.ItemId);
                continue;
            }
            retryToPublish.Add(envelope);
            retryItems.Add(item);
        }
        if (retryToPublish.Count > 0)
        {
            // Claim first: flip RetryScheduled -> Queued (+ stamp LastEnqueuedAt) and SAVE
            // before publishing. That way a worker that picks the message up can't have its
            // progress clobbered by a late status write, and a second reconciler pass won't
            // re-select these rows (they're no longer RetryScheduled). If the publish then
            // fails, the items are Queued with LastEnqueuedAt=now and the Queued re-drive
            // pass resends them after the grace window — they're never lost.
            foreach (var item in retryItems)
            {
                item.Status = MigrationLifecycleStatus.Queued;
                item.LastEnqueuedAt = now;
                item.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _publisher.PublishManyAsync(retryToPublish, cancellationToken).ConfigureAwait(false);
                throttleRetried = retryItems.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch reconciler failed to re-drive {Count} RetryScheduled item(s); the Queued re-drive will resend them.", retryToPublish.Count);
            }
        }

        // 3) Active items with no progress for too long => stalled/crashed worker.
        //    RetryScheduled is excluded — it is intentionally waiting out its backoff.
        var stallCutoff = now.AddMinutes(-thresholds.StallMinutes);
        var stalledItems = await db.MigrationJobItems
            .Where(i => !TerminalStatuses.Contains(i.Status)
                        && i.Status != MigrationLifecycleStatus.Queued
                        && i.Status != MigrationLifecycleStatus.RetryScheduled
                        && i.UpdatedAt < stallCutoff)
            .OrderBy(i => i.UpdatedAt)
            .Take(MaxItemsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in stalledItems)
        {
            await writer.TransitionAsync(
                item.ItemId,
                StalledFailureStatus(item.Status),
                $"No progress for over {thresholds.StallMinutes} min (worker stalled or crashed mid-operation). Marked failed so the job can complete; re-queue to retry.",
                level: LogLevel.Error,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            stalled++;
        }

        // 4) Non-terminal jobs that never produced any items (enumeration failed
        //    before any row was saved) — otherwise they show 'Queued' forever.
        var emptyJobList = await db.MigrationJobs
            .Where(j => !TerminalStatuses.Contains(j.Status)
                        && j.CreatedAt < stallCutoff
                        && !j.Items.Any())
            .Take(MaxItemsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var job in emptyJobList)
        {
            job.Status = MigrationLifecycleStatus.CompletedWithWarning;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            emptyJobs++;
        }
        if (emptyJobs > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // 5) Non-terminal jobs whose items are ALL terminal but whose rollup status
        //    was never finalized (the final rollup write was lost to a transient DB
        //    error). Recompute so the job reaches its terminal state instead of showing
        //    an in-progress status forever. Guarded by the enqueue grace so we don't
        //    race a worker that is about to write the same rollup. RecomputeJobRollupAsync
        //    saves per job.
        var rollupCutoff = now.AddSeconds(-thresholds.EnqueueGraceSeconds);
        var stuckJobIds = await db.MigrationJobs
            .Where(j => !TerminalStatuses.Contains(j.Status)
                        && j.UpdatedAt < rollupCutoff
                        && j.Items.Any()
                        && !j.Items.Any(i => !TerminalStatuses.Contains(i.Status)))
            .OrderBy(j => j.UpdatedAt)
            .Select(j => j.JobId)
            .Take(MaxItemsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var jobId in stuckJobIds)
        {
            await writer.RecomputeJobRollupAsync(jobId, cancellationToken).ConfigureAwait(false);
            jobsFinalized++;
        }

        return new DispatchReconcileSummary(reDriven, gaveUp, stalled, emptyJobs, jobsFinalized, throttleRetried);
    }

    private static MigrationLifecycleStatus StalledFailureStatus(MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.RestoreInProgress
            or MigrationLifecycleStatus.RestoredToSharePoint
            or MigrationLifecycleStatus.PostRestoreValidation
            or MigrationLifecycleStatus.PlaceholderRemoving
            or MigrationLifecycleStatus.RestoreFailed => MigrationLifecycleStatus.RestoreFailed,
        // Past the source-delete point (copy already succeeded): don't label these
        // "copy failed", which implies the source is intact. The orphan reconciler
        // handles a blob whose placeholder was never created.
        MigrationLifecycleStatus.DeletePending
            or MigrationLifecycleStatus.PlaceholderCreating
            or MigrationLifecycleStatus.PlaceholderFailed => MigrationLifecycleStatus.PlaceholderFailed,
        _ => MigrationLifecycleStatus.CopyToColdStorageFailed,
    };
}

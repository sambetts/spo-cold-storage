using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Models.ColdStorage;

namespace Migration.Engine.Providers;

/// <summary>
/// Provider-neutral archive pipeline: copies one item from an <see cref="ISourceStore"/> to an
/// <see cref="IColdStore"/> and replaces it with a placeholder pointer, enforcing the #1 invariant
/// — the source is never deleted until a byte-verified copy exists in cold storage. It knows
/// nothing about SharePoint or Azure; swap either adaptor (or an in-memory one for tests) and the
/// same orchestration and guards apply.
///
/// The step order IS the safety guarantee (validate → eligibility → hold → copy → verify → delete →
/// placeholder); do not reorder. Every failure routes through <see cref="StepFailureHandler"/> so a
/// throttle/transient blip parks for automatic retry instead of failing terminally, and destructive
/// steps (delete) are gated on prior success plus a re-checked lifecycle guard.
/// </summary>
public sealed class MigratePipeline
{
    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly IJobStatusWriter _statusWriter;
    private readonly ISourceStore _source;
    private readonly IColdStore _cold;
    private readonly IArchiveEligibilityEvaluator _eligibility;
    private readonly StepFailureHandler _failures;

    public MigratePipeline(
        Config config,
        ILogger logger,
        IJobStatusWriter statusWriter,
        ISourceStore source,
        IColdStore cold,
        IArchiveEligibilityEvaluator eligibility)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _cold = cold ?? throw new ArgumentNullException(nameof(cold));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _failures = new StepFailureHandler(config, logger, statusWriter);
    }

    /// <summary>
    /// Archives one item end-to-end. Returns true if archived (or short-circuited because it was
    /// already handled/skipped), false if a step failed and the message should be retried.
    /// </summary>
    public async Task<bool> ProcessAsync(MigrateRequest req, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        // FAILSAFE / resumability: a prior attempt that already copied + deleted the source but
        // crashed before the placeholder must NOT re-read a source that no longer exists (which
        // would look like a copy failure). Resume by recreating the placeholder from the persisted
        // blob coordinates.
        var priorItem = await _statusWriter.FindItemAsync(req.ItemId, cancellationToken).ConfigureAwait(false);
        if (priorItem is not null && priorItem.SourceDeletedAt is not null && !priorItem.Status.IsTerminal())
        {
            return await ResumeAfterSourceDeletedAsync(req, priorItem, cancellationToken).ConfigureAwait(false);
        }

        // --- Validating ---------------------------------------------------
        await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.Validating,
            $"Validating '{req.Source.DisplayPath}'.", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(req.Source.ItemPath) || string.IsNullOrWhiteSpace(req.Cold.Container))
        {
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.ValidationFailed,
                "Invalid item info (missing source path or cold container).", level: LogLevel.Warning, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        // --- Eligibility gate ---------------------------------------------
        var eligibility = await _eligibility.EvaluateAsync(new ArchiveCandidate
        {
            ServerRelativeUrl = req.Source.ItemPath,
            SiteUrl = req.Source.StoreUrl,
            WebUrl = req.Source.WebUrl,
            FileSizeBytes = req.SourceSizeHint,
            LastModified = req.SourceLastModifiedUtc,
            DriveId = req.DriveId,
            GraphItemId = req.GraphItemId,
        }, cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsEligible)
        {
            _logger.LogInformation("Skipping '{Path}': {Reason}", req.Source.DisplayPath, eligibility.SkipReason);
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.Skipped,
                $"Skipped: {eligibility.SkipReason}", level: LogLevel.Information, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        // --- Compliance-hold gate -----------------------------------------
        // Runs before any read so held content never leaves the source store.
        HoldStatus hold;
        try
        {
            hold = await _source.CheckHoldAsync(req.Source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A hold check that itself throws is treated like any other step failure (parks on
            // transient). It runs before any copy/delete, so the source is never at risk.
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.ValidationFailed,
                $"Compliance-hold check failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (hold.IsOnHold)
        {
            _logger.LogInformation("Skipping '{Path}': {Reason}", req.Source.DisplayPath, hold.Reason);
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.Skipped,
                $"Skipped: {hold.Reason}", level: LogLevel.Information, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        // --- MigrationInProgress: conflict-by-date + copy -----------------
        await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.MigrationInProgress,
            "Starting copy to cold storage.", cancellationToken: cancellationToken).ConfigureAwait(false);

        long size;
        string md5Base64;
        var copySkipped = false;
        try
        {
            var coldInfo = await _cold.GetInfoAsync(req.Cold, cancellationToken).ConfigureAwait(false);
            var decision = coldInfo.Exists
                ? MigrateConflictResolver.Decide(req.SourceLastModifiedUtc, coldInfo.ArchivedSourceLastModifiedUtc)
                : BlobConflictDecision.Copy;

            if (decision is BlobConflictDecision.SkipSameVersion or BlobConflictDecision.DestinationNewer)
            {
                // Already archived — skip the (throttle-heavy) re-copy but still place a placeholder,
                // using the existing archive's size/hash for the delete-safety check + metadata.
                size = coldInfo.Length;
                md5Base64 = coldInfo.ContentMd5Base64 ?? string.Empty;
                copySkipped = true;
                var why = decision == BlobConflictDecision.DestinationNewer
                    ? $"Already in cold storage (archive source-modified {coldInfo.ArchivedSourceLastModifiedUtc:u} vs source {req.SourceLastModifiedUtc.ToUniversalTime():u}); keeping the existing archive and placing a placeholder rather than overwriting."
                    : "Already in cold storage with the same version; skipping the copy and replacing the source with a placeholder.";
                await _statusWriter.LogAsync(req.JobId, req.ItemId, MigrationLifecycleStatus.MigrationInProgress, LogLevel.Information, why, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var content = await _source.ReadContentAsync(req.Source, cancellationToken).ConfigureAwait(false);
                size = content.Length;
                md5Base64 = content.ContentMd5Base64;
                await _cold.WriteAsync(req.Cold, content, new ColdWriteMetadata
                {
                    OriginalStoreUrl = req.Source.StoreUrl,
                    OriginalWebUrl = req.Source.WebUrl,
                    OriginalItemPath = req.Source.ItemPath,
                    OriginalFileName = Path.GetFileName(req.Source.ItemPath),
                    SourceLastModifiedUtc = req.SourceLastModifiedUtc.ToUniversalTime(),
                    ContentMd5Base64 = md5Base64,
                    JobId = req.JobId,
                    RequestedByUpn = req.RequestedByUpn,
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // CRITICAL: the source is never even reached for deletion on this path.
            _logger.LogError(ex, "Copy to cold storage failed for '{Path}'.", req.Source.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.CopyToColdStorageFailed, $"Copy failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // --- Copied / PostCopyValidation ----------------------------------
        var blobUrl = _cold.GetObjectUrl(req.Cold);
        await _statusWriter.RecordCopySuccessAsync(req.ItemId, req.Cold.Container, req.Cold.Path, blobUrl, md5Base64, cancellationToken).ConfigureAwait(false);
        await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.PostCopyValidation,
            "Verifying copy in cold storage.", cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            // Nothing new was written when a same-version copy was skipped; the existing archive was
            // verified when it was first written.
            if (!copySkipped)
            {
                await _cold.VerifyAsync(req.Cold, size, md5Base64, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-copy validation failed for '{Path}'.", req.Source.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.CopyToColdStorageFailed, $"Post-copy validation failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // --- DeletePending: delete the source (only now) ------------------
        await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.DeletePending,
            "Removing source item.", cancellationToken: cancellationToken).ConfigureAwait(false);

        string? originalCreatedBy = null, originalModifiedBy = null;
        DateTime? originalCreated = null, originalModified = null;
        try
        {
            var info = await _source.GetItemAsync(req.Source, cancellationToken).ConfigureAwait(false);
            if (info.Exists)
            {
                // FAILSAFE: the source must still be byte-for-byte what we archived. Never delete on a
                // length mismatch — the file changed after the copy (or the copy was short).
                if (info.Length != size)
                {
                    _logger.LogError("Source '{Path}' length {Actual} no longer matches the archived copy ({Copied}); refusing to delete.", req.Source.DisplayPath, info.Length, size);
                    await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                        $"Source length {info.Length} no longer matches the archived copy ({size}); not deleting.",
                        level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return false;
                }

                originalCreatedBy = info.CreatedBy;
                originalModifiedBy = info.ModifiedBy;
                originalCreated = info.CreatedUtc;
                originalModified = info.LastModifiedUtc;

                if (info.IsLocked)
                {
                    await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.DeleteFailed,
                        $"Source item '{req.Source.DisplayPath}' is locked/checked out and cannot be deleted ({info.LockReason}).",
                        level: LogLevel.Warning, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return false;
                }

                // FAILSAFE: re-read the persisted status and refuse to delete unless the lifecycle
                // explicitly permits it (guards a reordered pipeline / a concurrent cancel).
                var itemNow = await _statusWriter.FindItemAsync(req.ItemId, cancellationToken).ConfigureAwait(false);
                if (itemNow is null || !itemNow.Status.SourceDeleteAllowed())
                {
                    _logger.LogError("Refusing to delete source '{Path}': item status {Status} does not permit deletion.", req.Source.DisplayPath, itemNow?.Status);
                    await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                        "Source delete blocked by lifecycle guard (status did not permit deletion).",
                        level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return false;
                }

                await _source.DeleteAsync(req.Source, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Already gone — a prior/concurrent attempt (or an external delete) removed it. We
                // only reach here after a verified copy, so the archival goal is met. Treat as a
                // successful delete (never re-fail an already-archived item). Nothing new is deleted.
                _logger.LogWarning("Source '{Path}' already gone at the delete step; treating as already deleted.", req.Source.DisplayPath);
            }

            await _statusWriter.RecordSourceMetadataAsync(req.ItemId, originalCreatedBy, originalModifiedBy, originalCreated, originalModified, cancellationToken).ConfigureAwait(false);
            await _statusWriter.RecordSourceDeletedAsync(req.ItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete source '{Path}'.", req.Source.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.DeleteFailed, $"Failed to delete source: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // --- PlaceholderCreating ------------------------------------------
        try
        {
            var metadata = BuildPlaceholderMetadata(req, size, md5Base64, blobUrl, originalCreatedBy, originalModifiedBy, originalCreated, originalModified);
            var placeholderPath = await _source.WritePointerAsync(req.Source, metadata, BuildUserFacingUrl(req.ItemId), cancellationToken).ConfigureAwait(false);
            await _statusWriter.RecordPlaceholderCreatedAsync(req.ItemId, placeholderPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create placeholder for '{Path}'.", req.Source.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.PlaceholderFailed, $"Failed to create placeholder: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Recovery path for an item whose source was already deleted by a prior attempt that didn't
    /// finish. Verifies the archive still exists and (re)creates the placeholder rather than
    /// re-copying a source that no longer exists.
    /// </summary>
    private async Task<bool> ResumeAfterSourceDeletedAsync(MigrateRequest req, Entities.DBEntities.ColdStorage.MigrationJobItem item, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Item {ItemId} ('{Path}') already had its source deleted in a prior run; resuming without re-reading the source.", req.ItemId, req.Source.DisplayPath);

        if (item.PlaceholderCreatedAt is not null && !string.IsNullOrEmpty(item.PlaceholderServerRelativeUrl))
        {
            _logger.LogInformation("Item {ItemId} already has a placeholder; treating as complete.", req.ItemId);
            return true;
        }
        if (string.IsNullOrEmpty(item.BlobContainerName) || string.IsNullOrEmpty(item.BlobPath))
        {
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                "Resume failed: source was deleted but the archived coordinates are missing.",
                level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            var coldKey = new ColdStorageKey(item.BlobContainerName, item.BlobPath);
            var info = await _cold.GetInfoAsync(coldKey, cancellationToken).ConfigureAwait(false);
            if (!info.Exists)
            {
                _logger.LogError("Resume for item {ItemId}: source is deleted AND the archive '{Container}/{Path}' is missing.", req.ItemId, coldKey.Container, coldKey.Path);
                await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                    "Resume failed: the archive is missing. Manual recovery required.",
                    level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }

            var metadata = BuildPlaceholderMetadata(req, item.FileSize, item.ContentMd5Base64 ?? string.Empty, item.BlobUrl ?? _cold.GetObjectUrl(coldKey),
                item.OriginalCreatedBy, item.OriginalModifiedBy, item.OriginalCreated, item.SourceLastModified);
            var placeholderPath = await _source.WritePointerAsync(req.Source, metadata, BuildUserFacingUrl(req.ItemId), cancellationToken).ConfigureAwait(false);
            await _statusWriter.RecordPlaceholderCreatedAsync(req.ItemId, placeholderPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume: failed to recreate placeholder for '{Path}'.", req.Source.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.PlaceholderFailed, $"Resume: failed to recreate placeholder: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private PlaceholderFileMetadata BuildPlaceholderMetadata(
        MigrateRequest req, long size, string md5Base64, string blobUrl,
        string? createdBy, string? modifiedBy, DateTime? created, DateTime? modified)
        => new()
        {
            OriginalSiteUrl = req.Source.StoreUrl,
            OriginalWebUrl = req.Source.WebUrl,
            OriginalServerRelativeUrl = req.Source.ItemPath,
            OriginalFileName = Path.GetFileName(req.Source.ItemPath),
            OriginalFileSize = size,
            OriginalLastModified = modified ?? req.SourceLastModifiedUtc,
            OriginalCreatedBy = createdBy ?? string.Empty,
            OriginalModifiedBy = modifiedBy ?? string.Empty,
            OriginalCreated = created ?? req.SourceCreatedUtc ?? DateTime.MinValue,
            ContainerName = req.Cold.Container,
            BlobPath = req.Cold.Path,
            BlobUrl = blobUrl,
            ContentMd5Base64 = md5Base64,
            MigratedAt = DateTime.UtcNow,
            JobId = req.JobId,
        };

    private string? BuildUserFacingUrl(Guid itemId)
        => string.IsNullOrWhiteSpace(_config.AppBaseUrl)
            ? null
            : $"{_config.AppBaseUrl.TrimEnd('/')}/cold-storage/download/{itemId}";
}

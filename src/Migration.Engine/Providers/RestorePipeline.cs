using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Models.ColdStorage;

namespace Migration.Engine.Providers;

/// <summary>
/// Provider-neutral restore pipeline: reads the placeholder pointer, streams the archived content
/// back from an <see cref="IColdStore"/> to an <see cref="ISourceStore"/>, verifies it, then removes
/// the pointer. Like <see cref="MigratePipeline"/> it is provider-agnostic and throttle-resilient —
/// a transient failure parks the item for automatic retry (via <see cref="StepFailureHandler"/>) and
/// the write is idempotent under retry (the source adaptor treats a response-lost-but-landed upload
/// as done). The archive is never deleted until the restored content is verified.
/// </summary>
public sealed class RestorePipeline
{
    private readonly ILogger _logger;
    private readonly IJobStatusWriter _statusWriter;
    private readonly ISourceStore _source;
    private readonly IColdStore _cold;
    private readonly StepFailureHandler _failures;

    public RestorePipeline(TransferPipelineOptions options, ILogger logger, IJobStatusWriter statusWriter, ISourceStore source, IColdStore cold)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _cold = cold ?? throw new ArgumentNullException(nameof(cold));
        _failures = new StepFailureHandler(options, logger, statusWriter);
    }

    public async Task<bool> ProcessAsync(RestoreRequest req, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.Validating,
            $"Validating placeholder '{req.Pointer.DisplayPath}'.", cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            // Resume: a prior attempt already restored + recorded the content (RestoredAt set) but
            // hit a transient error before removing the pointer. Finish only the tail — removing the
            // pointer — rather than re-downloading/re-uploading (which would conflict).
            var prior = await _statusWriter.FindItemAsync(req.ItemId, cancellationToken).ConfigureAwait(false);
            if (prior?.RestoredAt is not null && !prior.Status.IsTerminal())
            {
                return await ResumeRemovePointerAsync(req, cancellationToken).ConfigureAwait(false);
            }

            var pointer = await _source.ReadPointerAsync(req.Pointer, cancellationToken).ConfigureAwait(false);
            if (pointer is null)
            {
                await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    "Placeholder not found or its metadata is missing/corrupt; refusing to restore.",
                    level: LogLevel.Warning, cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }

            // Cross-item concurrency guard: don't let two items restore the same pointer at once.
            if (await _statusWriter.IsRestoreInFlightForOtherItemAsync(req.ItemId, req.Pointer.ItemPath, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Another restore for '{Url}' is already in progress; coalescing this duplicate.", req.Pointer.DisplayPath);
                await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.Cancelled,
                    "Coalesced: another restore for this placeholder is already in progress.",
                    level: LogLevel.Warning, cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            }

            var coldKey = new ColdStorageKey(pointer.ContainerName, pointer.BlobPath);
            var destination = req.Destination ?? new SourceItemRef(pointer.OriginalSiteUrl, pointer.OriginalWebUrl, pointer.OriginalServerRelativeUrl);

            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.RestoreInProgress,
                $"Downloading archive '{coldKey.Container}/{coldKey.Path}'.", cancellationToken: cancellationToken).ConfigureAwait(false);

            // Verify the archive exists before touching SharePoint, and stream it into re-readable
            // content (length verified so a truncated download can't be written back).
            var coldInfo = await _cold.GetInfoAsync(coldKey, cancellationToken).ConfigureAwait(false);
            if (!coldInfo.Exists)
            {
                await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.RestoreFailed,
                    $"The archived object '{coldKey.Container}/{coldKey.Path}' is missing; cannot restore.",
                    level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
                return false;
            }

            string writtenPath;
            await using (var coldStream = await _cold.OpenReadAsync(coldKey, cancellationToken).ConfigureAwait(false))
            await using (var content = await TempFileTransferContent.CopyFromAsync(coldStream, coldInfo.Length, cancellationToken).ConfigureAwait(false))
            {
                writtenPath = await _source.WriteContentAsync(destination, content, req.ConflictBehavior, cancellationToken).ConfigureAwait(false);
            }

            await _statusWriter.RecordRestoredAsync(req.ItemId, writtenPath, cancellationToken).ConfigureAwait(false);

            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.PostRestoreValidation,
                "Verifying restored item.", cancellationToken: cancellationToken).ConfigureAwait(false);
            var restored = await _source.GetItemAsync(new SourceItemRef(destination.StoreUrl, destination.WebUrl, writtenPath), cancellationToken).ConfigureAwait(false);
            if (!restored.Exists)
            {
                throw TransferProviderException.Transient($"Restored item '{writtenPath}' not found after upload.", _source.ProviderId);
            }

            // Verified restore: only now may we optionally delete the archive (mirrors the migrate
            // invariant — never delete a copy before the other side is confirmed). Best-effort.
            if (req.DeleteColdAfterRestore)
            {
                try
                {
                    var deleted = await _cold.DeleteIfExistsAsync(coldKey, cancellationToken).ConfigureAwait(false);
                    await _statusWriter.LogAsync(req.JobId, req.ItemId, MigrationLifecycleStatus.PostRestoreValidation, LogLevel.Information,
                        deleted ? $"Archive '{coldKey.Container}/{coldKey.Path}' deleted after verified restore." : "Archive was already absent at cleanup.",
                        null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Post-restore archive cleanup failed for '{Container}/{Path}'; leaving it in place.", coldKey.Container, coldKey.Path);
                }
            }

            return await RemovePointerAndCompleteAsync(req, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed for placeholder '{Url}'.", req.Pointer.DisplayPath);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.RestoreFailed, $"Restore failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Resume tail: the content is already restored; just remove the pointer and complete.</summary>
    private async Task<bool> ResumeRemovePointerAsync(RestoreRequest req, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Item {ItemId}: content already restored by a prior attempt; resuming to remove the placeholder only.", req.ItemId);
        return await RemovePointerAndCompleteAsync(req, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RemovePointerAndCompleteAsync(RestoreRequest req, CancellationToken cancellationToken)
    {
        try
        {
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.PlaceholderRemoving,
                "Removing placeholder.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await _source.RemovePointerAsync(req.Pointer, cancellationToken).ConfigureAwait(false);
            await _statusWriter.TransitionAsync(req.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                "Restore completed; placeholder removed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // The content is restored; only removing the pointer failed. Park it for auto-retry (the
            // resume path finishes the removal) instead of leaving a stuck failure.
            _logger.LogWarning(ex, "Restored item but failed to remove placeholder for item {ItemId}.", req.ItemId);
            await _failures.HandleAsync(req.ItemId, ex, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                $"Restored the item, but removing the placeholder failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}

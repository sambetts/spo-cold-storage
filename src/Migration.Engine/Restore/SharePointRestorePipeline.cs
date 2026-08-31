using Azure;
using Azure.Storage.Blobs;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Migration.Engine.Utils;
using Microsoft.SharePoint.Client;
using Models.ColdStorage;
using System.Text;
using IOFile = System.IO.File;

namespace Migration.Engine.Restore;

/// <summary>
/// End-to-end restore pipeline. Reads the placeholder content from
/// SharePoint, downloads the matching blob from cold storage, uploads it back
/// to SharePoint at the original (or fallback) location, then removes or
/// updates the placeholder.
/// </summary>
public sealed class SharePointRestorePipeline : BaseComponent
{
    private readonly IJobStatusWriter _statusWriter;
    private readonly bool _deleteBlobAfterRestore;
    private readonly VersionHistoryArchiver _versionArchiver;

    public SharePointRestorePipeline(
        Config config,
        ILogger logger,
        IJobStatusWriter statusWriter) : base(config, logger)
    {
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _deleteBlobAfterRestore = config.ColdStorageDeleteBlobAfterRestore > 0;
        // Always constructed: restore must replay whatever history the ARCHIVE holds,
        // regardless of whether capture happens to be switched on right now. An archive
        // written while preservation was enabled is still restorable after it's turned
        // off, and the sidecars must still be cleaned up as one unit (issue #64).
        _versionArchiver = new VersionHistoryArchiver(config, logger);
    }

    /// <summary>
    /// Replays archived prior versions onto the destination before the current content
    /// is uploaded, so the destination rebuilds its history with the archived current
    /// version last (issue #66). Returns how many were replayed (0 when the archive has
    /// no version history). Best-effort — never throws.
    /// </summary>
    private async Task<int> ReplayArchivedVersionsAsync(
        ClientContext spCtx, string containerName, string? baseBlobPath, string destinationUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(baseBlobPath))
        {
            return 0;
        }
        return await _versionArchiver.ReplayAsync(spCtx, destinationUrl, baseBlobPath, containerName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ProcessAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.RestoreTarget is null)
        {
            throw new ArgumentException("Restore envelope must include RestoreTarget payload.", nameof(envelope));
        }

        var target = envelope.RestoreTarget;

        // Blob-driven restore: the cold-storage blob is the source of truth, so restore straight
        // from it — the SharePoint placeholder and the database row are both optional. This is what
        // lets an archive be restored even when its placeholder or migration record is missing
        // (an "orphaned" archive), and keeps the DB an audit log rather than the authority.
        if (target.IsBlobDriven)
        {
            return await ProcessBlobDrivenAsync(envelope, target, cancellationToken).ConfigureAwait(false);
        }

        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Validating,
            $"Validating placeholder '{target.PlaceholderServerRelativeUrl}'.", cancellationToken: cancellationToken);

        ClientContext? spCtx = null;
        string? tempFile = null;
        try
        {
            spCtx = await AuthUtils.GetClientContext(_config, target.SiteUrl, _logger, null).ConfigureAwait(false);

            // Resume: a prior attempt already uploaded + recorded the restored content (RestoredAt
            // set) but hit a transient error before removing the placeholder. Re-running the full
            // download/upload would collide with the file we already restored, so finish the tail
            // only. This keeps throttle retries safe once the upload has succeeded.
            var prior = await _statusWriter.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (prior?.RestoredAt is not null && !prior.Status.IsTerminal())
            {
                return await ResumeRemovePlaceholderAsync(envelope, spCtx, target, cancellationToken).ConfigureAwait(false);
            }

            var placeholderFile = spCtx.Web.GetFileByServerRelativeUrl(target.PlaceholderServerRelativeUrl);
            spCtx.Load(placeholderFile, f => f.Exists, f => f.ServerRelativeUrl, f => f.ListItemAllFields);
            await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            if (!placeholderFile.Exists)
            {
                // A missing placeholder is NOT automatically a failure. The normal end state of a
                // SUCCESSFUL restore is exactly this: the file is back and the placeholder was
                // removed. Re-running such an item (e.g. a duplicate row from re-submitting the
                // same folder, or an admin "Recover failed") would otherwise fail forever with
                // "Placeholder not found" and could never clear. Check the destination first: if
                // the real file is sitting there, the work is already done — mark it Skipped.
                var destination = target.OriginalServerRelativeUrl;
                if (!string.IsNullOrEmpty(destination)
                    && await GetFileLengthOrNullAsync(spCtx, destination, cancellationToken).ConfigureAwait(false) is not null)
                {
                    _logger.LogInformation(
                        "Item {ItemId}: placeholder '{Placeholder}' is gone and '{Dest}' is present — already restored; marking Skipped.",
                        envelope.ItemId, target.PlaceholderServerRelativeUrl, destination);
                    await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Skipped,
                        "Already restored — the file is back in SharePoint and its placeholder has been removed; nothing to do.",
                        cancellationToken: cancellationToken);
                    return true;
                }
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    "Placeholder not found in SharePoint, and no file is present at the original location to restore to.",
                    level: LogLevel.Warning, cancellationToken: cancellationToken);
                return false;
            }

            var content = await ReadFileContentAsStringAsync(spCtx, placeholderFile, cancellationToken).ConfigureAwait(false);
            var metadata = PlaceholderFileMetadata.TryParse(content);
            if (metadata is null)
            {
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    "Placeholder metadata is missing or corrupted; refusing to restore.",
                    level: LogLevel.Error, cancellationToken: cancellationToken);
                return false;
            }

            // The `.url` placeholder is an ordinary SharePoint file, so anyone who can edit the
            // library can rewrite these coordinates. Never let them redirect the restore: the
            // container must be the one the caller was ACL-checked against, and the archive must
            // have originated inside the site this job was authorized for. (Blob-driven restores
            // don't come through here — they use the trusted persisted key.)
            if (!string.IsNullOrEmpty(envelope.ContainerName)
                && !string.Equals(metadata.ContainerName, envelope.ContainerName, StringComparison.OrdinalIgnoreCase))
            {
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    $"Placeholder names cold-storage container '{metadata.ContainerName}', but this restore was authorized for '{envelope.ContainerName}'; refusing.",
                    level: LogLevel.Error, cancellationToken: cancellationToken);
                return false;
            }
            if (!SharePointScope.IsBlobKeyWithinSite(target.SiteUrl, metadata.BlobPath))
            {
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    $"Placeholder points at archive '{metadata.BlobPath}', which originates outside '{target.SiteUrl}'; refusing.",
                    level: LogLevel.Error, cancellationToken: cancellationToken);
                return false;
            }

            // Concurrency guard (issue #10): if another item is already actively
            // restoring this same placeholder, coalesce this duplicate (no-op) so we
            // don't double-upload or fight over the destination. Checked before we
            // claim RestoreInProgress to avoid two duplicates cancelling each other.
            if (await _statusWriter.IsRestoreInFlightForOtherItemAsync(envelope.ItemId, target.PlaceholderServerRelativeUrl, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Another restore for '{Url}' is already in progress; coalescing this duplicate.", target.PlaceholderServerRelativeUrl);
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Cancelled,
                    "Coalesced: another restore for this placeholder is already in progress.",
                    level: LogLevel.Warning, cancellationToken: cancellationToken);
                return true;
            }

            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreInProgress,
                $"Downloading blob '{metadata.ContainerName}/{metadata.BlobPath}'.", cancellationToken: cancellationToken);

            tempFile = Path.Combine(Path.GetTempPath(), "SpoColdStorageRestore", Guid.NewGuid().ToString("N") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);

            await DownloadBlobToTempAsync(metadata.ContainerName, metadata.BlobPath, tempFile, cancellationToken).ConfigureAwait(false);

            var destinationUrl = target.OriginalServerRelativeUrl
                                 ?? metadata.OriginalServerRelativeUrl;
            var destinationFolder = GetParentFolder(destinationUrl);

            // Conflict handling per envelope.ConflictBehavior.
            destinationUrl = await ResolveConflictAsync(spCtx, destinationUrl, envelope.ConflictBehavior, envelope.JobId, envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (destinationUrl is null)
            {
                return false;
            }

            // Replay archived prior versions FIRST (oldest-first) so the current
            // content uploaded next becomes the latest version, rebuilding history
            // (issue #18). Best-effort; never fails the restore.
            var replayedVersions = 0;
            if (metadata.VersionCount > 0)
            {
                replayedVersions = await ReplayArchivedVersionsAsync(
                    spCtx, metadata.ContainerName, metadata.BlobPath, destinationUrl, cancellationToken).ConfigureAwait(false);
            }

            using (var fs = IOFile.OpenRead(tempFile))
            {
                var folder = await EnsureDestinationFolderAsync(spCtx, destinationFolder, cancellationToken).ConfigureAwait(false);

                var restoredLength = new FileInfo(tempFile).Length;
                var addInfo = new FileCreationInformation
                {
                    ContentStream = fs,
                    Url = Path.GetFileName(destinationUrl),
                    // Overwrite when explicitly requested, or when we just replayed
                    // versions onto the destination (the current content is the latest).
                    Overwrite = envelope.ConflictBehavior == ConflictBehavior.Overwrite || replayedVersions > 0,
                };
                try
                {
                    var uploaded = folder.Files.Add(addInfo);
                    spCtx.Load(uploaded, f => f.ServerRelativeUrl);
                    await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                    destinationUrl = uploaded.ServerRelativeUrl;
                }
                catch (Exception ex) when (TransientErrorClassifier.IsTransient(ex))
                {
                    // The upload request may have reached SharePoint even though the RESPONSE was
                    // lost to a transient I/O hiccup. If the file is now present at the destination
                    // with the expected byte length, the upload actually succeeded — continue rather
                    // than re-uploading (which would hit a conflict on the next retry). Otherwise
                    // rethrow so the item parks for a clean full retry (the destination is still empty).
                    var existingLength = await GetFileLengthOrNullAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);
                    if (existingLength != restoredLength)
                    {
                        throw;
                    }
                    _logger.LogWarning(ex, "Upload response for '{Url}' was lost, but the file is present with the expected size ({Len} bytes); treating as uploaded.", destinationUrl, restoredLength);
                }
            }

            await _statusWriter.RecordRestoredAsync(envelope.ItemId, destinationUrl, cancellationToken);

            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PostRestoreValidation,
                "Verifying restored file in SharePoint.", cancellationToken: cancellationToken);
            await VerifyRestoredAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);

            // Verified post-restore cleanup (issue #4): only now that the restored
            // file is confirmed present do we optionally delete the cold-storage
            // blob, so the file never lives in both places. Mirrors the migrate
            // invariant (never delete a copy until the other side is verified); a
            // delete failure is logged but never fails the restore.
            if (_deleteBlobAfterRestore)
            {
                await TryDeleteBlobAsync(envelope.JobId, envelope.ItemId, metadata.ContainerName, metadata.BlobPath, cancellationToken).ConfigureAwait(false);
            }

            // Remove placeholder
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderRemoving,
                "Removing placeholder.", cancellationToken: cancellationToken);
            try
            {
                placeholderFile.DeleteObject();
                await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                    "Restore completed; placeholder removed.", cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // The content is restored and verified; only the placeholder removal failed. Under a
                // throttle storm that's usually transient — park it for an automatic retry (the resume
                // path finishes the placeholder removal) instead of leaving a stuck PlaceholderRemoveFailed.
                _logger.LogWarning(ex, "Restored file but failed to remove placeholder.");
                await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                    $"Restored the file, but removing the placeholder failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed for placeholder '{Url}'.", target.PlaceholderServerRelativeUrl);
            await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.RestoreFailed,
                $"Restore failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            spCtx?.Dispose();
            if (tempFile is not null)
            {
                try { IOFile.Delete(tempFile); } catch (IOException ex) { _logger.LogDebug(ex, "Temp delete failed."); }
            }
        }
    }

    /// <summary>
    /// Blob-driven restore: pushes a cold-storage blob back to its original SharePoint location
    /// using the blob as the source of truth. Unlike the legacy path it does NOT read a placeholder
    /// to discover the pointer (the envelope already carries the blob key + destination) and the
    /// placeholder is only cleaned up afterwards <b>if it still exists</b> — so an orphaned archive
    /// (blob present, placeholder and/or DB row missing) is fully restorable. Same download →
    /// conflict-resolve → upload → verify → (optional blob delete) → complete steps, with the same
    /// throttle parking + resume-tail safety as the placeholder-driven path.
    /// </summary>
    private async Task<bool> ProcessBlobDrivenAsync(
        ColdStorageBusEnvelope envelope,
        PlaceholderRestoreTarget target,
        CancellationToken cancellationToken)
    {
        var destinationUrl = target.OriginalServerRelativeUrl!;

        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Validating,
            $"Validating archive '{envelope.ContainerName}/{target.BlobPath}' for restore to '{destinationUrl}'.",
            cancellationToken: cancellationToken);

        ClientContext? spCtx = null;
        string? tempFile = null;
        try
        {
            spCtx = await AuthUtils.GetClientContext(_config, target.SiteUrl, _logger, null).ConfigureAwait(false);

            // Resume tail: a prior attempt already restored + recorded the content but hit a
            // transient error before removing the placeholder. Finish the tail only.
            var prior = await _statusWriter.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (prior?.RestoredAt is not null && !prior.Status.IsTerminal())
            {
                return await ResumeRemovePlaceholderAsync(envelope, spCtx, target, cancellationToken).ConfigureAwait(false);
            }

            // Idempotent lift-and-shift: if the destination already holds the archived file, the
            // restore already happened, so re-restoring the same folder is a no-op rather than a
            // conflict failure. Skip the copy, clean up any leftover placeholder, and mark Skipped.
            // Note this is an *inferred* restore (matched on size, or the archive is already gone) —
            // not one this run copied and verified — so the archive is deliberately NOT deleted here.
            var alreadyPresentLength = await GetFileLengthOrNullAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);
            if (alreadyPresentLength is long present)
            {
                var archivedLength = await GetBlobLengthAsync(envelope.ContainerName, target.BlobPath!, cancellationToken).ConfigureAwait(false);
                if (archivedLength is null)
                {
                    // Archive already deleted by an earlier verified restore of this file: the copy
                    // is back in SharePoint and there is nothing left to pull. Skip, don't fail.
                    _logger.LogInformation("Item {ItemId}: '{Dest}' is present and the archive is already gone; treating as already restored.", envelope.ItemId, destinationUrl);
                    return await SkipAlreadyRestoredAsync(envelope, spCtx, target, cancellationToken).ConfigureAwait(false);
                }
                if (archivedLength == present)
                {
                    _logger.LogInformation("Item {ItemId}: '{Dest}' already present at the archived size ({Len} bytes); skipping the copy (already restored).", envelope.ItemId, destinationUrl, present);
                    return await SkipAlreadyRestoredAsync(envelope, spCtx, target, cancellationToken).ConfigureAwait(false);
                }
            }

            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreInProgress,
                $"Downloading blob '{envelope.ContainerName}/{target.BlobPath}'.", cancellationToken: cancellationToken);

            tempFile = Path.Combine(Path.GetTempPath(), "SpoColdStorageRestore", Guid.NewGuid().ToString("N") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            await DownloadBlobToTempAsync(envelope.ContainerName, target.BlobPath!, tempFile, cancellationToken).ConfigureAwait(false);

            var resolvedDestination = await ResolveConflictAsync(spCtx, destinationUrl, envelope.ConflictBehavior, envelope.JobId, envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (resolvedDestination is null)
            {
                return false; // conflict + Fail: ResolveConflictAsync already transitioned to RestoreFailed
            }
            destinationUrl = resolvedDestination;

            var destinationFolder = GetParentFolder(destinationUrl);
            var restoredLength = new FileInfo(tempFile).Length;

            // Replay archived prior versions FIRST (oldest-first) so the current content
            // uploaded next lands as the latest version, rebuilding history (issue #66).
            // The blob-driven path is the production restore route, so without this the
            // feature was inert even when capture was enabled.
            var replayedVersions = await ReplayArchivedVersionsAsync(
                spCtx, envelope.ContainerName, target.BlobPath, destinationUrl, cancellationToken).ConfigureAwait(false);

            using (var fs = IOFile.OpenRead(tempFile))
            {
                var folder = await EnsureDestinationFolderAsync(spCtx, destinationFolder, cancellationToken).ConfigureAwait(false);

                var addInfo = new FileCreationInformation
                {
                    ContentStream = fs,
                    Url = Path.GetFileName(destinationUrl),
                    // Overwrite when explicitly requested, or when we just replayed versions
                    // onto the destination (the current content must land on top of them).
                    Overwrite = envelope.ConflictBehavior == ConflictBehavior.Overwrite || replayedVersions > 0,
                };
                try
                {
                    var uploaded = folder.Files.Add(addInfo);
                    spCtx.Load(uploaded, f => f.ServerRelativeUrl);
                    await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                    destinationUrl = uploaded.ServerRelativeUrl;
                }
                catch (Exception ex) when (TransientErrorClassifier.IsTransient(ex))
                {
                    // Response-lost-but-landed: if the file is now present with the expected length
                    // the upload succeeded despite the lost response — continue rather than re-uploading.
                    var existingLength = await GetFileLengthOrNullAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);
                    if (existingLength != restoredLength)
                    {
                        throw;
                    }
                    _logger.LogWarning(ex, "Upload response for '{Url}' was lost, but the file is present with the expected size ({Len} bytes); treating as uploaded.", destinationUrl, restoredLength);
                }
            }

            await _statusWriter.RecordRestoredAsync(envelope.ItemId, destinationUrl, cancellationToken);

            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PostRestoreValidation,
                "Verifying restored file in SharePoint.", cancellationToken: cancellationToken);
            await VerifyRestoredAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);

            // Only after the restored file is verified do we optionally delete the archive blob.
            if (_deleteBlobAfterRestore)
            {
                await TryDeleteBlobAsync(envelope.JobId, envelope.ItemId, envelope.ContainerName, target.BlobPath!, cancellationToken).ConfigureAwait(false);
            }

            // Remove the placeholder — but only if one still exists (an orphaned archive has none).
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderRemoving,
                "Removing placeholder if present.", cancellationToken: cancellationToken);
            try
            {
                if (!string.IsNullOrEmpty(target.PlaceholderServerRelativeUrl))
                {
                    await RemovePlaceholderIfPresentAsync(spCtx, target.PlaceholderServerRelativeUrl, cancellationToken).ConfigureAwait(false);
                }
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                    "Restore completed from cold storage.", cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // File is restored + verified; only placeholder removal failed. Park for the resume tail.
                _logger.LogWarning(ex, "Restored file but failed to remove placeholder '{Url}'.", target.PlaceholderServerRelativeUrl);
                await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                    $"Restored the file, but removing the placeholder failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blob-driven restore failed for '{Container}/{Blob}'.", envelope.ContainerName, target.BlobPath);
            await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.RestoreFailed,
                $"Restore failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            spCtx?.Dispose();
            if (tempFile is not null)
            {
                try { IOFile.Delete(tempFile); } catch (IOException ex) { _logger.LogDebug(ex, "Temp delete failed."); }
            }
        }
    }

    /// <summary>
    /// The destination already holds the archived file, so this restore is a no-op: remove any leftover
    /// placeholder and mark the item Skipped. The archive blob is deliberately <b>retained</b>: unlike
    /// the copy path, this run never downloaded or verified the content — "already restored" is inferred
    /// from the destination matching the archived byte length, and a same-size-but-different file would
    /// otherwise cost us the only copy of the archived data. Deleting the archive stays exclusively on
    /// the verified path (upload → <see cref="VerifyRestoredAsync"/> → delete). A leftover blob is a cost
    /// concern that orphan reconciliation reports; a deleted one is unrecoverable.
    /// A transient placeholder-removal failure parks for an automatic retry, which re-detects the
    /// already-restored state and retries the cleanup.
    /// </summary>
    private async Task<bool> SkipAlreadyRestoredAsync(ColdStorageBusEnvelope envelope, ClientContext spCtx, PlaceholderRestoreTarget target, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrEmpty(target.PlaceholderServerRelativeUrl))
            {
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderRemoving,
                    "Already restored; removing leftover placeholder if present.", cancellationToken: cancellationToken);
                await RemovePlaceholderIfPresentAsync(spCtx, target.PlaceholderServerRelativeUrl, cancellationToken).ConfigureAwait(false);
            }
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Skipped,
                "Already restored — a file of the archived size is already in SharePoint; skipped the copy. The archive was left in place because this run did not verify the restored content.",
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Already-restored item {ItemId}: leftover placeholder removal failed.", envelope.ItemId);
            await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                $"The file is already restored, but removing the leftover placeholder failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Removes a placeholder when it exists; a missing placeholder is a no-op (an orphaned archive
    /// may have none). Transient failures propagate so the caller can park the item for a retry.
    /// </summary>
    private async Task RemovePlaceholderIfPresentAsync(ClientContext ctx, string placeholderServerRelativeUrl, CancellationToken cancellationToken)
    {
        var ph = ctx.Web.GetFileByServerRelativeUrl(placeholderServerRelativeUrl);
        ctx.Load(ph, f => f.Exists);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (ServerException)
        {
            return; // file-not-found surfaces as ServerException on some tenants — nothing to remove
        }
        if (ph.Exists)
        {
            ph.DeleteObject();
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a restore step failure with the same throttle resilience as the migrate
    /// pipeline (<c>ColdStorageMigratorPipeline.HandleStepFailureAsync</c>): a <b>transient</b>
    /// error (throttle/timeout/transient 5xx) still under the attempt ceiling parks the item in
    /// the non-terminal <see cref="MigrationLifecycleStatus.RetryScheduled"/> status with a
    /// concrete <c>NextRetryAt</c> (from the SharePoint <c>Retry-After</c> header when present,
    /// else the exponential backoff). The message processor then schedules the retry directly on
    /// the bus, so a throttled bulk restore resumes automatically instead of mass-failing. A
    /// permanent error — or a transient one that has exhausted its attempts — transitions to
    /// <paramref name="terminalStatus"/>. A restore never deletes the archived blob before the
    /// upload is verified, so retrying is always safe.
    /// </summary>
    private async Task HandleRestoreFailureAsync(
        Guid itemId,
        Exception ex,
        MigrationLifecycleStatus terminalStatus,
        string terminalMessage,
        CancellationToken cancellationToken)
    {
        if (TransientErrorClassifier.IsTransient(ex))
        {
            var attempts = await _statusWriter.IncrementAttemptsAsync(itemId, cancellationToken).ConfigureAwait(false);
            var maxAttempts = _config.ColdStorageMaxProcessAttempts > 0 ? _config.ColdStorageMaxProcessAttempts : 5;
            if (attempts < maxAttempts)
            {
                var backoffSeconds = ThrottleBackoff.SecondsFor(attempts, _config);
                var retryAfterSeconds = ThrottleInfo.TryGetRetryAfterSeconds(ex);
                var waitSeconds = Math.Clamp(Math.Max(backoffSeconds, retryAfterSeconds ?? 0), 1, 3600);
                var nextRetryUtc = DateTime.UtcNow.AddSeconds(waitSeconds);
                var reason = retryAfterSeconds is int ra
                    ? $"SharePoint or Azure is busy and throttled the request (asked to wait {ra}s). It will be retried automatically at {nextRetryUtc:HH:mm:ss} UTC (attempt {attempts + 1} of {maxAttempts})."
                    : $"Throttled or hit a transient error; it will be retried automatically at {nextRetryUtc:HH:mm:ss} UTC (attempt {attempts + 1} of {maxAttempts}).";
                await _statusWriter.ScheduleRetryAsync(itemId, nextRetryUtc, retryAfterSeconds, reason, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
            await _statusWriter.TransitionAsync(itemId, terminalStatus,
                $"Gave up after {attempts} throttled/transient attempts. {terminalMessage}",
                exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        await _statusWriter.TransitionAsync(itemId, terminalStatus, terminalMessage,
            exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume path for an item whose content was already restored (RestoredAt set) by a prior
    /// attempt that then hit a transient error before removing the placeholder. Finishes just the
    /// tail — remove the placeholder and mark completed — rather than re-downloading/re-uploading
    /// (which would collide with the file we already restored). Throttle-safe: a transient failure
    /// here parks for another automatic retry.
    /// </summary>
    private async Task<bool> ResumeRemovePlaceholderAsync(
        ColdStorageBusEnvelope envelope,
        ClientContext spCtx,
        PlaceholderRestoreTarget target,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Item {ItemId}: content already restored by a prior attempt; resuming to remove the placeholder only.", envelope.ItemId);
        try
        {
            if (string.IsNullOrEmpty(target.PlaceholderServerRelativeUrl))
            {
                // Blob-driven orphan: nothing to remove, the file is already restored.
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                    "Restore completed (no placeholder to remove).", cancellationToken: cancellationToken);
                return true;
            }
            var placeholderFile = spCtx.Web.GetFileByServerRelativeUrl(target.PlaceholderServerRelativeUrl);
            spCtx.Load(placeholderFile, f => f.Exists);
            await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            if (placeholderFile.Exists)
            {
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderRemoving,
                    "Resuming: removing placeholder after a prior successful restore.", cancellationToken: cancellationToken);
                placeholderFile.DeleteObject();
                await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            }
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                "Restore completed; placeholder removed (resumed after a transient failure).", cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resume: failed to remove placeholder for item {ItemId}.", envelope.ItemId);
            await HandleRestoreFailureAsync(envelope.ItemId, ex, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                $"The file is restored, but removing the placeholder failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Break-glass restore (issue #6): pushes a cold-storage blob straight back to
    /// a target SharePoint location WITHOUT reading or requiring a placeholder and
    /// WITHOUT going through the bus queue. For admins when normal self-service
    /// restore can't run. Reuses the same download → conflict-resolve → upload →
    /// verify steps as the queued path, with full lifecycle/audit logging.
    /// </summary>
    public async Task<bool> ForceRestoreFromBlobAsync(
        Guid jobId,
        Guid itemId,
        string siteUrl,
        string containerName,
        string blobPath,
        string targetServerRelativeUrl,
        ConflictBehavior conflictBehavior,
        string? placeholderServerRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(siteUrl) || string.IsNullOrEmpty(containerName)
            || string.IsNullOrEmpty(blobPath) || string.IsNullOrEmpty(targetServerRelativeUrl))
        {
            throw new ArgumentException("siteUrl, containerName, blobPath and targetServerRelativeUrl are all required.");
        }

        await _statusWriter.TransitionAsync(itemId, MigrationLifecycleStatus.RestoreInProgress,
            $"Break-glass restore of '{containerName}/{blobPath}' to '{targetServerRelativeUrl}'.", cancellationToken: cancellationToken);

        ClientContext? spCtx = null;
        string? tempFile = null;
        try
        {
            spCtx = await AuthUtils.GetClientContext(_config, siteUrl, _logger, null).ConfigureAwait(false);

            tempFile = Path.Combine(Path.GetTempPath(), "SpoColdStorageRestore", Guid.NewGuid().ToString("N") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            await DownloadBlobToTempAsync(containerName, blobPath, tempFile, cancellationToken).ConfigureAwait(false);

            var destinationFolder = GetParentFolder(targetServerRelativeUrl);
            var destinationUrl = await ResolveConflictAsync(spCtx, targetServerRelativeUrl, conflictBehavior, jobId, itemId, cancellationToken).ConfigureAwait(false);
            if (destinationUrl is null)
            {
                return false; // ResolveConflictAsync already transitioned to RestoreFailed
            }

            using (var fs = IOFile.OpenRead(tempFile))
            {
                var folder = await EnsureDestinationFolderAsync(spCtx, destinationFolder, cancellationToken).ConfigureAwait(false);

                var addInfo = new FileCreationInformation
                {
                    ContentStream = fs,
                    Url = Path.GetFileName(destinationUrl),
                    Overwrite = conflictBehavior == ConflictBehavior.Overwrite,
                };
                var uploaded = folder.Files.Add(addInfo);
                spCtx.Load(uploaded, f => f.ServerRelativeUrl);
                await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                destinationUrl = uploaded.ServerRelativeUrl;
            }

            await _statusWriter.RecordRestoredAsync(itemId, destinationUrl, cancellationToken);
            await _statusWriter.TransitionAsync(itemId, MigrationLifecycleStatus.PostRestoreValidation,
                "Verifying restored file in SharePoint.", cancellationToken: cancellationToken);
            await VerifyRestoredAsync(spCtx, destinationUrl, cancellationToken).ConfigureAwait(false);

            if (_deleteBlobAfterRestore)
            {
                await TryDeleteBlobAsync(jobId, itemId, containerName, blobPath, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(placeholderServerRelativeUrl))
            {
                await TryRemovePlaceholderAsync(spCtx, placeholderServerRelativeUrl, cancellationToken).ConfigureAwait(false);
            }

            await _statusWriter.TransitionAsync(itemId, MigrationLifecycleStatus.RestoreCompleted,
                "Break-glass restore completed.", cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Break-glass restore failed for '{Container}/{Path}'.", containerName, blobPath);
            await _statusWriter.TransitionAsync(itemId, MigrationLifecycleStatus.RestoreFailed,
                $"Break-glass restore failed: {ex.Message}", exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken);
            return false;
        }
        finally
        {
            spCtx?.Dispose();
            if (tempFile is not null)
            {
                try { IOFile.Delete(tempFile); } catch (IOException ex) { _logger.LogDebug(ex, "Temp delete failed."); }
            }
        }
    }

    private async Task TryRemovePlaceholderAsync(ClientContext ctx, string placeholderServerRelativeUrl, CancellationToken cancellationToken)
    {
        try
        {
            var ph = ctx.Web.GetFileByServerRelativeUrl(placeholderServerRelativeUrl);
            ctx.Load(ph, f => f.Exists);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            if (ph.Exists)
            {
                ph.DeleteObject();
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Break-glass restore: could not remove placeholder '{Url}'.", placeholderServerRelativeUrl);
        }
    }

    private async Task<string> ReadFileContentAsStringAsync(ClientContext ctx, Microsoft.SharePoint.Client.File spFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streamResult = spFile.OpenBinaryStream();
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await streamResult.Value.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task DownloadBlobToTempAsync(string containerName, string blobPath, string tempFile, CancellationToken cancellationToken)
    {
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        var blob = serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);

        try
        {
            await blob.DownloadToAsync(tempFile, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException(
                $"Cold-storage blob '{containerName}/{blobPath}' not found.", ex);
        }
    }

    /// <summary>
    /// Returns the archived blob's content length, or null when the blob is missing. Used by the
    /// blob-driven path to detect a file that is already restored (destination present at the same
    /// size) so a re-run of a folder restore skips it instead of failing on a conflict.
    /// </summary>
    private async Task<long?> GetBlobLengthAsync(string containerName, string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
            var blob = serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
            var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return props.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the cold-storage archive after a verified restore (issue #4). The archive
    /// is ONE unit: the base blob plus any version-history sidecars and manifest
    /// (issue #64) — deleting only the base blob leaves the sidecars orphaned forever,
    /// since nothing else references them.
    /// Best-effort: a failure is logged + audited but never fails the restore,
    /// since the file is already safely back in SharePoint and a leftover blob is
    /// only a cost concern (the orphan-reconciliation job will catch it).
    /// </summary>
    private async Task TryDeleteBlobAsync(Guid jobId, Guid itemId, string containerName, string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
            var blob = serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
            var deleted = await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // Remove the version sidecars with the base blob so the archive unit goes away
            // together. Runs unconditionally: an archive may hold history captured while
            // preservation was enabled even if it is switched off now.
            var sidecars = await _versionArchiver
                .DeleteVersionArtifactsAsync(containerName, blobPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Post-restore cleanup: blob '{Container}/{Path}' {Outcome}; {Sidecars} version artifact(s) removed.",
                containerName, blobPath, deleted.Value ? "deleted" : "already absent", sidecars);
            await _statusWriter.LogAsync(jobId, itemId, MigrationLifecycleStatus.RestoreCompleted,
                LogLevel.Information,
                (deleted.Value
                    ? $"Cold-storage blob '{containerName}/{blobPath}' deleted after verified restore."
                    : $"Cold-storage blob '{containerName}/{blobPath}' was already absent at cleanup.")
                + (sidecars > 0 ? $" Also removed {sidecars} version-history artifact(s)." : string.Empty),
                null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Post-restore blob cleanup failed for '{Container}/{Path}'; leaving blob in place.", containerName, blobPath);
            await _statusWriter.LogAsync(jobId, itemId, MigrationLifecycleStatus.PostRestoreValidation,
                LogLevel.Warning,
                $"Post-restore blob cleanup failed for '{containerName}/{blobPath}': {ex.Message}",
                ex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Conflict handling for restore. Returns the chosen destination URL or
    /// null when the caller asked to fail and a conflict exists.
    /// </summary>
    private async Task<string?> ResolveConflictAsync(
        ClientContext ctx,
        string destinationUrl,
        ConflictBehavior behavior,
        Guid jobId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var existing = ctx.Web.GetFileByServerRelativeUrl(destinationUrl);
        ctx.Load(existing, f => f.Exists);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        catch (ServerException)
        {
            return destinationUrl;
        }

        if (!existing.Exists)
        {
            return destinationUrl;
        }

        switch (behavior)
        {
            case ConflictBehavior.Overwrite:
                return destinationUrl;
            case ConflictBehavior.Rename:
                var renamed = RenameToAvoidConflict(destinationUrl);
                await _statusWriter.LogAsync(jobId, itemId, MigrationLifecycleStatus.RestoreInProgress,
                    LogLevel.Information, $"Conflict at '{destinationUrl}', restoring as '{renamed}'.", null, cancellationToken);
                return renamed;
            default:
                await _statusWriter.TransitionAsync(itemId, MigrationLifecycleStatus.RestoreFailed,
                    $"Conflict at '{destinationUrl}' and conflict behavior = Fail.", level: LogLevel.Warning,
                    cancellationToken: cancellationToken);
                return null;
        }
    }

    private static string RenameToAvoidConflict(string destinationUrl)
    {
        var folder = GetParentFolder(destinationUrl);
        var name = Path.GetFileNameWithoutExtension(destinationUrl);
        var ext = Path.GetExtension(destinationUrl);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"{folder}/{name}.restored-{stamp}{ext}";
    }

    private async Task VerifyRestoredAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        ctx.Load(file, f => f.Exists, f => f.Length);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        if (!file.Exists)
        {
            throw new InvalidOperationException("Restored file not found after upload: " + serverRelativeUrl);
        }
    }

    /// <summary>
    /// Returns the byte length of a SharePoint file, or null if it doesn't exist. Used to detect
    /// an upload whose response was lost but whose bytes actually landed (same length), so a retry
    /// doesn't re-upload into a conflict.
    /// </summary>
    private async Task<long?> GetFileLengthOrNullAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken)
    {
        try
        {
            var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
            ctx.Load(file, f => f.Exists, f => f.Length);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            return file.Exists ? file.Length : null;
        }
        catch (ServerException)
        {
            // File-not-found surfaces as a ServerException on some tenants — treat as absent.
            return null;
        }
    }

    /// <summary>
    /// Returns the destination folder, creating it (and any missing ancestors) when it no longer
    /// exists. A restore frequently targets a folder that was itself removed — e.g. the whole
    /// folder was archived, or the user deleted the tree after archiving. Without this,
    /// <c>GetFolderByServerRelativeUrl</c> throws <c>ServerException: File Not Found</c> and the
    /// item fails permanently even though the archived content is perfectly restorable.
    /// Walks down from the document library root so each level is created in order.
    /// </summary>
    private async Task<Folder> EnsureDestinationFolderAsync(ClientContext ctx, string folderServerRelativeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
        ctx.Load(folder);
        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
            return folder;
        }
        catch (ServerException)
        {
            // Not there — fall through and build the path.
        }

        _logger.LogInformation("Destination folder '{Folder}' is missing; recreating it for the restore.", folderServerRelativeUrl);

        var segments = folderServerRelativeUrl.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /sites/<site>/<library> is the shallowest thing we can attach to; anything above that
        // is site plumbing we must never try to create.
        var rootDepth = segments.Length >= 4 && segments[0].Equals("sites", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        if (segments.Length <= rootDepth)
        {
            throw new InvalidOperationException($"Cannot recreate '{folderServerRelativeUrl}': it is above the document-library root.");
        }

        var currentPath = "/" + string.Join('/', segments.Take(rootDepth));
        var current = ctx.Web.GetFolderByServerRelativeUrl(currentPath);
        ctx.Load(current);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

        for (var i = rootDepth; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentPath += "/" + segments[i];
            var next = ctx.Web.GetFolderByServerRelativeUrl(currentPath);
            ctx.Load(next);
            try
            {
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                current = next;
                continue;
            }
            catch (ServerException)
            {
                // Missing level — create it.
            }
            current = current.Folders.Add(segments[i]);
            ctx.Load(current);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
        }
        return current;
    }

    private static string GetParentFolder(string serverRelativeUrl)    {
        var idx = serverRelativeUrl.LastIndexOf('/');
        if (idx <= 0)
        {
            throw new ArgumentException("Cannot derive parent folder from URL: " + serverRelativeUrl, nameof(serverRelativeUrl));
        }
        return serverRelativeUrl[..idx];
    }
}

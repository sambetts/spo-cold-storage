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
    private readonly VersionHistoryArchiver? _versionArchiver;

    public SharePointRestorePipeline(
        Config config,
        ILogger logger,
        IJobStatusWriter statusWriter) : base(config, logger)
    {
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _deleteBlobAfterRestore = config.ColdStorageDeleteBlobAfterRestore > 0;
        _versionArchiver = config.ColdStorageCaptureVersionHistory > 0
            ? new VersionHistoryArchiver(config, logger)
            : null;
    }

    public async Task<bool> ProcessAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.RestoreTarget is null)
        {
            throw new ArgumentException("Restore envelope must include RestoreTarget payload.", nameof(envelope));
        }

        var target = envelope.RestoreTarget;

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
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    "Placeholder not found in SharePoint.", level: LogLevel.Warning, cancellationToken: cancellationToken);
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
            if (_versionArchiver is not null && metadata.VersionCount > 0)
            {
                replayedVersions = await _versionArchiver.ReplayAsync(
                    spCtx, destinationUrl, metadata.BlobPath, metadata.ContainerName, cancellationToken).ConfigureAwait(false);
            }

            using (var fs = IOFile.OpenRead(tempFile))
            {
                var folder = spCtx.Web.GetFolderByServerRelativeUrl(destinationFolder);
                spCtx.Load(folder);
                await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

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
                var folder = spCtx.Web.GetFolderByServerRelativeUrl(destinationFolder);
                spCtx.Load(folder);
                await spCtx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

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
    /// Deletes the cold-storage blob after a verified restore (issue #4).
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

            _logger.LogInformation("Post-restore cleanup: blob '{Container}/{Path}' {Outcome}.",
                containerName, blobPath, deleted.Value ? "deleted" : "already absent");
            await _statusWriter.LogAsync(jobId, itemId, MigrationLifecycleStatus.RestoreCompleted,
                LogLevel.Information,
                deleted.Value
                    ? $"Cold-storage blob '{containerName}/{blobPath}' deleted after verified restore."
                    : $"Cold-storage blob '{containerName}/{blobPath}' was already absent at cleanup.",
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

    private static string GetParentFolder(string serverRelativeUrl)
    {
        var idx = serverRelativeUrl.LastIndexOf('/');
        if (idx <= 0)
        {
            throw new ArgumentException("Cannot derive parent folder from URL: " + serverRelativeUrl, nameof(serverRelativeUrl));
        }
        return serverRelativeUrl[..idx];
    }
}

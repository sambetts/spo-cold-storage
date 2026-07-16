using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Migration.Engine.Utils;
using Microsoft.SharePoint.Client;
using Models;
using Models.ColdStorage;
using System.Globalization;
using IOFile = System.IO.File;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Migration.Engine.Migration;

/// <summary>
/// End-to-end migrate-one-item pipeline driven from a service-bus message.
/// Strictly enforces the requirements' "source must not be deleted on
/// failure" guarantee by gating each destructive step on a prior successful
/// validation step.
/// </summary>
public sealed class ColdStorageMigratorPipeline : BaseComponent
{
    private readonly IJobStatusWriter _statusWriter;
    private readonly SharePointPlaceholderWriter _placeholderWriter;
    private readonly BlobStorageUploader _blobUploader;
    private readonly IArchiveEligibilityEvaluator _eligibility;
    private readonly IArchiveHoldDetector? _holdDetector;
    private readonly VersionHistoryArchiver? _versionArchiver;

    public ColdStorageMigratorPipeline(
        Config config,
        ILogger logger,
        IJobStatusWriter statusWriter) : base(config, logger)
    {
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _placeholderWriter = new SharePointPlaceholderWriter(logger);
        _blobUploader = new BlobStorageUploader(config, logger);
        _eligibility = new ArchiveEligibilityEvaluator(
            config,
            new DbArchiveExclusionSource(config, logger),
            new DbFileReadActivitySource(config, logger));
        // Only stand up the (per-item, CSOM round-trip) hold detector when enabled.
        _holdDetector = config.ColdStorageSkipRetentionLabeled > 0
            ? new RetentionLabelHoldDetector(logger)
            : null;
        _versionArchiver = config.ColdStorageCaptureVersionHistory > 0
            ? new VersionHistoryArchiver(config, logger)
            : null;
    }

    /// <summary>
    /// Executes the full lifecycle for one file. Returns true if the file was
    /// migrated end-to-end (or short-circuited because it had been migrated
    /// already). Returns false if any step failed and the message should be
    /// abandoned for retry.
    /// </summary>
    public async Task<bool> ProcessAsync(
        ColdStorageBusEnvelope envelope,
        IConfidentialClientApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(app);
        if (envelope.File is null)
        {
            throw new ArgumentException("Migrate envelope must include File payload.", nameof(envelope));
        }

        var file = envelope.File;

        // FAILSAFE / resumability: if a previous attempt already copied AND deleted
        // the source but didn't finish (e.g., the worker crashed before the
        // placeholder was written), do NOT re-download a source that no longer
        // exists — that would fail and wrongly mark an already-archived item as a
        // copy failure. Instead resume by recreating the placeholder from the
        // persisted blob coordinates.
        var priorItem = await _statusWriter.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
        if (priorItem is not null && priorItem.SourceDeletedAt is not null && !priorItem.Status.IsTerminal())
        {
            return await ResumeAfterSourceDeletedAsync(envelope, priorItem, cancellationToken).ConfigureAwait(false);
        }

        // --- Validating ----------------------------------------------------
        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Validating,
            $"Validating '{file.ServerRelativeFilePath}'.", cancellationToken: cancellationToken);

        if (!file.IsValidInfo)
        {
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                "Invalid SharePoint file info.", level: LogLevel.Warning, cancellationToken: cancellationToken);
            return false;
        }

        // --- Eligibility gate (issue #2) ----------------------------------
        // Authoritative choke point: even if an ineligible item slips past the
        // submit-time filter, it is never copied/deleted here. Skipped is a
        // terminal, source-intact outcome (handled, so the message completes).
        var eligibility = await _eligibility.EvaluateAsync(new ArchiveCandidate
        {
            ServerRelativeUrl = file.ServerRelativeFilePath,
            SiteUrl = file.SiteUrl,
            WebUrl = file.WebUrl,
            FileSizeBytes = file.FileSize,
            LastModified = file.LastModified,
            DriveId = file.DriveId,
            GraphItemId = file.GraphItemId,
        }, cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsEligible)
        {
            _logger.LogInformation("Skipping '{Url}': {Reason}", file.FullSharePointUrl, eligibility.SkipReason);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Skipped,
                $"Skipped: {eligibility.SkipReason}", level: LogLevel.Information, cancellationToken: cancellationToken);
            return true;
        }

        // --- Compliance-hold gate (issue #15) -----------------------------
        // Never copy content under a retention/legal-hold label out to Azure.
        // Runs before any download so held content never leaves SharePoint.
        if (_holdDetector is not null)
        {
            using var holdCtx = await AuthUtils.GetClientContext(_config, file.SiteUrl, _logger, null).ConfigureAwait(false);
            var hold = await _holdDetector.CheckAsync(holdCtx, file.ServerRelativeFilePath, cancellationToken).ConfigureAwait(false);
            if (hold.IsOnHold)
            {
                _logger.LogInformation("Skipping '{Url}': {Reason}", file.FullSharePointUrl, hold.Reason);
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.Skipped,
                    $"Skipped: {hold.Reason}", level: LogLevel.Information, cancellationToken: cancellationToken);
                return true;
            }
        }

        // --- MigrationInProgress: download + upload -----------------------
        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.MigrationInProgress,
            "Starting download from SharePoint.", cancellationToken: cancellationToken);

        var blobContainerName = envelope.ContainerName;
        // Collision-safe key: encode the SharePoint host (tenant) + server-relative
        // path so same-named files in different sites/tenants never overwrite each
        // other in cold storage. The key is persisted on the item + placeholder and
        // read back verbatim at restore time, so this stays backward compatible.
        var blobPath = ColdStorageBlobKey.Build(file.SiteUrl, file.ServerRelativeFilePath);
        string? tempFile = null;
        long size;
        string md5Base64;
        try
        {
            var downloader = new SharePointFileDownloader(app, _config, _logger);
            (tempFile, size) = await downloader.DownloadFileToTempDir(file).ConfigureAwait(false);
            md5Base64 = ComputeMd5Base64(tempFile);

            var containerClient = GetContainerClient(blobContainerName);
            await UploadToBlobAsync(
                containerClient, tempFile, blobPath, envelope, md5Base64, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // CRITICAL: source absolutely must not be deleted on upload failure.
            // We never even reach the delete step on this code path.
            _logger.LogError(ex, "Copy to cold storage failed for '{Url}'.", file.FullSharePointUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                $"Copy failed: {ex.Message}", exception: ex, level: LogLevel.Error,
                cancellationToken: cancellationToken);
            CleanupTempFile(tempFile);
            return false;
        }

        CleanupTempFile(tempFile);

        // --- Copied / PostCopyValidation ---------------------------------
        var blobUrl = BuildBlobUrl(blobContainerName, blobPath);
        await _statusWriter.RecordCopySuccessAsync(envelope.ItemId, blobContainerName, blobPath, blobUrl, md5Base64, cancellationToken);
        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PostCopyValidation,
            "Verifying blob in cold storage.", cancellationToken: cancellationToken);

        try
        {
            await VerifyBlobAsync(blobContainerName, blobPath, size, md5Base64, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Post-copy validation failed - again, do not delete the source.
            _logger.LogError(ex, "Post-copy validation failed for '{Url}'.", file.FullSharePointUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                $"Post-copy validation failed: {ex.Message}", exception: ex, level: LogLevel.Error,
                cancellationToken: cancellationToken);
            return false;
        }

        // --- DeletePending (source delete) -------------------------------
        await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.DeletePending,
            "Removing source file from SharePoint.", cancellationToken: cancellationToken);

        // Original authorship captured from the live item before it is deleted so
        // it survives onto the placeholder + restore (issue #1).
        string? originalCreatedBy = null;
        string? originalModifiedBy = null;
        DateTime? originalCreated = null;
        DateTime? originalModified = null;
        var capturedVersionCount = 0;

        ClientContext? spCtx = null;
        try
        {
            spCtx = await AuthUtils.GetClientContext(_config, file.SiteUrl, _logger, null).ConfigureAwait(false);
            var spFile = spCtx.Web.GetFileByServerRelativeUrl(file.ServerRelativeFilePath);
            spCtx.Load(spFile, f => f.Exists, f => f.Length, f => f.CheckOutType, f => f.ListItemAllFields);
            await spCtx.ExecuteQueryAsync().ConfigureAwait(false);

            if (!spFile.Exists)
            {
                _logger.LogWarning("Source file '{Url}' no longer exists; treating as already migrated.", file.FullSharePointUrl);
            }
            else
            {
                // FAILSAFE: the source must still be byte-for-byte what we archived.
                // If its current length differs from the copied size, the file was
                // changed after we copied it (or the copy was short) — never delete
                // on a mismatch; fail so the (now-stale) copy is retried.
                if (spFile.Length != size)
                {
                    _logger.LogError("Source '{Url}' length {Actual} no longer matches the archived copy ({Copied}); refusing to delete.",
                        file.FullSharePointUrl, spFile.Length, size);
                    await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                        $"Source length {spFile.Length} no longer matches the archived copy ({size}); not deleting.",
                        level: LogLevel.Error, cancellationToken: cancellationToken);
                    return false;
                }

                CaptureSourceAuthorship(spFile, ref originalCreatedBy, ref originalModifiedBy, ref originalCreated, ref originalModified);

                // Capture prior version history to cold storage BEFORE the source
                // is deleted (issue #18). Best-effort; never fails the migration.
                if (_versionArchiver is not null)
                {
                    capturedVersionCount = await _versionArchiver.CaptureAsync(
                        spCtx, file.ServerRelativeFilePath, blobPath, blobContainerName, cancellationToken).ConfigureAwait(false);
                }

                if (spFile.CheckOutType != CheckOutType.None)
                {
                    await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.DeleteFailed,
                        $"Source file '{file.ServerRelativeFilePath}' is checked out and cannot be deleted.",
                        level: LogLevel.Warning, cancellationToken: cancellationToken);
                    return false;
                }

                // FAILSAFE: re-read the item's persisted status and refuse to delete
                // unless the lifecycle explicitly permits it. Guards against a
                // reordered pipeline and against a concurrent cancel/state change
                // between copy and delete.
                var itemNow = await _statusWriter.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
                if (itemNow is null || !itemNow.Status.SourceDeleteAllowed())
                {
                    _logger.LogError("Refusing to delete source '{Url}': item status {Status} does not permit deletion.",
                        file.FullSharePointUrl, itemNow?.Status);
                    await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.CopyToColdStorageFailed,
                        "Source delete blocked by lifecycle guard (status did not permit deletion).",
                        level: LogLevel.Error, cancellationToken: cancellationToken);
                    return false;
                }

                spFile.DeleteObject();
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);
            }
            await _statusWriter.RecordSourceMetadataAsync(envelope.ItemId, originalCreatedBy, originalModifiedBy, originalCreated, originalModified, cancellationToken);
            await _statusWriter.RecordSourceDeletedAsync(envelope.ItemId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete source file '{Url}'.", file.FullSharePointUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.DeleteFailed,
                $"Failed to delete source: {ex.Message}", exception: ex, level: LogLevel.Error,
                cancellationToken: cancellationToken);
            return false;
        }

        // --- PlaceholderCreating ----------------------------------------
        try
        {
            var metadata = new PlaceholderFileMetadata
            {
                OriginalSiteUrl = file.SiteUrl,
                OriginalWebUrl = file.WebUrl,
                OriginalServerRelativeUrl = file.ServerRelativeFilePath,
                OriginalFileName = Path.GetFileName(file.ServerRelativeFilePath),
                OriginalFileSize = size,
                OriginalLastModified = originalModified ?? file.LastModified,
                OriginalCreatedBy = originalCreatedBy ?? string.Empty,
                OriginalModifiedBy = originalModifiedBy ?? string.Empty,
                OriginalCreated = originalCreated ?? (file.CreatedDate ?? DateTime.MinValue),
                ContainerName = blobContainerName,
                BlobPath = blobPath,
                BlobUrl = blobUrl,
                ContentMd5Base64 = md5Base64,
                MigratedAt = DateTime.UtcNow,
                JobId = envelope.JobId,
                VersionCount = capturedVersionCount,
            };

            var placeholderUrl = await _placeholderWriter.WritePlaceholderAsync(
                spCtx!, file.ServerRelativeFilePath, metadata, cancellationToken,
                // If the deployment configured a public app base URL, point the placeholder
                // at our SPA download route instead of the raw blob URL — see the comment on
                // Config.AppBaseUrl for why. The SPA route handles MSAL auth + ACL check +
                // redirect to a short-lived blob SAS so end users get a working download
                // even when storage public network access is locked down by policy.
                userFacingUrl: BuildPlaceholderUserFacingUrl(envelope.ItemId)).ConfigureAwait(false);

            // Best-effort: stamp the original authorship onto visible placeholder
            // columns so the audit trail isn't lost (issue #1). Never fails the migration.
            await _placeholderWriter.StampOriginalMetadataAsync(spCtx!, placeholderUrl, metadata, cancellationToken).ConfigureAwait(false);

            await _statusWriter.RecordPlaceholderCreatedAsync(envelope.ItemId, placeholderUrl, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create placeholder for '{Url}'.", file.FullSharePointUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                $"Failed to create placeholder: {ex.Message}", exception: ex, level: LogLevel.Error,
                cancellationToken: cancellationToken);
            return false;
        }
        finally
        {
            spCtx?.Dispose();
        }
    }

    /// <summary>
    /// Recovery path for an item whose source was already deleted by a prior
    /// attempt that didn't finish. Verifies the archived blob still exists and
    /// (re)creates the .url placeholder from the item's persisted coordinates,
    /// rather than re-downloading a source that no longer exists.
    /// </summary>
    private async Task<bool> ResumeAfterSourceDeletedAsync(ColdStorageBusEnvelope envelope, MigrationJobItem item, CancellationToken cancellationToken)
    {
        var file = envelope.File!;
        _logger.LogWarning("Item {ItemId} ('{Url}') already had its source deleted in a prior run; resuming without re-downloading.",
            envelope.ItemId, file.FullSharePointUrl);

        if (string.IsNullOrEmpty(item.BlobContainerName) || string.IsNullOrEmpty(item.BlobPath))
        {
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                "Resume failed: source was deleted but the archived blob coordinates are missing.",
                level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The archived copy MUST still exist before we finalise — otherwise both
        // the source and the copy are gone and we must surface that loudly.
        var blob = GetContainerClient(item.BlobContainerName).GetBlobClient(item.BlobPath);
        if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Resume for item {ItemId}: source is deleted AND the archived blob '{Container}/{Path}' is missing.",
                envelope.ItemId, item.BlobContainerName, item.BlobPath);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                "Resume failed: the archived blob is missing. Manual recovery required.",
                level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Placeholder already written by the prior run — nothing left to do.
        if (item.PlaceholderCreatedAt is not null && !string.IsNullOrEmpty(item.PlaceholderServerRelativeUrl))
        {
            _logger.LogInformation("Item {ItemId} already has a placeholder; treating as complete.", envelope.ItemId);
            return true;
        }

        ClientContext? spCtx = null;
        try
        {
            var metadata = new PlaceholderFileMetadata
            {
                OriginalSiteUrl = file.SiteUrl,
                OriginalWebUrl = file.WebUrl,
                OriginalServerRelativeUrl = file.ServerRelativeFilePath,
                OriginalFileName = Path.GetFileName(file.ServerRelativeFilePath),
                OriginalFileSize = item.FileSize,
                OriginalLastModified = item.SourceLastModified ?? file.LastModified,
                OriginalCreatedBy = item.OriginalCreatedBy ?? string.Empty,
                OriginalModifiedBy = item.OriginalModifiedBy ?? string.Empty,
                OriginalCreated = item.OriginalCreated ?? (file.CreatedDate ?? DateTime.MinValue),
                ContainerName = item.BlobContainerName,
                BlobPath = item.BlobPath,
                BlobUrl = item.BlobUrl ?? BuildBlobUrl(item.BlobContainerName, item.BlobPath),
                ContentMd5Base64 = item.ContentMd5Base64 ?? string.Empty,
                MigratedAt = item.CopiedAt ?? DateTime.UtcNow,
                JobId = envelope.JobId,
            };

            spCtx = await AuthUtils.GetClientContext(_config, file.SiteUrl, _logger, null).ConfigureAwait(false);
            var placeholderUrl = await _placeholderWriter.WritePlaceholderAsync(
                spCtx, file.ServerRelativeFilePath, metadata, cancellationToken,
                userFacingUrl: BuildPlaceholderUserFacingUrl(envelope.ItemId)).ConfigureAwait(false);
            await _placeholderWriter.StampOriginalMetadataAsync(spCtx, placeholderUrl, metadata, cancellationToken).ConfigureAwait(false);
            await _statusWriter.RecordPlaceholderCreatedAsync(envelope.ItemId, placeholderUrl, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resume: failed to recreate placeholder for '{Url}'.", file.FullSharePointUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderFailed,
                $"Resume: failed to recreate placeholder: {ex.Message}", exception: ex, level: LogLevel.Error,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            spCtx?.Dispose();
        }
    }

    private BlobContainerClient GetContainerClient(string containerName)
    {
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        return serviceClient.GetBlobContainerClient(containerName);
    }

    /// <summary>
    /// Reads the original Created By / Modified By / Created / Modified values
    /// from the live list item before it is deleted. Best-effort: any read
    /// failure leaves the out values null and is ignored, so capturing the
    /// authorship trail can never block a migration.
    /// </summary>
    private static void CaptureSourceAuthorship(
        Microsoft.SharePoint.Client.File spFile,
        ref string? createdBy,
        ref string? modifiedBy,
        ref DateTime? created,
        ref DateTime? modified)
    {
        try
        {
            var li = spFile.ListItemAllFields;
            if (li is null)
            {
                return;
            }
            createdBy = (li["Author"] as FieldUserValue)?.LookupValue;
            modifiedBy = (li["Editor"] as FieldUserValue)?.LookupValue;
            if (li["Created"] is DateTime c)
            {
                created = c;
            }
            if (li["Modified"] is DateTime m)
            {
                modified = m;
            }
        }
        catch (Exception)
        {
            // Best-effort capture; never block the migration on a metadata read.
        }
    }

    /// <summary>
    /// Compose the URL that gets written into the placeholder .url file's
    /// <c>[InternetShortcut].URL=</c> line. When the deployment has configured
    /// AppBaseUrl this routes through our SPA's <c>/cold-storage/download/{itemId}</c>
    /// page; otherwise we return null so the writer falls back to the raw
    /// blob URL (legacy behaviour, only suitable for dev).
    /// </summary>
    private string? BuildPlaceholderUserFacingUrl(Guid itemId)
    {
        if (string.IsNullOrWhiteSpace(_config.AppBaseUrl))
        {
            return null;
        }
        return $"{_config.AppBaseUrl.TrimEnd('/')}/cold-storage/download/{itemId}";
    }

    private async Task UploadToBlobAsync(
        BlobContainerClient containerClient,
        string tempFile,
        string blobPath,
        ColdStorageBusEnvelope envelope,
        string md5Base64,
        CancellationToken cancellationToken)
    {
        await containerClient.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
        var blob = containerClient.GetBlobClient(blobPath);

        using var fs = IOFile.OpenRead(tempFile);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BlobMetadataKeys.MigrationJobId] = envelope.JobId.ToString(),
            [BlobMetadataKeys.RequestedByUpn] = envelope.RequestedByUpn,
            [BlobMetadataKeys.OriginalServerRelativeUrl] = envelope.File!.ServerRelativeFilePath,
            [BlobMetadataKeys.OriginalSiteUrl] = envelope.File.SiteUrl,
            [BlobMetadataKeys.OriginalWebUrl] = envelope.File.WebUrl,
            [BlobMetadataKeys.OriginalFileName] = Path.GetFileName(envelope.File.ServerRelativeFilePath),
            [BlobMetadataKeys.OriginalLastModifiedUtc] = envelope.File.LastModified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            [BlobMetadataKeys.SourceContentMd5] = md5Base64,
        };
        var options = new BlobUploadOptions
        {
            Metadata = metadata,
            // FAILSAFE: give Azure the expected MD5 so it validates the uploaded
            // bytes server-side, and so VerifyBlobAsync can compare the blob's
            // ContentHash (which would otherwise be null for chunked uploads).
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders
            {
                ContentHash = Convert.FromBase64String(md5Base64),
            },
        };
        await blob.UploadAsync(fs, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyBlobAsync(string containerName, string blobPath, long expectedSize, string expectedMd5Base64, CancellationToken cancellationToken)
    {
        var containerClient = GetContainerClient(containerName);
        var blob = containerClient.GetBlobClient(blobPath);
        var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (props.Value.ContentLength != expectedSize)
        {
            throw new InvalidOperationException(
                $"Blob '{containerName}/{blobPath}' length {props.Value.ContentLength} does not match expected {expectedSize}.");
        }
        if (props.Value.ContentHash is { Length: > 0 } hash)
        {
            var actualMd5 = Convert.ToBase64String(hash);
            if (!string.Equals(actualMd5, expectedMd5Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Blob '{containerName}/{blobPath}' MD5 mismatch (expected {expectedMd5Base64}, got {actualMd5}).");
            }
        }
    }

    private static string ComputeMd5Base64(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fs = IOFile.OpenRead(filePath);
        return Convert.ToBase64String(md5.ComputeHash(fs));
    }

    private string BuildBlobUrl(string containerName, string blobPath)
    {
        try
        {
            var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
            return serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath).Uri.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not derive blob URL for {Container}/{Path}.", containerName, blobPath);
            return $"{containerName}/{blobPath}";
        }
    }

    private void CleanupTempFile(string? tempFile)
    {
        if (tempFile is null)
        {
            return;
        }
        try
        {
            IOFile.Delete(tempFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp file '{File}'.", tempFile);
        }
    }
}

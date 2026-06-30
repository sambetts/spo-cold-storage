using Azure;
using Azure.Storage.Blobs;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
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

    public SharePointRestorePipeline(
        Config config,
        ILogger logger,
        IJobStatusWriter statusWriter) : base(config, logger)
    {
        _statusWriter = statusWriter ?? throw new ArgumentNullException(nameof(statusWriter));
        _deleteBlobAfterRestore = config.ColdStorageDeleteBlobAfterRestore > 0;
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

            var placeholderFile = spCtx.Web.GetFileByServerRelativeUrl(target.PlaceholderServerRelativeUrl);
            spCtx.Load(placeholderFile, f => f.Exists, f => f.ServerRelativeUrl, f => f.ListItemAllFields);
            await spCtx.ExecuteQueryAsync().ConfigureAwait(false);
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

            using (var fs = IOFile.OpenRead(tempFile))
            {
                var folder = spCtx.Web.GetFolderByServerRelativeUrl(destinationFolder);
                spCtx.Load(folder);
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);

                var addInfo = new FileCreationInformation
                {
                    ContentStream = fs,
                    Url = Path.GetFileName(destinationUrl),
                    Overwrite = envelope.ConflictBehavior == ConflictBehavior.Overwrite,
                };
                var uploaded = folder.Files.Add(addInfo);
                spCtx.Load(uploaded, f => f.ServerRelativeUrl);
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);
                destinationUrl = uploaded.ServerRelativeUrl;
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
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreCompleted,
                    "Restore completed; placeholder removed.", cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Restored file but failed to remove placeholder.");
                await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.PlaceholderRemoveFailed,
                    $"Restored file but could not remove placeholder: {ex.Message}", exception: ex,
                    level: LogLevel.Warning, cancellationToken: cancellationToken);
                // Restored content is intact; treat as warning, not failure.
                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed for placeholder '{Url}'.", target.PlaceholderServerRelativeUrl);
            await _statusWriter.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.RestoreFailed,
                $"Restore failed: {ex.Message}", exception: ex, level: LogLevel.Error, cancellationToken: cancellationToken);
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
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);

                var addInfo = new FileCreationInformation
                {
                    ContentStream = fs,
                    Url = Path.GetFileName(destinationUrl),
                    Overwrite = conflictBehavior == ConflictBehavior.Overwrite,
                };
                var uploaded = folder.Files.Add(addInfo);
                spCtx.Load(uploaded, f => f.ServerRelativeUrl);
                await spCtx.ExecuteQueryAsync().ConfigureAwait(false);
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
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            if (ph.Exists)
            {
                ph.DeleteObject();
                await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Break-glass restore: could not remove placeholder '{Url}'.", placeholderServerRelativeUrl);
        }
    }

    private static async Task<string> ReadFileContentAsStringAsync(ClientContext ctx, Microsoft.SharePoint.Client.File spFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streamResult = spFile.OpenBinaryStream();
        await ctx.ExecuteQueryAsync().ConfigureAwait(false);
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
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
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

    private static async Task VerifyRestoredAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        ctx.Load(file, f => f.Exists, f => f.Length);
        await ctx.ExecuteQueryAsync().ConfigureAwait(false);
        if (!file.Exists)
        {
            throw new InvalidOperationException("Restored file not found after upload: " + serverRelativeUrl);
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

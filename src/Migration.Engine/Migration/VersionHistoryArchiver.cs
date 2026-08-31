using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;
using Microsoft.SharePoint.Client;
using Models.ColdStorage;
using System.Security.Cryptography;
using System.Text;
using IOFile = System.IO.File;

namespace Migration.Engine.Migration;

/// <summary>
/// Outcome of a version-history capture. Capture is <b>fail-closed</b> when
/// preservation is enabled: the caller must not delete the SharePoint source
/// unless <see cref="Success"/> is true, or history the product promised to keep
/// would be destroyed (issue #66).
/// </summary>
public sealed record VersionCaptureResult(bool Success, int Count, string? FailureReason, Exception? Failure)
{
    public static VersionCaptureResult Ok(int count) => new(true, count, null, null);

    public static VersionCaptureResult Failed(string reason, Exception? ex = null) => new(false, 0, reason, ex);
}

/// <summary>
/// Captures and replays a file's SharePoint version history to/from cold storage
/// (issues #18, #66). Prior versions are stored as individual blobs under the
/// <see cref="VersionBlobLayout"/> keys plus a JSON manifest sidecar; on restore
/// they are replayed oldest-first so the destination rebuilds its history and the
/// archived current version (uploaded last by the caller) stays latest.
///
/// <para>
/// <b>Capture is validated and fail-closed.</b> Every prior version is hashed while
/// it streams, written with that hash, then re-read from the blob to confirm length +
/// MD5 before the manifest is marked complete. Any failure returns an unsuccessful
/// <see cref="VersionCaptureResult"/> so the migrate pipeline aborts before the
/// source-delete step.
/// </para>
///
/// <para>
/// <b>Replay is best-effort by design.</b> By then the content is already safely in
/// cold storage and the caller restores the current version regardless, so a partial
/// history is logged, never fatal. SharePoint does not permit setting a version's
/// author or timestamp, so replayed versions are authored by the app identity with new
/// timestamps — the original values are preserved in the manifest for audit. See
/// docs/TECHNICAL.md for the full fidelity limitations.
/// </para>
/// </summary>
public sealed class VersionHistoryArchiver(Config config, ILogger logger)
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Uploads each prior version's content to a versioned blob, validates it, and
    /// writes the manifest sidecar. Call while the source file still exists (before
    /// deletion). A file with no prior versions is a successful capture of 0.
    /// </summary>
    public async Task<VersionCaptureResult> CaptureAsync(
        ClientContext ctx,
        string sourceServerRelativeUrl,
        string baseBlobKey,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var file = ctx.Web.GetFileByServerRelativeUrl(sourceServerRelativeUrl);
            ctx.Load(file.Versions, vs => vs.Include(
                v => v.VersionLabel, v => v.Created, v => v.Url, v => v.IsCurrentVersion,
                v => v.Size, v => v.CheckInComment));
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

            var priorVersions = file.Versions.Where(v => !v.IsCurrentVersion).ToList();
            if (priorVersions.Count == 0)
            {
                return VersionCaptureResult.Ok(0);
            }

            var container = GetContainerClient(containerName);
            var manifest = new VersionManifest
            {
                SchemaVersion = VersionManifest.CurrentSchemaVersion,
                BaseBlobPath = baseBlobKey,
                CapturedAtUtc = DateTime.UtcNow,
            };

            foreach (var version in priorVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var label = version.VersionLabel;
                var versionId = string.IsNullOrEmpty(label) ? version.Url : label;
                var versionKey = VersionBlobLayout.ForVersion(baseBlobKey, versionId);

                var streamResult = version.OpenBinaryStream();
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                if (streamResult?.Value is null)
                {
                    return VersionCaptureResult.Failed(
                        $"SharePoint returned no content stream for version '{versionId}' of '{sourceServerRelativeUrl}'.");
                }

                // Buffer to a temp file rather than memory: a version can be as large as
                // the file itself and the worker processes several items concurrently.
                var temp = Path.Combine(Path.GetTempPath(), "SpoColdStorageVersions", Guid.NewGuid().ToString("N") + ".bin");
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                try
                {
                    long length;
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await streamResult.Value.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                        length = fs.Length;
                    }
                    var md5Base64 = ComputeMd5Base64(temp);

                    using (var upload = IOFile.OpenRead(temp))
                    {
                        await container.GetBlobClient(versionKey).UploadAsync(
                            upload,
                            new BlobUploadOptions
                            {
                                HttpHeaders = new BlobHttpHeaders { ContentHash = Convert.FromBase64String(md5Base64) },
                            },
                            cancellationToken).ConfigureAwait(false);
                    }

                    // Validate the stored blob before we let the caller delete the source.
                    var mismatch = await VerifyVersionBlobAsync(container, versionKey, length, md5Base64, cancellationToken).ConfigureAwait(false);
                    if (mismatch is not null)
                    {
                        return VersionCaptureResult.Failed(
                            $"Validation failed for version '{versionId}' of '{sourceServerRelativeUrl}': {mismatch}");
                    }

                    manifest.Versions.Add(new ArchivedVersion
                    {
                        VersionId = versionId,
                        VersionLabel = label,
                        IsMajor = VersionManifest.IsMajorLabel(label),
                        BlobPath = versionKey,
                        Size = length,
                        LastModifiedUtc = version.Created.ToUniversalTime(),
                        CheckInComment = string.IsNullOrWhiteSpace(version.CheckInComment) ? null : version.CheckInComment,
                        ContentMd5Base64 = md5Base64,
                    });
                }
                finally
                {
                    try { IOFile.Delete(temp); } catch (IOException ex) { _logger.LogDebug(ex, "Temp version file delete failed."); }
                }
            }

            manifest.SortOldestFirst();
            manifest.CaptureComplete = true;

            var manifestKey = VersionBlobLayout.ManifestKey(baseBlobKey);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson());
            using (var manifestStream = new MemoryStream(manifestBytes))
            {
                await container.GetBlobClient(manifestKey).UploadAsync(manifestStream, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Captured + validated {Count} prior version(s) for '{Url}'.", manifest.Count, sourceServerRelativeUrl);
            return VersionCaptureResult.Ok(manifest.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Version-history capture failed for '{Url}'.", sourceServerRelativeUrl);
            return VersionCaptureResult.Failed(ex.Message, ex);
        }
    }

    /// <summary>
    /// Replays archived prior versions onto the restored file, oldest-first, so the
    /// destination rebuilds its version history. The caller restores the current
    /// content separately afterwards (it becomes the latest version). Returns the
    /// number replayed; best-effort — never throws.
    /// </summary>
    public async Task<int> ReplayAsync(
        ClientContext ctx,
        string destinationServerRelativeUrl,
        string baseBlobKey,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manifest = await TryReadManifestAsync(containerName, baseBlobKey, cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.Count == 0)
            {
                return 0;
            }

            // Defensive: a legacy manifest carries no explicit ordering guarantee.
            manifest.SortOldestFirst();

            var folderUrl = destinationServerRelativeUrl[..destinationServerRelativeUrl.LastIndexOf('/')];
            var fileName = Path.GetFileName(destinationServerRelativeUrl);
            var container = GetContainerClient(containerName);
            var folder = ctx.Web.GetFolderByServerRelativeUrl(folderUrl);
            ctx.Load(folder);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);

            var replayed = 0;
            foreach (var version in manifest.Versions) // oldest-first
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blob = container.GetBlobClient(version.BlobPath);
                if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("Version blob '{Path}' missing during replay; skipping that version.", version.BlobPath);
                    continue;
                }

                var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
                using var ms = new MemoryStream(content.Value.Content.ToArray());
                var addInfo = new FileCreationInformation
                {
                    ContentStream = ms,
                    Url = fileName,
                    Overwrite = true,
                };
                folder.Files.Add(addInfo);
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_logger).ConfigureAwait(false);
                replayed++;
            }

            _logger.LogInformation("Replayed {Count} of {Total} prior version(s) onto '{Url}'.",
                replayed, manifest.Count, destinationServerRelativeUrl);
            return replayed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version-history replay failed for '{Url}'; restored current version only.", destinationServerRelativeUrl);
            return 0;
        }
    }

    /// <summary>Reads the manifest sidecar for a base blob, or null when there isn't one.</summary>
    public async Task<VersionManifest?> TryReadManifestAsync(
        string containerName, string baseBlobKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var container = GetContainerClient(containerName);
            var manifestClient = container.GetBlobClient(VersionBlobLayout.ManifestKey(baseBlobKey));
            if (!await manifestClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var download = await manifestClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return VersionManifest.TryParse(download.Value.Content.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the version manifest for '{Container}/{Key}'.", containerName, baseBlobKey);
            return null;
        }
    }

    /// <summary>
    /// Deletes the version sidecars belonging to a base blob — the per-version content
    /// blobs and the manifest — so the archive is removed as ONE unit after a verified
    /// restore instead of leaking orphaned sidecars forever (issue #64).
    /// Best-effort: returns how many blobs were deleted; never throws.
    /// </summary>
    public async Task<int> DeleteVersionArtifactsAsync(
        string containerName, string baseBlobKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var container = GetContainerClient(containerName);
            var deleted = 0;

            // Enumerate rather than trusting the manifest: a partial capture can leave
            // version blobs the manifest never listed, and those would otherwise be
            // orphaned with nothing pointing at them.
            await foreach (var item in container
                .GetBlobsAsync(BlobTraits.None, BlobStates.None, VersionBlobLayout.VersionFolderPrefix(baseBlobKey), cancellationToken)
                .ConfigureAwait(false))
            {
                if (await container.GetBlobClient(item.Name)
                    .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }

            if (await container.GetBlobClient(VersionBlobLayout.ManifestKey(baseBlobKey))
                .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} version-history artifact(s) for '{Container}/{Key}'.", deleted, containerName, baseBlobKey);
            }
            return deleted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete version-history artifacts for '{Container}/{Key}'; they may be left orphaned.", containerName, baseBlobKey);
            return 0;
        }
    }

    /// <summary>
    /// Total bytes held by a base blob's version sidecars, so cost/savings accounting
    /// can treat the archive as one unit. Returns 0 when there are none.
    /// </summary>
    public async Task<long> GetVersionArtifactBytesAsync(
        string containerName, string baseBlobKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var container = GetContainerClient(containerName);
            long total = 0;
            await foreach (var item in container
                .GetBlobsAsync(BlobTraits.None, BlobStates.None, VersionBlobLayout.VersionFolderPrefix(baseBlobKey), cancellationToken)
                .ConfigureAwait(false))
            {
                total += item.Properties.ContentLength ?? 0;
            }
            return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not size version artifacts for '{Container}/{Key}'.", containerName, baseBlobKey);
            return 0;
        }
    }

    /// <summary>Returns null when the stored blob matches, else a description of the mismatch.</summary>
    private static async Task<string?> VerifyVersionBlobAsync(
        BlobContainerClient container, string key, long expectedLength, string expectedMd5Base64, CancellationToken cancellationToken)
    {
        var props = await container.GetBlobClient(key).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (props.Value.ContentLength != expectedLength)
        {
            return $"length {props.Value.ContentLength} != expected {expectedLength}";
        }
        if (props.Value.ContentHash is null)
        {
            return "the stored blob has no content hash to verify against";
        }
        var actual = Convert.ToBase64String(props.Value.ContentHash);
        return string.Equals(actual, expectedMd5Base64, StringComparison.Ordinal)
            ? null
            : $"MD5 {actual} != expected {expectedMd5Base64}";
    }

    private static string ComputeMd5Base64(string path)
    {
        using var md5 = MD5.Create();
        using var stream = IOFile.OpenRead(path);
        return Convert.ToBase64String(md5.ComputeHash(stream));
    }

    private BlobContainerClient GetContainerClient(string containerName)
    {
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        return serviceClient.GetBlobContainerClient(containerName);
    }
}

using Azure.Storage.Blobs;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;
using Microsoft.SharePoint.Client;
using Models.ColdStorage;
using System.Text;

namespace Migration.Engine.Migration;

/// <summary>
/// Captures and replays a file's SharePoint version history to/from cold storage
/// (issue #18). Prior versions are stored as individual blobs under the
/// <see cref="VersionBlobLayout"/> keys plus a JSON manifest sidecar; on restore
/// they are replayed oldest-first so the destination rebuilds its history.
///
/// All operations are BEST-EFFORT: a failure here is logged and swallowed so it
/// never fails the core migrate/restore (the current version is always handled
/// by the main pipeline). Enabled via Config.ColdStorageCaptureVersionHistory.
///
/// NOTE: the CSOM/blob round-trip here has not been exercised against a live
/// tenant in this change; it is structured to match the existing CSOM patterns
/// and guarded so it can't break the primary flow.
/// </summary>
public sealed class VersionHistoryArchiver(Config config, ILogger logger)
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Uploads each prior version's content to a versioned blob and writes the
    /// manifest sidecar. Returns the number of prior versions archived (0 on any
    /// problem). Call while the source file still exists (before deletion).
    /// </summary>
    public async Task<int> CaptureAsync(
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
            ctx.Load(file.Versions, vs => vs.Include(v => v.VersionLabel, v => v.Created, v => v.Url, v => v.IsCurrentVersion));
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            var priorVersions = file.Versions.Where(v => !v.IsCurrentVersion).ToList();
            if (priorVersions.Count == 0)
            {
                return 0;
            }

            var container = GetContainerClient(containerName);
            var manifest = new VersionManifest();

            foreach (var version in priorVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versionId = string.IsNullOrEmpty(version.VersionLabel) ? version.Url : version.VersionLabel;
                var versionKey = VersionBlobLayout.ForVersion(baseBlobKey, versionId);

                var streamResult = version.OpenBinaryStream();
                await ctx.ExecuteQueryAsync().ConfigureAwait(false);
                using var ms = new MemoryStream();
                await streamResult.Value.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;

                await container.GetBlobClient(versionKey).UploadAsync(ms, overwrite: true, cancellationToken).ConfigureAwait(false);

                manifest.Versions.Add(new ArchivedVersion
                {
                    VersionId = versionId,
                    BlobPath = versionKey,
                    Size = ms.Length,
                    LastModifiedUtc = version.Created.ToUniversalTime(),
                });
            }

            // Oldest-first so replay rebuilds history in order.
            manifest.Versions.Sort((a, b) => a.LastModifiedUtc.CompareTo(b.LastModifiedUtc));

            var manifestKey = VersionBlobLayout.ManifestKey(baseBlobKey);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson());
            using var manifestStream = new MemoryStream(manifestBytes);
            await container.GetBlobClient(manifestKey).UploadAsync(manifestStream, overwrite: true, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Captured {Count} prior version(s) for '{Url}'.", manifest.Count, sourceServerRelativeUrl);
            return manifest.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version-history capture failed for '{Url}'; archived current version only.", sourceServerRelativeUrl);
            return 0;
        }
    }

    /// <summary>
    /// Replays archived prior versions onto the restored file, oldest-first, so
    /// the destination rebuilds its version history. The caller restores the
    /// current content separately (it becomes the latest version).
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
            var container = GetContainerClient(containerName);
            var manifestClient = container.GetBlobClient(VersionBlobLayout.ManifestKey(baseBlobKey));
            if (!await manifestClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            var download = await manifestClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            var manifest = VersionManifest.TryParse(download.Value.Content.ToString());
            if (manifest is null || manifest.Count == 0)
            {
                return 0;
            }

            var folderUrl = destinationServerRelativeUrl[..destinationServerRelativeUrl.LastIndexOf('/')];
            var fileName = Path.GetFileName(destinationServerRelativeUrl);
            var folder = ctx.Web.GetFolderByServerRelativeUrl(folderUrl);
            ctx.Load(folder);
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);

            var replayed = 0;
            foreach (var version in manifest.Versions) // already oldest-first
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
                await ctx.ExecuteQueryAsync().ConfigureAwait(false);
                replayed++;
            }

            _logger.LogInformation("Replayed {Count} prior version(s) onto '{Url}'.", replayed, destinationServerRelativeUrl);
            return replayed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version-history replay failed for '{Url}'; restored current version only.", destinationServerRelativeUrl);
            return 0;
        }
    }

    private BlobContainerClient GetContainerClient(string containerName)
    {
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        return serviceClient.GetBlobContainerClient(containerName);
    }
}

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Utils;
using Models.ColdStorage;
using System.Globalization;

namespace Migration.Engine.Providers.AzureBlob;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IColdStore"/>. Wraps the blob operations the
/// migrate/restore pipelines used to perform inline, and — crucially — translates Azure throttling
/// (429) and transient gateway errors (500/503) into a transient <see cref="TransferProviderException"/>
/// so the pipeline's retry policy applies uniformly. Writes are idempotent (overwrite) and send the
/// content MD5 so the service validates the bytes on receipt.
/// </summary>
public sealed class AzureBlobColdStore : IColdStore
{
    private readonly BlobServiceClient _serviceClient;
    private readonly ILogger _logger;

    public AzureBlobColdStore(Config config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceClient = BlobServiceClientFactory.Create(config.ConnectionStrings.Storage, config);
    }

    public string ProviderId => "AzureBlob";

    private BlobClient Blob(ColdStorageKey key) => _serviceClient.GetBlobContainerClient(key.Container).GetBlobClient(key.Path);

    public string GetObjectUrl(ColdStorageKey key) => Blob(key).Uri.ToString();

    public async Task<ColdObjectInfo> GetInfoAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            try
            {
                var props = (await Blob(key).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
                DateTime? archived = null;
                var raw = props.Metadata.FirstOrDefault(kv => string.Equals(kv.Key, BlobMetadataKeys.OriginalLastModifiedUtc, StringComparison.OrdinalIgnoreCase)).Value;
                if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    archived = parsed;
                }
                return new ColdObjectInfo
                {
                    Exists = true,
                    Length = props.ContentLength,
                    ContentMd5Base64 = props.ContentHash is { Length: > 0 } h ? Convert.ToBase64String(h) : null,
                    ArchivedSourceLastModifiedUtc = archived,
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return ColdObjectInfo.Missing;
            }
        }, key).ConfigureAwait(false);
    }

    public async Task WriteAsync(ColdStorageKey key, ITransferContent content, ColdWriteMetadata metadata, CancellationToken cancellationToken = default)
    {
        await GuardAsync(async () =>
        {
            var container = _serviceClient.GetBlobContainerClient(key.Container);
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
            var blob = container.GetBlobClient(key.Path);

            await using var stream = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var options = new BlobUploadOptions
            {
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BlobMetadataKeys.MigrationJobId] = metadata.JobId.ToString(),
                    [BlobMetadataKeys.RequestedByUpn] = metadata.RequestedByUpn ?? string.Empty,
                    [BlobMetadataKeys.OriginalServerRelativeUrl] = metadata.OriginalItemPath,
                    [BlobMetadataKeys.OriginalSiteUrl] = metadata.OriginalStoreUrl,
                    [BlobMetadataKeys.OriginalWebUrl] = metadata.OriginalWebUrl,
                    [BlobMetadataKeys.OriginalFileName] = metadata.OriginalFileName,
                    [BlobMetadataKeys.OriginalLastModifiedUtc] = metadata.SourceLastModifiedUtc.ToString("O", CultureInfo.InvariantCulture),
                    [BlobMetadataKeys.SourceContentMd5] = metadata.ContentMd5Base64,
                },
                // Let Azure validate the bytes on receipt, and make ContentHash available for VerifyAsync.
                HttpHeaders = new BlobHttpHeaders { ContentHash = Convert.FromBase64String(content.ContentMd5Base64) },
            };
            await blob.UploadAsync(stream, options, cancellationToken).ConfigureAwait(false);
            return true;
        }, key).ConfigureAwait(false);
    }

    public async Task VerifyAsync(ColdStorageKey key, long expectedLength, string expectedMd5Base64, CancellationToken cancellationToken = default)
    {
        var props = await GuardAsync(async () => (await Blob(key).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value, key).ConfigureAwait(false);
        if (props.ContentLength != expectedLength)
        {
            throw TransferProviderException.Permanent($"Blob '{key.Container}/{key.Path}' length {props.ContentLength} != expected {expectedLength}.", ProviderId);
        }
        if (props.ContentHash is { Length: > 0 } hash)
        {
            var actual = Convert.ToBase64String(hash);
            if (!string.Equals(actual, expectedMd5Base64, StringComparison.Ordinal))
            {
                throw TransferProviderException.Permanent($"Blob '{key.Container}/{key.Path}' MD5 mismatch (expected {expectedMd5Base64}, got {actual}).", ProviderId);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            try
            {
                return (Stream)await Blob(key).OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw TransferProviderException.Permanent($"Cold object '{key.Container}/{key.Path}' not found.", ProviderId, ex);
            }
        }, key).ConfigureAwait(false);
    }

    public async Task<bool> DeleteIfExistsAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
        => await GuardAsync(async () => (await Blob(key).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value, key).ConfigureAwait(false);

    /// <summary>
    /// Runs a blob operation, translating Azure throttling/transient failures into a transient
    /// <see cref="TransferProviderException"/> (with the server Retry-After when present) so the
    /// pipeline parks + retries them; other failures propagate for the pipeline to classify.
    /// </summary>
    private async Task<T> GuardAsync<T>(Func<Task<T>> action, ColdStorageKey key)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 429 or 500 or 503 or 504)
        {
            var retryAfter = TryGetRetryAfterSeconds(ex);
            _logger.LogWarning(ex, "Azure blob throttled/transient ({Status}) on '{Container}/{Path}'; will retry.", ex.Status, key.Container, key.Path);
            throw new TransferProviderException($"Azure blob {ex.Status} on '{key.Container}/{key.Path}': {ex.Message}", isTransient: true, retryAfter, ProviderId, ex);
        }
    }

    private static int? TryGetRetryAfterSeconds(RequestFailedException ex)
    {
        var response = ex.GetRawResponse();
        if (response is not null && response.Headers.TryGetValue("Retry-After", out var value)
            && ThrottleInfo.TryParseSeconds(value, out var seconds))
        {
            return seconds;
        }
        return null;
    }
}

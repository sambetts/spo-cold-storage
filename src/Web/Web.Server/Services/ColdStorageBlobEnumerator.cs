using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Migration.Engine.Utils;
using Models.ColdStorage;
using System.Runtime.CompilerServices;

namespace Web.Services;

/// <summary>
/// An archived file discovered in cold storage. The blob is the source of truth for a restore:
/// its metadata carries the authoritative original SharePoint location (stamped at migrate time),
/// so an archive can be restored even when its placeholder and/or database row are gone.
/// </summary>
public sealed record ArchivedBlob(
    string BlobPath,
    string OriginalSiteUrl,
    string OriginalWebUrl,
    string OriginalServerRelativeUrl,
    long Length);

/// <summary>
/// Enumerates archived blobs in cold storage so a restore can be driven by what is actually in the
/// blob store rather than by database records (which are only an audit log and can be missing or
/// stale). Isolated behind an interface so the expander stays unit-testable with an in-memory source.
/// </summary>
public interface IColdStorageBlobEnumerator
{
    /// <summary>Enumerates every archived blob under <paramref name="prefix"/> in the container,
    /// resolving each blob's original location from its metadata (falling back to the blob key).</summary>
    IAsyncEnumerable<ArchivedBlob> EnumerateAsync(ColdStorageContainer container, string prefix, CancellationToken cancellationToken = default);

    /// <summary>Resolves a single archived blob by key, or null when it does not exist in the container.</summary>
    Task<ArchivedBlob?> GetAsync(ColdStorageContainer container, string blobPath, CancellationToken cancellationToken = default);
}

public sealed class ColdStorageBlobEnumerator(Config config) : IColdStorageBlobEnumerator
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));

    private BlobContainerClient ContainerClient(ColdStorageContainer container)
    {
        var conn = string.IsNullOrWhiteSpace(container.StorageAccountUri)
            ? _config.ConnectionStrings.Storage
            : container.StorageAccountUri;
        return BlobServiceClientFactory.Create(conn, _config).GetBlobContainerClient(container.BlobContainerName);
    }

    public async IAsyncEnumerable<ArchivedBlob> EnumerateAsync(
        ColdStorageContainer container,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = ContainerClient(container);
        await foreach (var blob in client.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, cancellationToken).ConfigureAwait(false))
        {
            // Skip archived version-history sidecars (".versions/<id>" content + ".versions.json"
            // manifest): they live under the same prefix as real files but are not restorable files
            // — restoring them would create junk (a ".versions.json") or target a non-existent folder.
            if (VersionBlobLayout.IsVersionArtifact(blob.Name))
            {
                continue;
            }
            var archived = ToArchivedBlob(blob.Name, blob.Metadata, blob.Properties?.ContentLength ?? 0);
            if (archived is not null)
            {
                yield return archived;
            }
        }
    }

    public async Task<ArchivedBlob?> GetAsync(ColdStorageContainer container, string blobPath, CancellationToken cancellationToken = default)
    {
        var blob = ContainerClient(container).GetBlobClient(blobPath);
        try
        {
            var props = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            return ToArchivedBlob(blobPath, props.Metadata, props.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="ArchivedBlob"/> from a blob's metadata. The original server-relative URL
    /// comes from <see cref="BlobMetadataKeys.OriginalServerRelativeUrl"/>; when that is absent (a very
    /// old blob) it is derived from the blob key, which is deterministically "{host}/{server-relative
    /// path without leading slash}". Returns null when no destination path can be determined.
    /// </summary>
    internal static ArchivedBlob? ToArchivedBlob(string blobPath, IDictionary<string, string>? metadata, long length)
    {
        metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        metadata.TryGetValue(BlobMetadataKeys.OriginalServerRelativeUrl, out var serverRel);
        metadata.TryGetValue(BlobMetadataKeys.OriginalSiteUrl, out var site);
        metadata.TryGetValue(BlobMetadataKeys.OriginalWebUrl, out var web);

        if (string.IsNullOrEmpty(serverRel))
        {
            serverRel = DeriveServerRelativeFromBlobPath(blobPath);
        }
        if (string.IsNullOrEmpty(serverRel))
        {
            return null;
        }
        return new ArchivedBlob(blobPath, site ?? string.Empty, web ?? string.Empty, serverRel, length);
    }

    /// <summary>Reverses <see cref="ColdStorageBlobKey.Build"/>: a blob key is
    /// "{host}/{server-relative path without leading slash}", so the original server-relative URL is
    /// everything after the first '/' (the host segment), re-prefixed with '/'.</summary>
    internal static string DeriveServerRelativeFromBlobPath(string blobPath)
        => ColdStorageBlobKey.ReverseServerRelativeUrl(blobPath);
}
